using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Ui.Services.TikTok;

/// <summary>分批上传策略（移植自 Python <c>batch_upload_service.py</c>）。</summary>
public static class TikTokBatchUploadService
{
    private static readonly Regex EpisodeNumberPattern = new(@"第\s*(\d+)\s*集", RegexOptions.Compiled);

    public static async Task FillRemainingWithBatchedUploadAsync(
        IPage page,
        TikTokPublishPayload payload,
        TikTokPublishOptions options,
        TikTokPublishRecommendation recommendation,
        string coverPath,
        bool coverAlreadyUploaded,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!coverAlreadyUploaded)
            await TikTokBrowserActions.UploadCoverAsync(page, coverPath, log, ct);

        var uploadPaths = payload.UploadVideoPaths.Count > 0
            ? payload.UploadVideoPaths.ToList()
            : payload.VideoPaths.ToList();

        await UploadInBatchesAsync(
            page,
            uploadPaths,
            batchSize: options.UploadBatchSize,
            stallSeconds: options.UploadBatchStallSeconds,
            maxRetries: options.UploadBatchMaxRetries,
            titleCandidates: TikTokBrowserActions.PayloadTitleCandidates(payload),
            log,
            ct);

        await TikTokBrowserActions.FillSharedPublishFieldsAsync(
            page, payload, options, recommendation, log, ct);
        log?.Invoke("TikTok 分批上传完成，其余表单已填写。");
    }

    private static async Task UploadInBatchesAsync(
        IPage page,
        IReadOnlyList<string> videoPaths,
        int batchSize,
        double stallSeconds,
        int maxRetries,
        IReadOnlyList<string>? titleCandidates,
        Action<string>? log,
        CancellationToken ct)
    {
        var total = videoPaths.Count;
        if (total == 0)
            throw new InvalidOperationException("未提供 TikTok 视频文件");

        batchSize = Math.Clamp(batchSize, 1, 20);
        maxRetries = Math.Clamp(maxRetries, 1, 10);
        stallSeconds = Math.Clamp(stallSeconds, 20, 600);

        var batches = new List<List<string>>();
        for (var i = 0; i < total; i += batchSize)
            batches.Add(videoPaths.Skip(i).Take(batchSize).ToList());

        log?.Invoke($"分批上传：共 {total} 集，每批 {batchSize} 个，分 {batches.Count} 批顺序上传。");

        var readyDone = 0;
        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            var batch = batches[batchIndex];
            var labels = string.Join("、", batch.Select(EpisodeLabel));
            var targetReady = readyDone + batch.Count;
            var attempt = 0;

            while (true)
            {
                attempt++;
                ct.ThrowIfCancellationRequested();
                log?.Invoke($"第 {batchIndex + 1}/{batches.Count} 批（{labels}）开始上传（第 {attempt} 次尝试）。");

                await FeedBatchAsync(page, batch, log, ct);
                var outcome = await WaitBatchAsync(
                    page,
                    targetReady,
                    titleCandidates,
                    stallSeconds,
                    log,
                    ct);

                if (outcome == BatchWaitOutcome.Done)
                {
                    readyDone = targetReady;
                    log?.Invoke($"第 {batchIndex + 1}/{batches.Count} 批上传完成（已就绪 {readyDone}/{total}）。");
                    break;
                }

                log?.Invoke($"⚠️ 第 {batchIndex + 1} 批检测到卡死，删除本批并重传（第 {attempt}/{maxRetries} 次）。");
                var batchEpisodes = batch.Select(EpisodeNumber).Where(n => n > 0).ToList();
                await DeleteBatchRowsAsync(page, batchEpisodes, log, ct);
                if (attempt >= maxRetries)
                {
                    throw new InvalidOperationException(
                        $"TikTok 分批上传失败：第 {batchIndex + 1} 批（{labels}）重试 {maxRetries} 次仍卡死。");
                }
            }
        }
    }

    private static async Task FeedBatchAsync(
        IPage page,
        IReadOnlyList<string> batch,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await TikTokBrowserActions.DismissFloatingAssistantAsync(page, log);
        var resolved = batch.Select(Path.GetFullPath).ToList();
        var button = await ResolveUploadButtonAsync(page);

        if (button is not null)
            await TikTokBrowserActions.FeedVideoFilesToButtonAsync(page, button, resolved, ct);
        else
        {
            var input = await TikTokBrowserActions.FindVideoFileInputAsync(page);
            if (input is null)
                throw new InvalidOperationException("未找到 TikTok 视频上传控件（上传视频按钮 / 文件输入均不可用）。");
            await input.SetInputFilesAsync(resolved, new() { Timeout = 15000 });
        }

        log?.Invoke($"已提交本批 {batch.Count} 个文件。");
        await page.WaitForTimeoutAsync(1500);
    }

    private static async Task<ILocator?> ResolveUploadButtonAsync(IPage page)
    {
        foreach (var text in new[] { "上传视频", "本地上传" })
        {
            var loc = page.Locator("button").Filter(new() { HasText = text }).First;
            try
            {
                if (await loc.CountAsync() > 0)
                    return loc;
            }
            catch { /* try next */ }
        }
        return null;
    }

    private static async Task<BatchWaitOutcome> WaitBatchAsync(
        IPage page,
        int targetReady,
        IReadOnlyList<string>? titleCandidates,
        double stallSeconds,
        Action<string>? log,
        CancellationToken ct,
        double pollSeconds = 3.0)
    {
        (int Ready, int Percent)? lastSig = null;
        var lastChange = DateTime.UtcNow;
        var lastLog = "";

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string bodyText;
            try { bodyText = await page.Locator("body").InnerTextAsync(new() { Timeout = 3000 }); }
            catch { bodyText = ""; }

            if (bodyText.Contains("上传失败", StringComparison.Ordinal) ||
                bodyText.Contains("Upload failed", StringComparison.OrdinalIgnoreCase))
                return BatchWaitOutcome.Stuck;

            var ready = TikTokBrowserActions.ExtractReadyUploadedVideoCount(bodyText, titleCandidates) ?? 0;
            var percent = TikTokBrowserActions.ExtractTotalUploadPercent(bodyText);
            if (ready >= targetReady)
                return BatchWaitOutcome.Done;

            var sig = (ready, percent);
            if (lastSig != sig)
            {
                lastSig = sig;
                lastChange = DateTime.UtcNow;
            }
            else if ((DateTime.UtcNow - lastChange).TotalSeconds >= stallSeconds)
            {
                return BatchWaitOutcome.Stuck;
            }

            var msg = $"⏳ 等待本批上传：已就绪 {ready}/{targetReady}（页面进度信号 {percent}）。";
            if (msg != lastLog)
            {
                log?.Invoke(msg);
                lastLog = msg;
            }

            await page.WaitForTimeoutAsync((int)(pollSeconds * 1000));
        }
    }

    private static async Task<int> DeleteBatchRowsAsync(
        IPage page,
        IReadOnlyList<int> episodes,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var targets = episodes.Where(e => e > 0).Distinct().OrderBy(e => e).ToList();
        if (targets.Count == 0) return 0;

        var deleted = 0;
        for (var guard = 0; guard < 200; guard++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await page.EvaluateAsync<string>(
                """
                (targets) => {
                  const body = document.querySelector('.semi-table-body');
                  if (!body) return 'no-table';
                  body.scrollTop = body.scrollHeight;
                  const rows = body.querySelectorAll('tr.semi-table-row');
                  for (const tr of rows) {
                    const name = (tr.querySelector('td') && tr.querySelector('td').textContent) || '';
                    const m = name.match(/-第\s*(\d+)\s*集/);
                    if (!m || !targets.includes(+m[1])) continue;
                    const icon = tr.querySelector('[data-icon="X"],[data-testid="X"]')
                              || tr.querySelector('[data-icon="Backspace"],[data-testid="Backspace"]');
                    const btn = icon ? icon.closest('button') : null;
                    if (btn) { btn.click(); return 'clicked:' + m[1]; }
                  }
                  return 'none';
                }
                """,
                targets);

            if (result == "no-table")
                throw new InvalidOperationException("删除本批行失败：未找到正片列表（semi-table）。");
            if (result == "none")
                break;

            await page.WaitForTimeoutAsync(500);
            foreach (var text in new[] { "确认删除", "确认", "确定", "删除" })
            {
                try
                {
                    var dlg = page.Locator("[role='dialog'] button").Filter(new() { HasText = text }).First;
                    if (await dlg.CountAsync() > 0)
                    {
                        await TikTokBrowserActions.ClickLocatorAsync(dlg, ct);
                        break;
                    }
                }
                catch { /* try next */ }
            }
            await page.WaitForTimeoutAsync(700);
            deleted++;
        }

        if (deleted >= 200)
            throw new InvalidOperationException("删除本批行次数异常超限，已中止。");

        log?.Invoke($"已删除本批 {deleted} 行（集号 {string.Join(", ", targets)}）。");
        return deleted;
    }

    private static string EpisodeLabel(string path)
    {
        var match = EpisodeNumberPattern.Match(Path.GetFileName(path));
        return match.Success ? $"第{match.Groups[1].Value}集" : Path.GetFileName(path);
    }

    private static int EpisodeNumber(string path)
    {
        var match = EpisodeNumberPattern.Match(Path.GetFileName(path));
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 0;
    }

    private enum BatchWaitOutcome
    {
        Done,
        Stuck,
    }
}
