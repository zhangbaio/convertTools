using ChannelsPublisher.Core.Models;

namespace ChannelsPublisher.Core.Publishing;

/// <summary>一个账号的发布作业：目标账号 + 其内嵌浏览器 CDP 端点 + 待发素材列表。</summary>
public sealed record AccountPublishJob(
    PublishAccount Account,
    string CdpEndpoint,
    IReadOnlyList<PublishItem> Items);

/// <summary>并发发布调度器：账号间并行（受 maxParallelAccounts 限制），账号内串行
/// （同一浏览器不能并行上传两条）。这是相对旧版单账号串行的核心增量。</summary>
public sealed class PublishScheduler
{
    private readonly IPublishAutomation _automation;

    public PublishScheduler(IPublishAutomation automation) => _automation = automation;

    public async Task RunAsync(
        IReadOnlyList<AccountPublishJob> jobs,
        FinalAction finalAction,
        int maxParallelAccounts,
        Action<PublishProgress>? onProgress,
        CancellationToken ct)
    {
        maxParallelAccounts = Math.Max(1, maxParallelAccounts);
        using var gate = new SemaphoreSlim(maxParallelAccounts);
        var tasks = new List<Task>(jobs.Count);
        foreach (var job in jobs)
            tasks.Add(RunAccountAsync(job, finalAction, gate, onProgress, ct));
        await Task.WhenAll(tasks);
    }

    private async Task RunAccountAsync(
        AccountPublishJob job,
        FinalAction finalAction,
        SemaphoreSlim gate,
        Action<PublishProgress>? onProgress,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            foreach (var item in job.Items) // 账号内串行
            {
                ct.ThrowIfCancellationRequested();
                Report(onProgress, job, item, "开始发布", done: false, ok: false);

                PublishResult result;
                try
                {
                    result = await _automation.PublishAsync(
                        item, job.CdpEndpoint, finalAction,
                        msg => Report(onProgress, job, item, msg, done: false, ok: false),
                        ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result = PublishResult.Fail($"{ex.GetType().Name}: {ex.Message}");
                }

                Report(onProgress, job, item, result.Message, done: true, ok: result.Ok);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static void Report(Action<PublishProgress>? onProgress, AccountPublishJob job, PublishItem item, string message, bool done, bool ok)
        => onProgress?.Invoke(new PublishProgress(job.Account.Id, job.Account.Name, item.DisplayName, message, done, ok));
}
