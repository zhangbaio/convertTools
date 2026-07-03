using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Publishing;

public sealed record AccountPublishJob(
    TikTokAccountProfile Account,
    IReadOnlyList<PublishItem> Items);

public sealed class PublishScheduler
{
    private readonly IPublishAutomation _automation;
    private readonly IEmbeddedBrowserProvider _browserProvider;

    public PublishScheduler(IPublishAutomation automation, IEmbeddedBrowserProvider browserProvider)
    {
        _automation = automation;
        _browserProvider = browserProvider;
    }

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
            var browser = await _browserProvider
                .GetBrowserAsync(job.Account, ct, EmbeddedBrowserAccessOptions.Background)
                .ConfigureAwait(false);
            if (browser is null)
            {
                foreach (var item in job.Items)
                {
                    Report(onProgress, job, item, "内置浏览器未就绪，请先在「浏览器」页登录", done: true, ok: false);
                }
                return;
            }

            var proxy = TikTokProxyHelper.BuildFromAccount(job.Account);
            if (proxy is not null)
            {
                foreach (var item in job.Items)
                    Report(onProgress, job, item, $"使用账号代理：{proxy.Description}", done: false, ok: false);
            }

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
                        browser,
                        finalAction,
                        msg => Report(onProgress, job, item, msg, done: false, ok: false),
                        ct).ConfigureAwait(false);
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
