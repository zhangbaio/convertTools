using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services.TikTok;

public static partial class TikTokBrowserActions
{
    private static readonly Regex UploadedContentCountPattern = new(@"正片内容\s*[\(（](\d+)[\)）]", RegexOptions.Compiled);
    private static readonly Regex PercentPattern = new(@"(\d{1,3})\s*%", RegexOptions.Compiled);

    /// <summary>ConnectOverCDP 下 Playwright 串流上限；单文件超过此值必须走 CDP 路径注入。</summary>
    internal const long CdpFileTransferLimitBytes = 45L * 1024 * 1024;
    private const int MaxInactiveIncompleteRepairAttempts = 2;
    private const int MaxInactiveIncompleteRepairFiles = 3;

    public static async Task UploadLocalVideosAsync(
        IPage page,
        IReadOnlyList<string> videoPaths,
        bool waitForFinish,
        Action<string>? log,
        CancellationToken ct)
    {
        if (videoPaths.Count == 0)
            throw new InvalidOperationException("未提供 TikTok 视频文件");

        var resolved = videoPaths.Select(Path.GetFullPath).ToList();
        var button = page.Locator("button").Filter(new() { HasText = "本地上传" }).First;
        await FeedVideoFilesAsync(page, button, resolved, ct);
        Log(log, waitForFinish
            ? $"已选择 {resolved.Count} 个视频文件，等待 TikTok 上传完成。"
            : $"已选择 {resolved.Count} 个视频文件，TikTok 已开始上传，继续填写其他表单。");
        await page.WaitForTimeoutAsync(5000);
        await EnsureVideoUploadStartedAsync(page, button, resolved, resolved.Count, log, ct);
        if (waitForFinish)
        {
            await WaitVideoUploadFinishedAsync(
                page,
                expectedCount: resolved.Count,
                titleCandidates: null,
                stallSeconds: 180,
                log: log,
                ct: ct,
                videoPaths: resolved);
        }
    }

    public static async Task WaitVideoUploadFinishedAsync(
        IPage page,
        int expectedCount,
        IReadOnlyList<string>? titleCandidates = null,
        double stallSeconds = 180,
        Action<string>? log = null,
        CancellationToken ct = default,
        int timeoutSeconds = 7200,
        double settleSeconds = 3.0,
        IReadOnlyList<string>? videoPaths = null)
    {
        timeoutSeconds = ResolveUploadTimeoutSeconds(timeoutSeconds, expectedCount, videoPaths);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var stallLimit = ResolveUploadStallSeconds(stallSeconds, expectedCount, videoPaths);
        string lastStatus = "";
        DateTime? readySince = null;
        (int? uploaded, int percent, bool uploading)? lastSignature = null;
        var lastProgressTime = DateTime.UtcNow;
        var readFailStreak = 0;
        (int uploaded, int waiting)? inactiveIncompleteSignature = null;
        DateTime? inactiveIncompleteSince = null;
        var inactiveIncompleteRepairAttempts = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            string bodyText;
            try
            {
                bodyText = await page.Locator("body").InnerTextAsync(new() { Timeout = 5000 });
                readFailStreak = 0;
            }
            catch
            {
                readFailStreak++;
                if (readFailStreak * 3 >= stallLimit)
                    throw new TimeoutException("TikTok 页面长时间无响应，无法读取上传进度。");
                await page.WaitForTimeoutAsync(3000);
                continue;
            }

            await ThrowIfDailyEpisodeLimitAsync(page).ConfigureAwait(false);
            if (bodyText.Contains("上传失败", StringComparison.Ordinal) ||
                bodyText.Contains("Upload failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TikTok 视频上传失败，请查看页面提示。");
            ThrowIfTikTokCrashText(bodyText);

            var uploadedCount = ExtractReadyUploadedVideoCount(bodyText, titleCandidates);
            var percentTotal = ExtractTotalUploadPercent(bodyText);
            var activity = TikTokUploadProgressParser.DetectUploadActivity(
                bodyText,
                await ReadUploadTableTextsAsync(page));
            var uploading = activity.Uploading;
            var waitingCount = activity.WaitingCount;
            var meetsDone = TikTokUploadProgressParser.IsUploadComplete(
                uploadedCount,
                expectedCount,
                activity);
            var inactiveIncomplete = IsInactiveIncompleteUpload(
                uploadedCount,
                expectedCount,
                waitingCount,
                uploading);

            var signature = (uploadedCount, percentTotal, uploading);
            if (lastSignature != signature)
            {
                lastSignature = signature;
                lastProgressTime = DateTime.UtcNow;
            }

            var countLabel = uploadedCount?.ToString() ?? "识别中";
            var displayPercent = TikTokUploadProgressParser.EstimateDisplayPercent(
                uploadedCount,
                expectedCount,
                waitingCount);
            var status =
                $"done={meetsDone}, uploaded={countLabel}/{expectedCount}, percent={percentTotal}, waiting={waitingCount}, uploading={uploading}, scoped={activity.IsTableScoped}";
            if (status != lastStatus)
            {
                if (meetsDone)
                    Log(log, $"视频已全部上传完成（{countLabel}/{expectedCount}）。");
                else
                    Log(log,
                        $"⏳ 正在等待视频文件上传完成（已就绪 {countLabel}/{expectedCount}，仍有 {waitingCount} 个等待中，" +
                        $"上传状态={(uploading ? "处理中" : "空闲")}，整体进度约 {displayPercent}%）。");
                lastStatus = status;
            }

            if (!meetsDone && inactiveIncomplete)
            {
                var inactiveSignature = (uploaded: uploadedCount.GetValueOrDefault(), waiting: waitingCount);
                if (inactiveIncompleteSignature != inactiveSignature)
                {
                    inactiveIncompleteSignature = inactiveSignature;
                    inactiveIncompleteSince = DateTime.UtcNow;
                }
                else if (inactiveIncompleteSince is not null &&
                         (DateTime.UtcNow - inactiveIncompleteSince.Value).TotalSeconds >= ResolveInactiveIncompleteRepairSeconds(stallLimit))
                {
                    if (inactiveIncompleteRepairAttempts >= MaxInactiveIncompleteRepairAttempts)
                    {
                        throw new TimeoutException(
                            $"TikTok 视频上传疑似单集卡死：当前已就绪 {countLabel}/{expectedCount}，等待中 0 个，自动补传 {inactiveIncompleteRepairAttempts} 次后仍未完成。");
                    }

                    if (!TryResolveInactiveIncompleteUploadPaths(
                            videoPaths,
                            expectedCount,
                            uploadedCount.GetValueOrDefault(),
                            bodyText,
                            titleCandidates,
                            out var missingPaths,
                            out var repairReason))
                    {
                        throw new TimeoutException(
                            $"TikTok 视频上传疑似卡死：当前已就绪 {countLabel}/{expectedCount}，等待中 0 个，且无法安全定位要补传的视频（{repairReason}）。");
                    }

                    inactiveIncompleteRepairAttempts++;
                    Log(log,
                        $"⚠️ 检测到 TikTok 上传疑似卡死（已就绪 {countLabel}/{expectedCount}，等待中 0 个），" +
                        $"自动补传 {missingPaths.Count} 个视频（第 {inactiveIncompleteRepairAttempts}/{MaxInactiveIncompleteRepairAttempts} 次）：{FormatUploadPathPreview(missingPaths)}");
                    await RefeedInactiveIncompleteVideosAsync(page, missingPaths, log, ct).ConfigureAwait(false);
                    await page.WaitForTimeoutAsync(5000);
                    inactiveIncompleteSignature = null;
                    inactiveIncompleteSince = null;
                    readySince = null;
                    lastSignature = null;
                    lastProgressTime = DateTime.UtcNow;
                    lastStatus = "";
                    continue;
                }
            }
            else
            {
                inactiveIncompleteSignature = null;
                inactiveIncompleteSince = null;
            }

            if (meetsDone)
            {
                readySince ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - readySince.Value).TotalSeconds >= Math.Max(0.5, settleSeconds))
                    return;
            }
            else
            {
                readySince = null;
                if ((DateTime.UtcNow - lastProgressTime).TotalSeconds >= stallLimit)
                {
                    throw new TimeoutException(
                        $"TikTok 视频上传长时间无进展（约 {(int)stallLimit} 秒进度无变化，当前已就绪 {countLabel}/{expectedCount}）。");
                }
            }

            await page.WaitForTimeoutAsync(3000);
        }

        throw new TimeoutException("等待 TikTok 视频上传完成超时。");
    }

    private static bool IsInactiveIncompleteUpload(
        int? uploadedCount,
        int expectedCount,
        int waitingCount,
        bool uploading)
    {
        if (uploadedCount is null || expectedCount <= 0)
            return false;

        var missingCount = expectedCount - uploadedCount.Value;
        return uploadedCount.Value > 0 &&
               missingCount is > 0 and <= MaxInactiveIncompleteRepairFiles &&
               waitingCount == 0 &&
               !uploading;
    }

    private static double ResolveInactiveIncompleteRepairSeconds(double stallLimit) =>
        Math.Clamp(stallLimit / 4.0, 60.0, 90.0);

    private static bool TryResolveInactiveIncompleteUploadPaths(
        IReadOnlyList<string>? videoPaths,
        int expectedCount,
        int uploadedCount,
        string bodyText,
        IReadOnlyList<string>? titleCandidates,
        out List<string> missingPaths,
        out string reason)
    {
        missingPaths = [];
        reason = "";

        var missingCount = expectedCount - uploadedCount;
        if (missingCount <= 0)
        {
            reason = "平台已识别为完整上传";
            return false;
        }

        if (missingCount > MaxInactiveIncompleteRepairFiles)
        {
            reason = $"缺失数量 {missingCount} 超过自动补传上限 {MaxInactiveIncompleteRepairFiles}";
            return false;
        }

        var paths = (videoPaths ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToList();
        if (paths.Count == 0)
        {
            reason = "没有可用于补传的视频路径";
            return false;
        }

        if (paths.Count <= missingCount)
        {
            missingPaths = paths;
            return true;
        }

        var completedIndexes = TikTokUploadProgressParser.ExtractCompletedUploadedEpisodeIndexes(
            bodyText,
            titleCandidates ?? Array.Empty<string>());
        if (completedIndexes.Count > 0)
        {
            var completedSet = completedIndexes.ToHashSet();
            var byIndex = new List<string>();
            for (var i = 0; i < paths.Count; i++)
            {
                var episodeIndex = ExtractEpisodeIndexFromPath(paths[i]) ?? (i + 1);
                if (!completedSet.Contains(episodeIndex))
                    byIndex.Add(paths[i]);
            }

            if (byIndex.Count is > 0 and <= MaxInactiveIncompleteRepairFiles)
            {
                missingPaths = byIndex;
                return true;
            }

            if (byIndex.Count > MaxInactiveIncompleteRepairFiles)
            {
                reason = $"按页面集号推断缺失 {byIndex.Count} 个视频，超过自动补传上限";
                return false;
            }
        }

        if (paths.Count == expectedCount)
        {
            var start = Math.Clamp(uploadedCount, 0, paths.Count);
            missingPaths = paths.Skip(start).Take(missingCount).ToList();
            if (missingPaths.Count == missingCount)
                return true;
        }

        if (paths.Count >= missingCount)
        {
            missingPaths = paths.Skip(paths.Count - missingCount).Take(missingCount).ToList();
            return missingPaths.Count == missingCount;
        }

        reason = $"当前仅有 {paths.Count} 个候选视频路径，不足以补传缺失的 {missingCount} 个";
        return false;
    }

    private static async Task RefeedInactiveIncompleteVideosAsync(
        IPage page,
        IReadOnlyList<string> missingPaths,
        Action<string>? log,
        CancellationToken ct)
    {
        await DismissFloatingAssistantAsync(page, log);
        var resolved = missingPaths.Select(Path.GetFullPath).ToList();
        var button = await ResolveVideoUploadButtonAsync(page);
        if (button is not null)
        {
            await FeedVideoFilesAsync(page, button, resolved, ct);
        }
        else
        {
            var input = await FindVideoFileInputAsync(page);
            if (input is null)
                throw new InvalidOperationException("未找到 TikTok 视频上传控件，无法自动补传卡住的视频。");
            await FeedVideoFilesToInputAsync(page, input, resolved, ct);
        }

        Log(log, $"已重新提交疑似卡住的视频：{FormatUploadPathPreview(resolved)}");
    }

    private static async Task<ILocator?> ResolveVideoUploadButtonAsync(IPage page)
    {
        foreach (var text in new[] { "上传视频", "本地上传" })
        {
            var locator = page.Locator("button").Filter(new() { HasText = text }).First;
            try
            {
                if (await locator.CountAsync() > 0)
                    return locator;
            }
            catch { /* try next */ }
        }

        return null;
    }

    private static string FormatUploadPathPreview(IReadOnlyList<string> paths)
    {
        var labels = paths
            .Take(5)
            .Select((path, index) =>
            {
                var episode = ExtractEpisodeIndexFromPath(path);
                var prefix = episode is > 0 ? $"第{episode}集 " : "";
                return $"{prefix}{Path.GetFileName(path)}";
            })
            .ToList();
        var suffix = paths.Count > labels.Count ? $" 等 {paths.Count} 个" : "";
        return string.Join("、", labels) + suffix;
    }

    private static async Task FeedVideoFilesAsync(
        IPage page,
        ILocator button,
        IReadOnlyList<string> resolvedPaths,
        CancellationToken ct)
    {
        if (await CdpDomFileUpload.TrySetFilesAsync(page, resolvedPaths, ct).ConfigureAwait(false))
            return;

        if (ContainsPlaywrightStreamBlockedFile(resolvedPaths))
            throw CreateCdpPathInjectionRequiredException(resolvedPaths);

        var batches = BuildVideoUploadBatches(resolvedPaths);
        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var batch = batches[batchIndex];
            if (await CdpDomFileUpload.TrySetFilesAsync(page, batch, ct).ConfigureAwait(false))
            {
                if (batchIndex < batches.Count - 1)
                    await page.WaitForTimeoutAsync(400);
                continue;
            }

            if (ContainsPlaywrightStreamBlockedFile(batch))
                throw CreateCdpPathInjectionRequiredException(batch);

            await FeedVideoFilesViaPlaywrightAsync(page, button, batch, ct);

            if (batchIndex < batches.Count - 1)
                await page.WaitForTimeoutAsync(400);
        }
    }

    public static async Task FeedVideoFilesToInputAsync(
        IPage page,
        ILocator input,
        IReadOnlyList<string> resolvedPaths,
        CancellationToken ct)
    {
        if (await CdpDomFileUpload.TrySetFilesAsync(page, resolvedPaths, ct).ConfigureAwait(false))
            return;

        if (ContainsPlaywrightStreamBlockedFile(resolvedPaths))
            throw CreateCdpPathInjectionRequiredException(resolvedPaths);

        await FeedVideoFilesWithBatchesAsync(page, input, resolvedPaths, ct);
    }

    private static async Task FeedVideoFilesViaPlaywrightAsync(
        IPage page,
        ILocator button,
        IReadOnlyList<string> batch,
        CancellationToken ct)
    {
        var timeoutMs = ResolveSetInputFilesTimeoutMs(batch);
        try
        {
            await button.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
            var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
            {
                await ClickWithFallbackAsync(button, ct);
            }, new() { Timeout = 15000 });
            await chooser.SetFilesAsync(batch);
        }
        catch (Exception ex)
        {
            var input = await FindVideoFileInputAsync(page);
            if (input is null)
                throw new InvalidOperationException($"未找到 TikTok 视频上传控件：{ex.Message}", ex);

            await SetInputFilesWithCdpGuardAsync(page, input, batch, timeoutMs, ct);
        }
    }

    private static List<IReadOnlyList<string>> BuildVideoUploadBatches(IReadOnlyList<string> resolvedPaths)
    {
        if (resolvedPaths.Count == 0)
            return new List<IReadOnlyList<string>>();

        var totalBytes = resolvedPaths.Sum(path => SafeFileSize(path));
        if (resolvedPaths.Count == 1 || totalBytes <= CdpFileTransferLimitBytes)
            return new List<IReadOnlyList<string>> { resolvedPaths.ToList() };

        var batches = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        long currentBytes = 0;
        foreach (var path in resolvedPaths)
        {
            var size = SafeFileSize(path);
            if (current.Count > 0 && currentBytes + size > CdpFileTransferLimitBytes)
            {
                batches.Add(current);
                current = new List<string>();
                currentBytes = 0;
            }

            current.Add(path);
            currentBytes += size;
        }

        if (current.Count > 0)
            batches.Add(current);

        return batches;
    }

    private static long SafeFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static async Task EnsureVideoUploadStartedAsync(
        IPage page,
        ILocator button,
        IReadOnlyList<string> resolvedPaths,
        int expectedCount,
        Action<string>? log,
        CancellationToken ct)
    {
        var startupSeconds = ResolveUploadStartupSeconds(expectedCount, resolvedPaths);
        if (await WaitVideoUploadSignalAsync(page, startupSeconds, ct))
            return;

        Log(log, $"⚠️ 约 {(int)startupSeconds} 秒未检测到视频开始上传，重新触发一次本地上传。");
        try
        {
            await FeedVideoFilesAsync(page, button, resolvedPaths, ct);
        }
        catch (Exception ex)
        {
            Log(log, $"⚠️ 重新触发本地上传失败：{ex.Message}");
            return;
        }

        await page.WaitForTimeoutAsync(5000);
        if (await WaitVideoUploadSignalAsync(page, startupSeconds, ct))
            Log(log, "重新触发后已检测到视频开始上传。");
        else
            Log(log, "⚠️ 重新触发后仍未检测到上传开始，将继续等待并由停滞看门狗处理。");
    }

    private static async Task<bool> WaitVideoUploadSignalAsync(IPage page, double maxSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, maxSeconds));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            string body;
            try { body = await page.Locator("body").InnerTextAsync(new() { Timeout = 3000 }); }
            catch { body = ""; }
            await ThrowIfDailyEpisodeLimitAsync(page).ConfigureAwait(false);
            if (HasVideoUploadProgressSignal(body)) return true;
            await page.WaitForTimeoutAsync(2000);
        }
        return false;
    }

    private static bool HasVideoUploadProgressSignal(string bodyText)
    {
        if (bodyText.Contains("等待中", StringComparison.Ordinal)) return true;
        if (new[] { "上传中", "正在上传", "处理中", "Transcoding", "Uploading" }
            .Any(m => bodyText.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (ExtractTotalUploadPercent(bodyText) > 0) return true;
        if (ExtractUploadedVideoCount(bodyText) is not null) return true;
        return false;
    }

    private static double ResolveUploadStartupSeconds(int expectedCount, IReadOnlyList<string>? videoPaths = null)
    {
        var baseline = Math.Min(180, Math.Max(45, 6 * Math.Max(0, expectedCount)));
        return baseline + ResolveUploadSizeBonusSeconds(videoPaths, expectedCount) * 0.5;
    }

    internal static double ResolveUploadStallSeconds(
        double baseSeconds,
        int expectedCount,
        IReadOnlyList<string>? videoPaths = null)
    {
        var baseline = baseSeconds > 0 ? baseSeconds : 180;
        var countScaled = Math.Max(baseline, 20.0 * Math.Max(0, expectedCount));
        return Math.Min(1200, countScaled + ResolveUploadSizeBonusSeconds(videoPaths, expectedCount));
    }

    internal static int ResolveUploadTimeoutSeconds(
        int baseTimeoutSeconds,
        int expectedCount,
        IReadOnlyList<string>? videoPaths = null)
    {
        var baseline = Math.Max(baseTimeoutSeconds, 3600);
        var bonus = (int)ResolveUploadSizeBonusSeconds(videoPaths, expectedCount);
        return Math.Min(14_400, baseline + bonus * 4);
    }

    internal static int ResolveSetInputFilesTimeoutMs(IReadOnlyList<string> paths)
    {
        var maxBytes = paths.Count == 0 ? 0 : paths.Max(SafeFileSize);
        var sizeMb = maxBytes / (1024.0 * 1024.0);
        return (int)Math.Min(600_000, Math.Max(60_000, 60_000 + sizeMb * 2000));
    }

    private static double ResolveUploadSizeBonusSeconds(IReadOnlyList<string>? videoPaths, int expectedCount)
    {
        if (videoPaths is null || videoPaths.Count == 0)
            return 0;

        var maxMb = videoPaths.Max(SafeFileSize) / (1024.0 * 1024.0);
        var avgMb = videoPaths.Sum(SafeFileSize) / (1024.0 * 1024.0) / Math.Max(1, expectedCount);
        var referenceMb = Math.Max(maxMb, avgMb);
        if (referenceMb <= 50)
            return 0;

        return 60.0 * Math.Ceiling(referenceMb / 50.0);
    }

    private static bool ContainsPlaywrightStreamBlockedFile(IReadOnlyList<string> paths) =>
        paths.Any(path => SafeFileSize(path) > CdpFileTransferLimitBytes);

    private static bool ExceedsPlaywrightStreamBatchLimit(IReadOnlyList<string> paths) =>
        paths.Sum(SafeFileSize) > CdpFileTransferLimitBytes;

    private static InvalidOperationException CreateCdpPathInjectionRequiredException(IReadOnlyList<string> paths)
    {
        var largest = paths
            .Select(path => (path, size: SafeFileSize(path)))
            .OrderByDescending(item => item.size)
            .FirstOrDefault();
        var name = string.IsNullOrWhiteSpace(largest.path) ? "未知文件" : Path.GetFileName(largest.path);
        var sizeLabel = FormatFileSize(largest.size);
        return new InvalidOperationException(
            $"视频文件过大（{name}，{sizeLabel}），内嵌浏览器须通过 CDP 路径注入上传，但未能绑定文件控件。" +
            "请确认当前在「内容上传」步骤且页面已加载完成，然后重试。");
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):0.#} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes} B";
    }

    public static async Task FeedVideoFilesToButtonAsync(
        IPage page,
        ILocator button,
        IReadOnlyList<string> resolvedPaths,
        CancellationToken ct) =>
        await FeedVideoFilesAsync(page, button, resolvedPaths, ct);

    public static async Task<ILocator?> FindVideoFileInputAsync(IPage page) =>
        await FindVideoFileInputInternalAsync(page);

    public static int ExtractTotalUploadPercent(string bodyText) => ExtractTotalUploadPercentInternal(bodyText);

    public static int? ExtractReadyUploadedVideoCount(string bodyText, IReadOnlyList<string>? titleCandidates) =>
        TikTokUploadProgressParser.ExtractReadyUploadedVideoCount(bodyText, titleCandidates);

    internal static async Task<IReadOnlyList<string>> ReadUploadTableTextsAsync(IPage page)
    {
        try
        {
            return await page.Locator(".semi-table-body").AllInnerTextsAsync();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static int? ExtractReadyUploadedVideoCountInternal(string bodyText, IReadOnlyList<string>? titleCandidates) =>
        TikTokUploadProgressParser.ExtractReadyUploadedVideoCount(bodyText, titleCandidates);

    private static int ExtractTotalUploadPercentInternal(string bodyText)
    {
        try
        {
            return PercentPattern.Matches(bodyText)
                .Select(m => int.Parse(m.Groups[1].Value))
                .Where(v => v is >= 0 and <= 100)
                .Sum();
        }
        catch { return 0; }
    }

    private static int? ExtractUploadedVideoCount(string bodyText)
    {
        var match = UploadedContentCountPattern.Match(bodyText);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var count))
            return count;
        return null;
    }

    private static async Task<ILocator?> FindVideoFileInputInternalAsync(IPage page)
    {
        // accept 现为扩展名(.mp4,.mov)，需同时匹配 mp4/mov/video（对齐 Python browser_actions）。
        var selectors = new[]
        {
            "input.semi-upload-hidden-input[accept*='mp4']",
            "input.semi-upload-hidden-input[accept*='mov']",
            "input.semi-upload-hidden-input[accept*='video']",
            "input[type=file][accept*='mp4']",
            "input[type=file][accept*='mov']",
            "input[type=file][accept*='video']",
            "input.semi-upload-hidden-input",
            "input.semi-upload-hidden-input-replace",
            "input[type=file]",
        };

        ILocator? best = null;
        var bestScore = -1;
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            int count;
            try { count = Math.Min(await locator.CountAsync(), 8); }
            catch { continue; }

            for (var index = 0; index < count; index++)
            {
                var candidate = locator.Nth(index);
                string accept;
                try { accept = (await candidate.GetAttributeAsync("accept") ?? "").Trim().ToLowerInvariant(); }
                catch { accept = ""; }

                var isVideo = accept.Contains("mp4") || accept.Contains("mov") || accept.Contains("video");
                if (!string.IsNullOrEmpty(accept) && !isVideo) continue;

                string? multipleAttr;
                try { multipleAttr = await candidate.GetAttributeAsync("multiple"); }
                catch { multipleAttr = null; }

                var score = 0;
                if (isVideo) score += 5;
                if (!string.IsNullOrEmpty(accept)) score += 2;
                if (multipleAttr is not null) score += 4;
                var normalized = selector.ToLowerInvariant();
                if (normalized.Contains("semi-upload-hidden-input") && !normalized.Contains("replace")) score += 3;
                if (normalized.Contains("replace")) score -= 1;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
        }

        return best;
    }

    private static async Task SetInputFilesInBatchesAsync(
        IPage page,
        ILocator input,
        IReadOnlyList<string> resolvedPaths,
        CancellationToken ct)
    {
        var batches = BuildVideoUploadBatches(resolvedPaths);
        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var batch = batches[batchIndex];
            if (await CdpDomFileUpload.TrySetFilesAsync(page, batch, ct).ConfigureAwait(false))
            {
                if (batchIndex < batches.Count - 1)
                    await Task.Delay(400, ct);
                continue;
            }

            if (ContainsPlaywrightStreamBlockedFile(batch) || ExceedsPlaywrightStreamBatchLimit(batch))
                throw CreateCdpPathInjectionRequiredException(batch);

            await SetInputFilesWithCdpGuardAsync(
                page, input, batch, ResolveSetInputFilesTimeoutMs(batch), ct);

            if (batchIndex < batches.Count - 1)
                await Task.Delay(400, ct);
        }
    }

    private static async Task SetInputFilesWithCdpGuardAsync(
        IPage page,
        ILocator input,
        IReadOnlyList<string> batch,
        int timeoutMs,
        CancellationToken ct)
    {
        if (await CdpDomFileUpload.TrySetFilesAsync(page, batch, ct).ConfigureAwait(false))
            return;

        if (ContainsPlaywrightStreamBlockedFile(batch) || ExceedsPlaywrightStreamBatchLimit(batch))
            throw CreateCdpPathInjectionRequiredException(batch);

        if (batch.Count == 1)
            await input.SetInputFilesAsync(batch[0], new() { Timeout = timeoutMs });
        else
            await input.SetInputFilesAsync(batch, new() { Timeout = timeoutMs });
    }

    private static async Task FeedVideoFilesWithBatchesAsync(
        IPage page,
        ILocator input,
        IReadOnlyList<string> resolvedPaths,
        CancellationToken ct)
    {
        string? multipleAttr;
        try { multipleAttr = await input.GetAttributeAsync("multiple"); }
        catch { multipleAttr = null; }

        if (multipleAttr is null)
        {
            foreach (var path in resolvedPaths)
            {
                ct.ThrowIfCancellationRequested();
                var single = new[] { path };
                if (await CdpDomFileUpload.TrySetFilesAsync(page, single, ct).ConfigureAwait(false))
                {
                    await Task.Delay(400, ct);
                    continue;
                }

                if (ContainsPlaywrightStreamBlockedFile(single))
                    throw CreateCdpPathInjectionRequiredException(single);

                await input.SetInputFilesAsync(path, new() { Timeout = ResolveSetInputFilesTimeoutMs(single) });
                await Task.Delay(400, ct);
            }
            return;
        }

        await SetInputFilesInBatchesAsync(page, input, resolvedPaths, ct);
    }

    private static async Task<ILocator?> FindCoverFileInputAsync(IPage page)
    {
        // 对齐 Python _find_cover_file_input：限定在 coverStruct 区域，避免误传视频/pdf 控件。
        var selectors = new[]
        {
            "#coverStruct input.semi-upload-hidden-input-replace",
            "#coverStruct input.semi-upload-hidden-input",
            "[x-field-id='coverStruct'] input.semi-upload-hidden-input-replace",
            "[x-field-id='coverStruct'] input.semi-upload-hidden-input",
            ".uploadField-Xm2Vjl input.semi-upload-hidden-input-replace",
            ".uploadField-Xm2Vjl input.semi-upload-hidden-input",
            "input.semi-upload-hidden-input-replace[accept='image/*']",
            "input.semi-upload-hidden-input[accept='image/*']",
            "input.semi-upload-hidden-input-replace[accept*='image']",
            "input.semi-upload-hidden-input[accept*='image']",
        };
        foreach (var selector in selectors)
        {
            var loc = page.Locator(selector).First;
            if (await loc.CountAsync() > 0) return loc;
        }
        return null;
    }
}
