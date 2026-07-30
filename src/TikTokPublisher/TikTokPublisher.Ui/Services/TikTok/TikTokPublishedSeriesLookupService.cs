using Microsoft.Playwright;
using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services.TikTok;

public sealed record TikTokPublishedSeriesLookupProgress(
    int Completed,
    int Total,
    string CurrentTitle,
    TikTokPublishedSeriesMatch? Match);

public static class TikTokPublishedSeriesLookupService
{
    public static async Task<IReadOnlyList<TikTokPublishedSeriesMatch>> LookupAsync(
        TikTokAccountProfile account,
        IEmbeddedBrowser? browser,
        IReadOnlyList<string> newTitles,
        IProgress<TikTokPublishedSeriesLookupProgress>? progress,
        Action<string>? log,
        CancellationToken ct)
    {
        if (newTitles.Count == 0)
            return [];

        IPlaywright? playwright = null;
        IBrowser? chromium = null;
        try
        {
            var useLaunch = string.Equals(
                (account.TiktokUploadBrowserMode ?? string.Empty).Trim(),
                "playwright",
                StringComparison.OrdinalIgnoreCase);

            IPage page;
            if (useLaunch)
            {
                var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(account);
                (playwright, chromium, page) = await EmbeddedBrowserAutomationBridge
                    .LaunchPageAsync(
                        account,
                        TikTokUrls.DefaultSeriesListUrl,
                        authPath,
                        account.TiktokPlaywrightUploadHeadless,
                        log,
                        ct)
                    .ConfigureAwait(false);
            }
            else
            {
                if (browser is null)
                    throw new InvalidOperationException("当前账号的内置浏览器尚未就绪或未登录。");
                (playwright, chromium, page) = await EmbeddedBrowserAutomationBridge
                    .ConnectPageAsync(browser, TikTokUrls.DefaultSeriesListUrl, log, ct)
                    .ConfigureAwait(false);
            }

            await TikTokSeriesListLookupService.OpenAsync(page, log, ct).ConfigureAwait(false);

            var results = new List<TikTokPublishedSeriesMatch>(newTitles.Count);
            for (var index = 0; index < newTitles.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var title = newTitles[index];
                log?.Invoke($"匹配已发布剧集 {index + 1}/{newTitles.Count}：{title}");
                TikTokPublishedSeriesMatch match;
                try
                {
                    var exactRows = await TikTokSeriesListLookupService
                        .SearchExactAsync(page, title, ct, log)
                        .ConfigureAwait(false);
                    match = BuildMatch(title, exactRows);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsLoginFailure(ex.Message))
                {
                    throw;
                }
                catch (Exception ex)
                {
                    match = new TikTokPublishedSeriesMatch(
                        title,
                        TikTokPublishedSeriesMatchKind.Failed,
                        Message: ex.Message);
                }

                results.Add(match);
                progress?.Report(new TikTokPublishedSeriesLookupProgress(
                    index + 1,
                    newTitles.Count,
                    title,
                    match));
            }

            return results;
        }
        finally
        {
            try
            {
                if (chromium is not null)
                    await chromium.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 外部 launch 关闭浏览器；CDP 模式仅断开自动化连接。
            }

            playwright?.Dispose();
        }
    }

    private static TikTokPublishedSeriesMatch BuildMatch(
        string title,
        IReadOnlyList<TikTokSeriesListRow> exactRows)
    {
        if (exactRows.Count == 0)
        {
            return new TikTokPublishedSeriesMatch(
                title,
                TikTokPublishedSeriesMatchKind.Missing);
        }

        if (exactRows.Count > 1)
        {
            return new TikTokPublishedSeriesMatch(
                title,
                TikTokPublishedSeriesMatchKind.Conflict,
                Message: $"平台存在 {exactRows.Count} 个完全同名项目");
        }

        var row = exactRows[0];
        var kind = TikTokPublishedSeriesMatchText.IsPublishedStatus(row.PlatformStatus)
            ? TikTokPublishedSeriesMatchKind.Published
            : TikTokPublishedSeriesMatchKind.NotPublished;
        return new TikTokPublishedSeriesMatch(
            title,
            kind,
            row.PlatformStatus,
            row.SeriesId,
            row.DetailUrl);
    }

    private static bool IsLoginFailure(string? message)
    {
        var text = message ?? string.Empty;
        return text.Contains("登录态失效", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("重新登录", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("/login", StringComparison.OrdinalIgnoreCase);
    }
}
