using System.Collections.Concurrent;
using PlatformPublisher.Publishing.Distribution;
using PlatformPublisher.Publishing.Models;

namespace PlatformPublisher.Publishing.Execution;

public interface IUnifiedMaterialExecutor
{
    Task<AccountPublishOutcome> ExecuteAccountAsync(
        string batchId,
        AccountPublishPlan plan,
        IProgress<UnifiedPublishProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IPublishBatchStore
{
    void SaveStarted(PublishBatchRequest request, IReadOnlyList<AccountPublishPlan> plans, DateTimeOffset startedAt);
    void SaveAccountOutcome(string batchId, AccountPublishOutcome outcome);
    void SaveFinished(PublishBatchOutcome outcome);
}

public sealed class AccountOperationGate
{
    private readonly ConcurrentDictionary<string,SemaphoreSlim> _gates=new(StringComparer.OrdinalIgnoreCase);
    public async Task<IDisposable> AcquireAsync(string accountId,CancellationToken cancellationToken)
    {
        var gate=_gates.GetOrAdd(accountId,_=>new SemaphoreSlim(1,1));
        if(!await gate.WaitAsync(0,cancellationToken))throw new InvalidOperationException("该账号已有提交型任务正在运行。");
        return new Lease(gate);
    }
    private sealed class Lease(SemaphoreSlim gate):IDisposable{public void Dispose()=>gate.Release();}
}

public sealed class PublishBatchCoordinator
{
    private readonly IUnifiedMaterialExecutor _executor;
    private readonly AccountOperationGate _accountGate;
    private readonly IPublishBatchStore _store;
    public PublishBatchCoordinator(IUnifiedMaterialExecutor executor,AccountOperationGate accountGate,IPublishBatchStore store){_executor=executor;_accountGate=accountGate;_store=store;}

    public async Task<PublishBatchOutcome> ExecuteAsync(PublishBatchRequest request,IProgress<UnifiedPublishProgress>? progress,CancellationToken cancellationToken)
    {
        var plans=MaterialDistributionPlanner.Build(request);var started=DateTimeOffset.UtcNow;_store.SaveStarted(request,plans,started);
        using var groupCts=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);using var concurrency=new SemaphoreSlim(Math.Clamp(request.MaxParallelAccounts,1,8));
        var tasks=plans.Select(async plan=>
        {
            var acquired=false;try
            {
                await concurrency.WaitAsync(groupCts.Token);acquired=true;
                using var lease=await _accountGate.AcquireAsync(plan.Target.AccountId,groupCts.Token);
                var result=await _executor.ExecuteAccountAsync(request.BatchId,plan,progress,groupCts.Token);_store.SaveAccountOutcome(request.BatchId,result);
                if(request.FailurePolicy==PublishFailurePolicy.StopAll&&result.Status is UnifiedPublishItemStatus.Failed or UnifiedPublishItemStatus.SubmissionUnknown)groupCts.Cancel();
                return result;
            }
            catch(OperationCanceledException){var cancelled=new AccountPublishOutcome(plan.Target.AccountId,UnifiedPublishItemStatus.Cancelled,"批次已停止。",[]);_store.SaveAccountOutcome(request.BatchId,cancelled);return cancelled;}
            catch(Exception ex){var failed=new AccountPublishOutcome(plan.Target.AccountId,UnifiedPublishItemStatus.Failed,ex.Message,[]);_store.SaveAccountOutcome(request.BatchId,failed);return failed;}
            finally{if(acquired)concurrency.Release();}
        }).ToArray();
        var accounts=await Task.WhenAll(tasks);var status=Summarize(accounts,request.Draft.Form.FinalAction);var finished=DateTimeOffset.UtcNow;
        var outcome=new PublishBatchOutcome(request.BatchId,status,Message(accounts,status),accounts,started,finished);_store.SaveFinished(outcome);return outcome;
    }

    private static UnifiedPublishItemStatus Summarize(IReadOnlyList<AccountPublishOutcome> values,UnifiedFinalAction action)
    {
        if(values.Any(item=>item.Status==UnifiedPublishItemStatus.SubmissionUnknown))return UnifiedPublishItemStatus.SubmissionUnknown;
        var success=values.Count(item=>item.Status is UnifiedPublishItemStatus.Success or UnifiedPublishItemStatus.DraftSaved);var failed=values.Count-success;
        if(failed==0)return action==UnifiedFinalAction.Draft?UnifiedPublishItemStatus.DraftSaved:UnifiedPublishItemStatus.Success;
        return success>0?UnifiedPublishItemStatus.Failed:values.All(item=>item.Status==UnifiedPublishItemStatus.Cancelled)?UnifiedPublishItemStatus.Cancelled:UnifiedPublishItemStatus.Failed;
    }
    private static string Message(IReadOnlyList<AccountPublishOutcome> values,UnifiedPublishItemStatus status)=>$"批次{status}：账号{values.Count}，成功{values.Count(item=>item.Status is UnifiedPublishItemStatus.Success or UnifiedPublishItemStatus.DraftSaved)}。";
}

public static class PublishRetryPolicy
{
    public static readonly TimeSpan[] Delays=[TimeSpan.Zero,TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(5)];
    public static bool CanRetry(PublishErrorKind kind,int completedAttempts)=>kind==PublishErrorKind.Recoverable&&completedAttempts<Delays.Length;
    public static TimeSpan DelayBeforeAttempt(int attemptNumber)=>attemptNumber<=1?TimeSpan.Zero:Delays[Math.Min(attemptNumber-1,Delays.Length-1)];
}
