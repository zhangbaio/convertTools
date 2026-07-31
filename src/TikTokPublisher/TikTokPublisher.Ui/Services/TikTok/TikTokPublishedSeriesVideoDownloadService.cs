using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services.TikTok;

public sealed record TikTokPublishedSeriesVideoDownloadResult(
    bool Ok,
    string Message,
    string SeriesId = "",
    string DetailUrl = "",
    string StagingDirectory = "",
    int PlatformEpisodeCount = 0,
    int DownloadedEpisodeCount = 0);

/// <summary>
/// Downloads only the episodes needed for copyright-proof generation from an
/// already-published TikTok series. Existing validated files are reused so a
/// stopped recovery can continue without downloading completed episodes again.
/// </summary>
public static class TikTokPublishedSeriesVideoDownloadService
{
    private const int DownloadAttempts = 3;
    private const long MinimumVideoBytes = 64 * 1024;
    private static readonly TimeSpan EpisodeLookupTimeout = TimeSpan.FromSeconds(35);

    private static readonly Regex EpisodeNumberPattern =
        new(@"第\s*(\d+)\s*集", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EpisodeCountPattern =
        new(@"(?:共\s*)?(\d{1,4})\s*集", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<TikTokPublishedSeriesVideoDownloadResult> DownloadAsync(
        TikTokAccountProfile account,
        IEmbeddedBrowser? browser,
        string newTitle,
        string workspaceRoot,
        int requiredEpisodeCount,
        Action<string>? log,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        var title = (newTitle ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
            return Fail("新剧名不能为空。");

        var required = Math.Clamp(requiredEpisodeCount <= 0 ? 1 : requiredEpisodeCount, 1, 200);
        IPlaywright? playwright = null;
        IBrowser? chromium = null;
        List<IPage> extraPages = [];
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
                    return Fail("当前账号的内置浏览器尚未就绪或未登录。");
                (playwright, chromium, page) = await EmbeddedBrowserAutomationBridge
                    .ConnectPageAsync(
                        browser,
                        TikTokUrls.DefaultSeriesListUrl,
                        log,
                        ct)
                    .ConfigureAwait(false);
            }

            await TikTokSeriesListLookupService.OpenAsync(page, log, ct).ConfigureAwait(false);
            var exactRows = await TikTokSeriesListLookupService
                .SearchExactAsync(page, title, ct, log)
                .ConfigureAwait(false);
            if (exactRows.Count == 0)
                return Fail($"TikTok 原创管理中未找到完全一致的新剧名：{title}");
            if (exactRows.Count > 1)
                return Fail($"TikTok 原创管理中存在 {exactRows.Count} 个同名项目：{title}");

            var match = exactRows[0];
            if (!TikTokPublishedSeriesMatchText.IsPublishedStatus(match.PlatformStatus))
                return Fail($"TikTok 项目「{title}」尚未发布，当前状态：{match.PlatformStatus}");
            if (string.IsNullOrWhiteSpace(match.DetailUrl))
                return Fail($"TikTok 项目「{title}」缺少详情页地址。");

            var staging = DeletedCopyrightProofPublishedVideoRecoveryService
                .ResolveStagingDirectory(workspaceRoot, title, match.SeriesId);
            Directory.CreateDirectory(staging);

            log?.Invoke(
                $"平台视频恢复：已按新剧名定位 TikTok 已发布项目「{title}」，" +
                $"准备获取前 {required} 集。");
            await PrepareDownloadPageAsync(page, match.DetailUrl, log, ct).ConfigureAwait(false);
            var platformEpisodeCount = await ReadPlatformEpisodeCountAsync(page).ConfigureAwait(false);
            var targetCount = platformEpisodeCount > 0
                ? Math.Min(required, platformEpisodeCount)
                : required;

            var downloaded = 0;
            var pendingEpisodes = new List<int>(targetCount);
            for (var episode = 1; episode <= targetCount; episode++)
            {
                ct.ThrowIfCancellationRequested();
                var existing = FindExistingEpisodeFile(staging, episode);
                if (!string.IsNullOrWhiteSpace(existing) && IsValidVideo(existing))
                {
                    downloaded++;
                    log?.Invoke(
                        $"平台视频恢复 [{episode}/{targetCount}]：已存在，跳过下载。");
                    continue;
                }

                pendingEpisodes.Add(episode);
            }

            var concurrency = DeletedCopyrightProofPublishedVideoRecoveryService
                .ResolveEpisodeDownloadConcurrency(pendingEpisodes.Count);
            if (concurrency > 0)
            {
                log?.Invoke(
                    $"平台视频恢复：{pendingEpisodes.Count} 集待下载，" +
                    $"启用 {concurrency} 路分集并发。");

                for (var index = 1; index < concurrency; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    extraPages.Add(await page.Context.NewPageAsync().ConfigureAwait(false));
                }

                await Task.WhenAll(
                        extraPages.Select(extraPage =>
                            PrepareDownloadPageAsync(
                                extraPage,
                                match.DetailUrl,
                                log: null,
                                ct)))
                    .ConfigureAwait(false);

                var queue = new ConcurrentQueue<int>(pendingEpisodes);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var failureGate = new object();
                var logGate = new object();
                string? failureMessage = null;

                void WriteLog(string message)
                {
                    lock (logGate)
                    {
                        log?.Invoke(message);
                    }
                }

                void RecordFailure(string message)
                {
                    lock (failureGate)
                    {
                        if (failureMessage is not null)
                            return;
                        failureMessage = message;
                        linkedCts.Cancel();
                    }
                }

                var workerPages = new[] { page }.Concat(extraPages).ToArray();
                var workers = workerPages.Select(async workerPage =>
                {
                    while (!linkedCts.IsCancellationRequested &&
                           queue.TryDequeue(out var episode))
                    {
                        try
                        {
                            var error = await DownloadEpisodeAsync(
                                    workerPage,
                                    staging,
                                    episode,
                                    targetCount,
                                    WriteLog,
                                    linkedCts.Token)
                                .ConfigureAwait(false);
                            if (error is not null)
                            {
                                RecordFailure(error);
                                break;
                            }

                            Interlocked.Increment(ref downloaded);
                        }
                        catch (OperationCanceledException)
                            when (!ct.IsCancellationRequested && linkedCts.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            RecordFailure(
                                $"TikTok 第 {episode} 集下载任务异常：{ex.Message}。");
                            break;
                        }
                    }
                });
                await Task.WhenAll(workers).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                if (failureMessage is not null)
                {
                    return Fail(
                        failureMessage +
                        $"已保留完成的 {downloaded} 集供下次继续。",
                        match,
                        staging,
                        platformEpisodeCount,
                        downloaded);
                }
            }

            return new TikTokPublishedSeriesVideoDownloadResult(
                true,
                $"已从 TikTok 已发布项目恢复 {downloaded} 集视频。",
                match.SeriesId,
                match.DetailUrl,
                staging,
                platformEpisodeCount,
                downloaded);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail($"从 TikTok 已发布项目下载视频失败：{ex.Message}");
        }
        finally
        {
            foreach (var extraPage in extraPages.AsEnumerable().Reverse())
            {
                try
                {
                    if (!extraPage.IsClosed)
                        await extraPage.CloseAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 只关闭本次并发下载创建的辅助页面。
                }
            }

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

    private static async Task PrepareDownloadPageAsync(
        IPage page,
        string detailUrl,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await page.GotoAsync(detailUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 90000,
        }).ConfigureAwait(false);
        try
        {
            await page.WaitForLoadStateAsync(
                LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = 15000 }).ConfigureAwait(false);
        }
        catch
        {
            // TikTok 详情页为持续请求的 SPA。
        }

        ct.ThrowIfCancellationRequested();
        await TikTokBrowserActions.DismissFloatingAssistantAsync(page, log).ConfigureAwait(false);
        await OpenContentUploadTabAsync(page, ct).ConfigureAwait(false);
    }

    private static async Task<string?> DownloadEpisodeAsync(
        IPage page,
        string staging,
        int episode,
        int targetCount,
        Action<string> log,
        CancellationToken ct)
    {
        var row = await FindEpisodeRowAsync(page, episode, ct).ConfigureAwait(false);
        if (row is null)
        {
            return $"TikTok 内容上传页面未找到第 {episode} 集，";
        }

        var button = await FindDownloadButtonAsync(row).ConfigureAwait(false);
        if (button is null)
        {
            return $"TikTok 内容上传页面第 {episode} 集没有可用的下载按钮，";
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= DownloadAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                log(
                    $"平台视频恢复 [{episode}/{targetCount}]：开始下载" +
                    (attempt > 1 ? $"（重试 {attempt}/{DownloadAttempts}）" : "。"));
                var download = await page.RunAndWaitForDownloadAsync(
                        () => button.ClickAsync(new LocatorClickOptions
                        {
                            Timeout = 15000,
                        }),
                        new PageRunAndWaitForDownloadOptions
                        {
                            Timeout = 90000,
                        })
                    .ConfigureAwait(false);
                var extension = ResolveVideoExtension(download.SuggestedFilename);
                var destination = Path.Combine(
                    staging,
                    $"第{episode:D3}集{extension}");
                var partial = destination + ".part";
                if (File.Exists(partial))
                    File.Delete(partial);
                await download.SaveAsAsync(partial).ConfigureAwait(false);
                ValidateDownloadedVideo(partial);
                File.Move(partial, destination, overwrite: true);
                log(
                    $"平台视频恢复 [{episode}/{targetCount}]：下载完成，" +
                    $"{new FileInfo(destination).Length / 1024d / 1024d:0.0} MB。");
                return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < DownloadAttempts)
                    await page.WaitForTimeoutAsync(900).ConfigureAwait(false);
            }
        }

        return $"TikTok 第 {episode} 集下载失败：{lastError?.Message ?? "未知错误"}。";
    }

    private static async Task OpenContentUploadTabAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var candidates = new[]
        {
            page.GetByText("内容上传", new PageGetByTextOptions { Exact = true }).First,
            page.Locator("[role='tab']").Filter(new LocatorFilterOptions { HasText = "内容上传" }).First,
        };
        foreach (var candidate in candidates)
        {
            if (await candidate.CountAsync().ConfigureAwait(false) == 0)
                continue;
            try
            {
                if (!await candidate.IsVisibleAsync().ConfigureAwait(false))
                    continue;
                await candidate.ClickAsync(new LocatorClickOptions { Timeout = 10000 })
                    .ConfigureAwait(false);
                await page.WaitForTimeoutAsync(800).ConfigureAwait(false);
                return;
            }
            catch
            {
                // 尝试下一个定位器。
            }
        }

        throw new InvalidOperationException("TikTok 剧集详情页未找到“内容上传”页签。");
    }

    private static async Task<int> ReadPlatformEpisodeCountAsync(IPage page)
    {
        try
        {
            var text = await page.Locator("body").InnerTextAsync(
                    new LocatorInnerTextOptions { Timeout = 10000 })
                .ConfigureAwait(false);
            var values = EpisodeCountPattern.Matches(text)
                .Select(match => int.TryParse(match.Groups[1].Value, out var value) ? value : 0)
                .Where(value => value > 0)
                .ToArray();
            return values.Length == 0 ? 0 : values.Max();
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<ILocator?> FindEpisodeRowAsync(
        IPage page,
        int episode,
        CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var stagnantRounds = 0;
        var lastMaxEpisode = 0;
        while (stopwatch.Elapsed < EpisodeLookupTimeout)
        {
            ct.ThrowIfCancellationRequested();
            var rows = page.Locator("tbody tr, [role='row']");
            var count = Math.Min(await rows.CountAsync().ConfigureAwait(false), 300);
            var maxEpisode = 0;
            ILocator? lastEpisodeRow = null;
            for (var index = 0; index < count; index++)
            {
                var row = rows.Nth(index);
                string text;
                try
                {
                    text = await row.InnerTextAsync(
                            new LocatorInnerTextOptions { Timeout = 1200 })
                        .ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }

                var match = EpisodeNumberPattern.Match(text);
                if (!match.Success ||
                    !int.TryParse(match.Groups[1].Value, out var rowEpisode))
                {
                    continue;
                }

                maxEpisode = Math.Max(maxEpisode, rowEpisode);
                lastEpisodeRow = row;
                if (rowEpisode == episode)
                {
                    try
                    {
                        await row.ScrollIntoViewIfNeededAsync(
                                new LocatorScrollIntoViewIfNeededOptions { Timeout = 5000 })
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // 行已在虚拟列表中即可继续。
                    }
                    return row;
                }
            }

            stagnantRounds = maxEpisode <= lastMaxEpisode ? stagnantRounds + 1 : 0;
            lastMaxEpisode = Math.Max(lastMaxEpisode, maxEpisode);
            if (stagnantRounds >= 4 || lastEpisodeRow is null)
                break;

            try
            {
                await lastEpisodeRow.EvaluateAsync(
                        """
                        element => {
                          let parent = element.parentElement;
                          while (parent && parent.scrollHeight <= parent.clientHeight) {
                            parent = parent.parentElement;
                          }
                          if (parent) {
                            parent.scrollTop += Math.max(parent.clientHeight * 0.8, 480);
                          } else {
                            window.scrollBy(0, 720);
                          }
                        }
                        """)
                    .ConfigureAwait(false);
            }
            catch
            {
                await page.Mouse.WheelAsync(0, 720).ConfigureAwait(false);
            }
            await page.WaitForTimeoutAsync(450).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<ILocator?> FindDownloadButtonAsync(ILocator row)
    {
        foreach (var selector in new[]
                 {
                     "button[aria-label*='下载']",
                     "button[title*='下载']",
                     "[role='button'][aria-label*='下载']",
                     "[role='button'][title*='下载']",
                     "button[aria-label*='Download' i]",
                     "button[title*='Download' i]",
                 })
        {
            var candidate = row.Locator(selector).First;
            if (await candidate.CountAsync().ConfigureAwait(false) == 0)
                continue;
            try
            {
                if (await candidate.IsVisibleAsync().ConfigureAwait(false) &&
                    await candidate.IsEnabledAsync().ConfigureAwait(false))
                {
                    return candidate;
                }
            }
            catch
            {
                // 尝试下一个定位器。
            }
        }

        // 当前 TikTok 内容行的操作区依次为：编辑、下载、重新上传、删除。
        // 保留语义定位优先，只有页面未暴露 aria/title 时才使用操作区顺序兜底。
        var buttons = row.Locator("button");
        var count = await buttons.CountAsync().ConfigureAwait(false);
        if (count < 4)
            return null;

        var fallback = buttons.Nth(count - 3);
        try
        {
            return await fallback.IsVisibleAsync().ConfigureAwait(false) &&
                   await fallback.IsEnabledAsync().ConfigureAwait(false)
                ? fallback
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindExistingEpisodeFile(string directory, int episode)
    {
        if (!Directory.Exists(directory))
            return null;
        return Directory
            .EnumerateFiles(directory, $"第{episode:D3}集.*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path =>
                !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveVideoExtension(string? suggestedFilename)
    {
        var extension = Path.GetExtension(suggestedFilename ?? string.Empty).ToLowerInvariant();
        return extension is ".mp4" or ".mov" or ".m4v" or ".webm" or ".mkv" or ".avi"
            ? extension
            : ".mp4";
    }

    private static void ValidateDownloadedVideo(string path)
    {
        if (!File.Exists(path))
            throw new InvalidDataException("浏览器没有生成下载文件。");
        var length = new FileInfo(path).Length;
        if (length < MinimumVideoBytes)
            throw new InvalidDataException($"下载文件过小（{length} 字节），可能不是有效视频。");
    }

    private static bool IsValidVideo(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length >= MinimumVideoBytes;
        }
        catch
        {
            return false;
        }
    }

    private static TikTokPublishedSeriesVideoDownloadResult Fail(string message) =>
        new(false, message);

    private static TikTokPublishedSeriesVideoDownloadResult Fail(
        string message,
        TikTokSeriesListRow match,
        string staging,
        int platformEpisodeCount,
        int downloaded) =>
        new(
            false,
            message,
            match.SeriesId,
            match.DetailUrl,
            staging,
            platformEpisodeCount,
            downloaded);
}
