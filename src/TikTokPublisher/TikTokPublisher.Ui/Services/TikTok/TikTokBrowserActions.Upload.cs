using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Ui.Services.TikTok;

public static partial class TikTokBrowserActions
{
    private static readonly Regex UploadedContentCountPattern = new(@"正片内容\s*[\(（](\d+)[\)）]", RegexOptions.Compiled);
    private static readonly Regex PercentPattern = new(@"(\d{1,3})\s*%", RegexOptions.Compiled);

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
            await WaitVideoUploadFinishedAsync(page, expectedCount: resolved.Count, log: log, ct: ct);
    }

    public static async Task WaitVideoUploadFinishedAsync(
        IPage page,
        int expectedCount,
        IReadOnlyList<string>? titleCandidates = null,
        double stallSeconds = 180,
        Action<string>? log = null,
        CancellationToken ct = default,
        int timeoutSeconds = 7200,
        double settleSeconds = 3.0)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var stallLimit = ResolveUploadStallSeconds(stallSeconds, expectedCount);
        string lastStatus = "";
        DateTime? readySince = null;
        (int? uploaded, int percent, bool uploading)? lastSignature = null;
        var lastProgressTime = DateTime.UtcNow;
        var readFailStreak = 0;

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

            if (bodyText.Contains("上传失败", StringComparison.Ordinal) ||
                bodyText.Contains("Upload failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TikTok 视频上传失败，请查看页面提示。");

            var uploading = new[] { "上传中", "正在上传", "处理中", "Transcoding", "Uploading" }
                .Any(m => bodyText.Contains(m, StringComparison.OrdinalIgnoreCase));
            var uploadedCount = ExtractReadyUploadedVideoCount(bodyText, titleCandidates);
            var percentTotal = ExtractTotalUploadPercent(bodyText);
            var waitingCount = CountOccurrences(bodyText, "等待中");
            var submit = page.Locator("button").Filter(new() { HasText = "提交" }).First;
            var disabled = await IsAriaDisabledAsync(submit);
            var meetsDone = !uploading && !disabled && UploadedCountMeetsExpected(uploadedCount, expectedCount);

            var signature = (uploadedCount, percentTotal, uploading);
            if (lastSignature != signature)
            {
                lastSignature = signature;
                lastProgressTime = DateTime.UtcNow;
            }

            var countLabel = uploadedCount?.ToString() ?? "识别中";
            var displayPercent = uploadedCount is not null && expectedCount > 0
                ? Math.Min(100, Math.Max(0, (int)Math.Round(uploadedCount.Value / (double)expectedCount * 100)))
                : 0;
            var status =
                $"done={meetsDone}, uploaded={countLabel}/{expectedCount}, percent={percentTotal}, waiting={waitingCount}, uploading={uploading}, disabled={disabled}";
            if (status != lastStatus)
            {
                if (meetsDone)
                    Log(log, $"视频已全部上传完成（{countLabel}/{expectedCount}）。");
                else
                    Log(log,
                        $"⏳ 正在等待视频文件上传完成（已就绪 {countLabel}/{expectedCount}，仍有 {waitingCount} 个等待中，整体进度约 {displayPercent}%）。");
                lastStatus = status;
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

    private static async Task FeedVideoFilesAsync(
        IPage page,
        ILocator button,
        IReadOnlyList<string> resolvedPaths,
        CancellationToken ct)
    {
        try
        {
            await button.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
            var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
            {
                await ClickWithFallbackAsync(button, ct);
            }, new() { Timeout = 15000 });
            await chooser.SetFilesAsync(resolvedPaths);
        }
        catch (Exception ex)
        {
            var input = await FindVideoFileInputAsync(page);
            if (input is null)
                throw new InvalidOperationException($"未找到 TikTok 视频上传控件：{ex.Message}", ex);
            await input.SetInputFilesAsync(resolvedPaths, new() { Timeout = 15000 });
        }
    }

    private static async Task EnsureVideoUploadStartedAsync(
        IPage page,
        ILocator button,
        IReadOnlyList<string> resolvedPaths,
        int expectedCount,
        Action<string>? log,
        CancellationToken ct)
    {
        var startupSeconds = ResolveUploadStartupSeconds(expectedCount);
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

    private static double ResolveUploadStartupSeconds(int expectedCount) =>
        Math.Min(180, Math.Max(45, 6 * Math.Max(0, expectedCount)));

    private static double ResolveUploadStallSeconds(double baseSeconds, int expectedCount)
    {
        var baseline = baseSeconds > 0 ? baseSeconds : 180;
        return Math.Min(600, Math.Max(baseline, 20.0 * Math.Max(0, expectedCount)));
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
        ExtractReadyUploadedVideoCountInternal(bodyText, titleCandidates);

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

    private static int? ExtractReadyUploadedVideoCountInternal(string bodyText, IReadOnlyList<string>? titleCandidates)
    {
        _ = titleCandidates;
        return ExtractUploadedVideoCount(bodyText);
    }

    private static bool UploadedCountMeetsExpected(int? uploadedCount, int expectedCount) =>
        uploadedCount is not null && uploadedCount.Value >= Math.Max(1, expectedCount);

    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value)) return 0;
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static async Task<ILocator?> FindVideoFileInputInternalAsync(IPage page)
    {
        var selectors = new[]
        {
            "input.semi-upload-hidden-input",
            "input.semi-upload-hidden-input-replace",
            "input[type=file][accept*='video']",
        };
        foreach (var selector in selectors)
        {
            var loc = page.Locator(selector).First;
            if (await loc.CountAsync() > 0) return loc;
        }
        return null;
    }

    private static async Task<ILocator?> FindCoverFileInputAsync(IPage page)
    {
        var selectors = new[]
        {
            "input[type=file][accept*='image']",
            ".semi-upload input[type=file]",
        };
        foreach (var selector in selectors)
        {
            var loc = page.Locator(selector).First;
            if (await loc.CountAsync() > 0) return loc;
        }
        return null;
    }
}
