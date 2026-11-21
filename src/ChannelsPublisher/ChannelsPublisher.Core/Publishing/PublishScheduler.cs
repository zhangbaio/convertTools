using ChannelsPublisher.Core.Models;

namespace ChannelsPublisher.Core.Publishing;

/// <summary>一个账号的发布作业：目标账号 + 其内嵌浏览器 CDP 端点 + 待发素材列表。</summary>
public sealed record AccountPublishJob(
    PublishAccount Account,
    string CdpEndpoint,
    IReadOnlyList<PublishItem> Items)
{
    public Func<CancellationToken, Task<string>>? ResolveCdpEndpoint { get; init; }
}

/// <summary>并发发布调度器：账号间并行（受 maxParallelAccounts 限制），账号内串行
/// （同一浏览器不能并行上传两条）。这是相对旧版单账号串行的核心增量。</summary>
public sealed class PublishScheduler
{
    private readonly IPublishAutomation _automation;
    private readonly PublishRunStateStore? _runState;
    private readonly bool _scopeSubmissionByAction;

    public PublishScheduler(IPublishAutomation automation, PublishRunStateStore? runState = null,
        bool scopeSubmissionByAction = false)
    {
        _automation = automation;
        _runState = runState;
        _scopeSubmissionByAction = scopeSubmissionByAction;
    }

    public async Task RunAsync(
        IReadOnlyList<AccountPublishJob> jobs,
        FinalAction finalAction,
        int maxParallelAccounts,
        Action<PublishProgress>? onProgress,
        CancellationToken ct,
        bool resume = false)
    {
        using var state = finalAction == FinalAction.None ? null : _runState?.OpenRun();
        maxParallelAccounts = Math.Max(1, maxParallelAccounts);
        using var gate = new SemaphoreSlim(maxParallelAccounts);
        var tasks = new List<Task>(jobs.Count);
        foreach (var job in jobs)
            tasks.Add(RunAccountAsync(job, finalAction, state, resume, gate, onProgress, ct));
        await Task.WhenAll(tasks);
    }

    private async Task RunAccountAsync(
        AccountPublishJob job,
        FinalAction finalAction,
        PublishRunStateStore.Run? state,
        bool resume,
        SemaphoreSlim gate,
        Action<PublishProgress>? onProgress,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            Task<string>? endpointTask = null;
            foreach (var item in job.Items) // 账号内串行
            {
                ct.ThrowIfCancellationRequested();
                var signature = state is null ? "" : PublishRunStateStore.SignatureFor(job.Account.Id, item);
                var status = state?.GetStatus(signature, finalAction);
                var recordKey = _scopeSubmissionByAction
                    ? PublishRunStateStore.ScopedSignature(signature, finalAction)
                    : signature;
                if (status == PublishRunStateStore.Status.Pending)
                {
                    Report(onProgress, job, item, "上次提交结果待确认。请到平台核对发表或草稿记录，避免重复提交。", done: true, ok: false, submissionPending: true);
                    continue;
                }
                if (resume && status == PublishRunStateStore.Status.Completed)
                {
                    Report(onProgress, job, item, "已完成·续传跳过", done: true, ok: true);
                    continue;
                }
                Report(onProgress, job, item, "开始发布", done: false, ok: false);

                var submissionPending = false;
                void RecordPending()
                {
                    state!.Record(recordKey, item.DisplayName, job.Account.Name, pending: true);
                    submissionPending = true;
                }

                PublishResult result;
                try
                {
                    endpointTask ??= job.ResolveCdpEndpoint?.Invoke(ct) ?? Task.FromResult(job.CdpEndpoint);
                    var endpoint = await endpointTask;
                    result = await _automation.PublishAsync(
                        item, endpoint, finalAction,
                        msg => Report(onProgress, job, item, msg, done: false, ok: false),
                        ct,
                        state is null ? null : RecordPending);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ct.ThrowIfCancellationRequested();
                    result = PublishResult.Fail($"{ex.GetType().Name}: {ex.Message}");
                }

                if (submissionPending && result.Ok)
                {
                    try
                    {
                        state!.Record(recordKey, item.DisplayName, job.Account.Name, pending: false);
                    }
                    catch (IOException ex)
                    {
                        Report(onProgress, job, item, $"平台已确认提交。{ex.Message}，请核对平台记录后再处理。", done: true, ok: false, submissionPending: true);
                        throw;
                    }
                }

                var message = submissionPending && !result.Ok
                    ? $"{result.Message}。提交结果待确认，请到平台核对后再处理。"
                    : result.Message;
                Report(onProgress, job, item, message, done: true, ok: result.Ok, submissionPending && !result.Ok);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static void Report(Action<PublishProgress>? onProgress, AccountPublishJob job, PublishItem item, string message, bool done, bool ok, bool submissionPending = false)
        => onProgress?.Invoke(new PublishProgress(job.Account.Id, job.Account.Name, item.DisplayName, message, done, ok)
        {
            Item = item,
            SubmissionPending = submissionPending,
        });
}
