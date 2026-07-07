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
            ct,
            baselineReadyCount: 0,
            expectedTotal: payload.EpisodeCount);

        await TikTokBrowserActions.FillSharedPublishFieldsAsync(
            page, payload, options, recommendation, log, ct);
        log?.Invoke("TikTok 分批上传完成，其余表单已填写。");
    }

    /// <summary>按账号配置每批 N 个顺序上传（编辑流补传等场景可传入已有就绪数作为 baseline）。</summary>
    public static async Task UploadPathsInBatchesAsync(
        IPage page,
        IReadOnlyList<string> videoPaths,
        TikTokPublishOptions options,
        IReadOnlyList<string>? titleCandidates,
        Action<string>? log,
        CancellationToken ct,
        int baselineReadyCount = 0,
        int expectedTotal = 0)
    {
        await UploadInBatchesAsync(
            page,
            videoPaths,
            batchSize: options.UploadBatchSize,
            stallSeconds: options.UploadBatchStallSeconds,
            maxRetries: options.UploadBatchMaxRetries,
            titleCandidates,
            log,
            ct,
            baselineReadyCount,
            expectedTotal);
    }

    private static async Task UploadInBatchesAsync(
        IPage page,
        IReadOnlyList<string> videoPaths,
        int batchSize,
        double stallSeconds,
        int maxRetries,
        IReadOnlyList<string>? titleCandidates,
        Action<string>? log,
        CancellationToken ct,
        int baselineReadyCount = 0,
        int expectedTotal = 0)
    {
        var total = videoPaths.Count;
        if (total == 0)
            throw new InvalidOperationException("未提供 TikTok 视频文件");

        batchSize = Math.Clamp(batchSize, 1, 20);
        maxRetries = Math.Clamp(maxRetries, 1, 10);
        stallSeconds = Math.Clamp(stallSeconds, 20, 600);
        baselineReadyCount = Math.Max(0, baselineReadyCount);
        // 进度分母使用短剧总集数（补传时=已就绪 baseline + 本次补传数，通常等于总集数）。
        var grandTotal = Math.Max(expectedTotal, baselineReadyCount + total);

        var batches = new List<List<string>>();
        for (var i = 0; i < total; i += batchSize)
            batches.Add(videoPaths.Skip(i).Take(batchSize).ToList());

        log?.Invoke(
            $"分批上传：短剧总集数 {grandTotal}，本次补传 {total} 集，每批 {batchSize} 个，分 {batches.Count} 批顺序上传" +
            (baselineReadyCount > 0 ? $"（已有 {baselineReadyCount} 集就绪）。" : "。"));

        var readyDone = baselineReadyCount;
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
                var batchStallSeconds = TikTokBrowserActions.ResolveUploadStallSeconds(
                    stallSeconds, batch.Count, batch);
                var outcome = await WaitBatchAsync(
                    page,
                    targetReady,
                    grandTotal,
                    titleCandidates,
                    batchStallSeconds,
                    log,
                    ct);

                if (outcome == BatchWaitOutcome.Done)
                {
                    readyDone = targetReady;
                    log?.Invoke(
                        $"第 {batchIndex + 1}/{batches.Count} 批上传完成（已就绪 {readyDone}/{grandTotal}）。");
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
            await TikTokBrowserActions.FeedVideoFilesToInputAsync(page, input, resolved, ct);
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

    private static readonly string[] UploadingMarkers =
    {
        "上传中", "正在上传", "处理中", "等待中", "Uploading", "Transcoding", "Processing",
    };

    private static async Task<BatchWaitOutcome> WaitBatchAsync(
        IPage page,
        int targetReady,
        int grandTotal,
        IReadOnlyList<string>? titleCandidates,
        double stallSeconds,
        Action<string>? log,
        CancellationToken ct,
        double pollSeconds = 3.0)
    {
        (int? Ready, int Percent, int RowCount, bool Uploading)? lastSig = null;
        var lastChange = DateTime.UtcNow;
        var lastLog = "";
        var doneStreak = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string bodyText;
            try { bodyText = await page.Locator("body").InnerTextAsync(new() { Timeout = 3000 }); }
            catch { bodyText = ""; }

            TikTokBrowserActions.ThrowIfTikTokCrashText(bodyText);
            TikTokBrowserActions.ThrowIfDailyEpisodeLimitText(bodyText);

            if (bodyText.Contains("上传失败", StringComparison.Ordinal) ||
                bodyText.Contains("Upload failed", StringComparison.OrdinalIgnoreCase))
                return BatchWaitOutcome.Stuck;

            // 编辑页正片表格是虚拟化列表，可见文本只包含视口内的行；
            // 就绪计数必须结合 aria-rowcount（全量行数）与上传中标记判断，不能只数可见行。
            var ready = TikTokBrowserActions.ExtractReadyUploadedVideoCount(bodyText, titleCandidates);
            var percent = TikTokBrowserActions.ExtractTotalUploadPercent(bodyText);
            var rowCount = await ReadUploadTableRowCountAsync(page);
            var uploading = UploadingMarkers.Any(m => bodyText.Contains(m, StringComparison.OrdinalIgnoreCase));

            if (ready is not null && ready.Value >= targetReady)
                return BatchWaitOutcome.Done;

            if (rowCount >= targetReady && !uploading)
            {
                // 全量行数已达标且页面无上传中标记；连续确认多轮，防止虚拟化把正在上传的行滚出视口造成误判。
                doneStreak++;
                if (doneStreak >= 3)
                    return BatchWaitOutcome.Done;
            }
            else
            {
                doneStreak = 0;
            }

            var sig = (ready, percent, rowCount, uploading);
            if (lastSig != sig)
            {
                lastSig = sig;
                lastChange = DateTime.UtcNow;
            }
            else if ((DateTime.UtcNow - lastChange).TotalSeconds >= stallSeconds)
            {
                return BatchWaitOutcome.Stuck;
            }

            var readyLabel = ready?.ToString() ?? "识别中";
            var msg =
                $"⏳ 等待本批上传：已就绪 {readyLabel}/{grandTotal} 集（本批目标 {targetReady}，表格 {rowCount} 行，" +
                $"{(uploading ? "仍有上传中" : "无上传中标记")}）。";
            if (msg != lastLog)
            {
                log?.Invoke(msg);
                lastLog = msg;
            }

            await page.WaitForTimeoutAsync((int)(pollSeconds * 1000));
        }
    }

    /// <summary>读取正片表格全量行数（aria-rowcount 覆盖虚拟化未渲染的行）；非编辑页返回 0。</summary>
    private static async Task<int> ReadUploadTableRowCountAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<int>(
                """
                () => {
                  const t = document.querySelector('.semi-table-body table');
                  if (t && t.getAttribute('aria-rowcount')) return +t.getAttribute('aria-rowcount');
                  return document.querySelectorAll('.semi-table-body tr.semi-table-row').length;
                }
                """);
        }
        catch
        {
            return 0;
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
