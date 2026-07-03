using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Publishing;

public sealed record AccountPublishJob(
    TikTokAccountProfile Account,
    string CdpEndpoint,
    IReadOnlyList<PublishItem> Items);

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
            foreach (var item in job.Items)
            {
                ct.ThrowIfCancellationRequested();
                Report(onProgress, job, item, "开始发布", done: false, ok: false);

                PublishResult result;
                try
                {
                    result = await _automation.PublishAsync(
                        job.Account,
                        item,
                        job.CdpEndpoint,
                        finalAction,
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

    private static void Report(
        Action<PublishProgress>? onProgress,
        AccountPublishJob job,
        PublishItem item,
        string message,
        bool done,
        bool ok)
        => onProgress?.Invoke(new PublishProgress(
            job.Account.Id,
            job.Account.DisplayName,
            item.DisplayName,
            message,
            done,
            ok));
}
