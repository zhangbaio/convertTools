using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services.TikTok;

/// <summary>分批上传策略（移植自 Python <c>batch_upload_service.py</c>）。</summary>
public static class TikTokBatchUploadService
{
    private static readonly Regex EpisodeNumberPattern = new(@"第\s*(\d+)\s*集", RegexOptions.Compiled);

    internal static bool ShouldUseBatchedUpload(TikTokPublishOptions options, int fileCount)
    {
        var batchSize = Math.Clamp(options.UploadBatchSize, 1, 20);
        return options.UseBatchUpload || fileCount > batchSize;
    }

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

                var accepted = await FeedBatchAsync(page, batch, targetReady, log, ct);
                var outcome = BatchWaitOutcome.Stuck;
                if (accepted)
                {
                    var batchStallSeconds = TikTokBrowserActions.ResolveUploadStallSeconds(
                        stallSeconds, batch.Count, batch);
                    outcome = await WaitBatchAsync(
                        page,
                        targetReady,
                        grandTotal,
                        titleCandidates,
                        batchStallSeconds,
                        log,
                        ct);
                }

                if (outcome == BatchWaitOutcome.Done)
                {
                    readyDone = targetReady;
                    log?.Invoke(
                        $"第 {batchIndex + 1}/{batches.Count} 批上传完成（已就绪 {readyDone}/{grandTotal}）。");
                    break;
                }

                if (accepted)
                {
                    log?.Invoke($"⚠️ 第 {batchIndex + 1} 批检测到卡死，删除本批并重传（第 {attempt}/{maxRetries} 次）。");
                    var batchEpisodes = batch.Select(EpisodeNumber).Where(n => n > 0).ToList();
                    await DeleteBatchRowsAsync(page, batchEpisodes, log, ct);
                }
                else
                {
                    // 文件选择命令成功返回后，页面可能只是延迟渲染视频行。此时再次选择
                    // 同一批会把已经被平台接收的文件重复加入列表。状态不确定时必须失败
                    // 关闭，交给下一次编辑流程先核对并修复列表，不能盲目重传。
                    throw new InvalidOperationException(
                        $"TikTok 分批上传状态不确定：第 {batchIndex + 1} 批（{labels}）" +
                        "执行文件选择后，页面未在确认时间内显示对应视频行。" +
                        "已停止自动重选，避免重复上传；请重新执行编辑，程序会先检查并修复重复或乱序集数。");
                }
                if (attempt >= maxRetries)
                {
                    throw new InvalidOperationException(
                        accepted
                            ? $"TikTok 分批上传失败：第 {batchIndex + 1} 批（{labels}）重试 {maxRetries} 次仍卡死。"
                            : $"TikTok 分批上传失败：第 {batchIndex + 1} 批（{labels}）执行文件选择 {maxRetries} 次后，页面仍未出现视频行或上传状态。");
                }
            }
        }
    }

    private static async Task<bool> FeedBatchAsync(
        IPage page,
        IReadOnlyList<string> batch,
        int targetReady,
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

        log?.Invoke($"已向浏览器选择本批 {batch.Count} 个文件，正在确认 TikTok 页面已接收。");
        await page.WaitForTimeoutAsync(1500);
        if (await WaitForBatchAcceptedAsync(page, batch, targetReady, ct))
        {
            log?.Invoke($"TikTok 页面已接收本批 {batch.Count} 个文件。");
            return true;
        }

        log?.Invoke("⚠️ 文件选择命令已执行，但 TikTok 页面未出现本批视频行或上传状态，按未接收处理。");
        return false;
    }

    private static async Task<bool> WaitForBatchAcceptedAsync(
        IPage page,
        IReadOnlyList<string> batch,
        int targetReady,
        CancellationToken ct,
        int timeoutSeconds = 25)
    {
        var names = batch
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, timeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            string bodyText;
            try { bodyText = await page.Locator("body").InnerTextAsync(new() { Timeout = 3000 }); }
            catch { bodyText = ""; }

            TikTokBrowserActions.ThrowIfTikTokCrashText(bodyText);
            await TikTokBrowserActions.ThrowIfDailyEpisodeLimitAsync(page).ConfigureAwait(false);
            if (bodyText.Contains("上传失败", StringComparison.Ordinal) ||
                bodyText.Contains("Upload failed", StringComparison.OrdinalIgnoreCase))
                return false;

            var rowCount = await ReadUploadTableRowCountAsync(page);
            if (rowCount >= targetReady ||
                names.Any(name => bodyText.Contains(name, StringComparison.OrdinalIgnoreCase)))
                return true;

            var activity = TikTokUploadProgressParser.DetectUploadActivity(
                bodyText,
                await TikTokBrowserActions.ReadUploadTableTextsAsync(page));
            if (activity.Uploading || activity.WaitingCount > 0)
                return true;

            await page.WaitForTimeoutAsync(500);
        }

        return false;
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
            await TikTokBrowserActions.ThrowIfDailyEpisodeLimitAsync(page).ConfigureAwait(false);

            if (bodyText.Contains("上传失败", StringComparison.Ordinal) ||
                bodyText.Contains("Upload failed", StringComparison.OrdinalIgnoreCase))
                return BatchWaitOutcome.Stuck;

            // 编辑页正片表格是虚拟化列表，可见文本只包含视口内的行；
            // 就绪计数必须结合 aria-rowcount（全量行数）与上传中标记判断，不能只数可见行。
            var ready = TikTokBrowserActions.ExtractReadyUploadedVideoCount(bodyText, titleCandidates);
            var percent = TikTokBrowserActions.ExtractTotalUploadPercent(bodyText);
            var rowCount = await ReadUploadTableRowCountAsync(page);
            var activity = TikTokUploadProgressParser.DetectUploadActivity(
                bodyText,
                await TikTokBrowserActions.ReadUploadTableTextsAsync(page));
            var uploading = activity.Uploading;

            if (TikTokUploadProgressParser.IsUploadComplete(ready, targetReady, activity))
                return BatchWaitOutcome.Done;

            if (rowCount >= targetReady && !uploading && activity.WaitingCount == 0)
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
                $"{(uploading ? "仍有上传中" : "无上传中标记")}，等待中 {activity.WaitingCount} 个）。";
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
            var result = await ClickBatchDeleteButtonAsync(page, targets);

            if (result == "no-table")
                throw new InvalidOperationException("删除本批行失败：未找到正片列表（semi-table）。");
            if (result.StartsWith("none:", StringComparison.Ordinal))
                break;
            if (!result.StartsWith("clicked:", StringComparison.Ordinal))
                throw new InvalidOperationException($"删除本批行失败：{result}");

            await page.WaitForTimeoutAsync(500);
            await TikTokBrowserActions.ConfirmDeleteDialogIfPresentAsync(page, ct);
            await page.WaitForTimeoutAsync(700);
            deleted++;
        }

        if (deleted >= 200)
            throw new InvalidOperationException("删除本批行次数异常超限，已中止。");

        log?.Invoke($"已删除本批 {deleted} 行（集号 {string.Join(", ", targets)}）。");
        return deleted;
    }

    private static async Task<string> ClickBatchDeleteButtonAsync(
        IPage page,
        IReadOnlyList<int> targets)
    {
        return await page.EvaluateAsync<string>(
            """
            async (targetValues) => {
              const targets = new Set((targetValues || []).map((value) => Number(value)).filter((value) => value > 0));
              if (targets.size === 0) return 'none:no-targets';

              const body = document.querySelector('.semi-table-body');
              if (!body) return 'no-table';

              const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const rows = () => Array.from(body.querySelectorAll('tr.semi-table-row'));
              const rowEpisode = (tr) => {
                const text = normalize(Array.from(tr.querySelectorAll('td'))
                  .map((td) => td.textContent || '')
                  .join(' '));
                const fileMatch = text.match(/-\s*第\s*(\d+)\s*集/);
                if (fileMatch) return Number.parseInt(fileMatch[1], 10) || 0;

                const matches = Array.from(text.matchAll(/第\s*(\d+)\s*集/g));
                if (matches.length === 0) return 0;
                return Number.parseInt(matches[matches.length - 1][1], 10) || 0;
              };
              const visibleEpisodes = () => rows().map(rowEpisode).filter((episode) => episode > 0);
              const hoverRow = (tr) => {
                try { tr.scrollIntoView({ block: 'center', inline: 'nearest' }); } catch {}
                for (const type of ['mouseover', 'mouseenter']) {
                  try { tr.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, view: window })); } catch {}
                }
              };
              const findDeleteButton = (tr) => {
                const icon = tr.querySelector('[data-icon="X"],[data-testid="X"],[data-icon="Backspace"],[data-testid="Backspace"]');
                const iconButton = icon?.closest('button,[role="button"]');
                if (iconButton) return iconButton;

                return Array.from(tr.querySelectorAll('button,[role="button"]'))
                  .find((button) => {
                    const text = normalize(button.textContent);
                    return /删除|Delete|Remove/i.test(text)
                      || button.querySelector('[data-icon="X"],[data-testid="X"],[data-icon="Backspace"],[data-testid="Backspace"]');
                  }) || null;
              };
              const clickDelete = async (target) => {
                hoverRow(target.tr);
                await sleep(60);
                const button = findDeleteButton(target.tr);
                if (!button) return `no-button:${target.episode}`;
                if (button.disabled || button.getAttribute('aria-disabled') === 'true') {
                  return `disabled:${target.episode}`;
                }

                try { button.scrollIntoView({ block: 'center', inline: 'nearest' }); } catch {}
                for (const type of ['mouseover', 'mouseenter', 'mousedown', 'mouseup']) {
                  try { button.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, view: window })); } catch {}
                }
                button.click();
                return `clicked:${target.episode}`;
              };
              const pickTarget = () => {
                const candidates = rows()
                  .map((tr) => ({ tr, episode: rowEpisode(tr) }))
                  .filter((item) => targets.has(item.episode))
                  .sort((a, b) => b.episode - a.episode);
                return candidates[0] || null;
              };
              const scrollAndPick = async (top) => {
                body.scrollTop = Math.max(0, top);
                try { body.dispatchEvent(new Event('scroll', { bubbles: true })); } catch {}
                for (let i = 0; i < 8; i++) {
                  await sleep(120);
                  const target = pickTarget();
                  if (target) return clickDelete(target);
                }
                return null;
              };

              const maxTarget = Math.max(...targets);
              const firstRow = rows()[0];
              const rowHeight = Math.max(32, Math.round(firstRow?.getBoundingClientRect().height || 48));
              const scrollTargets = [
                body.scrollHeight,
                Math.max(0, (maxTarget - 1) * rowHeight),
                0,
                body.scrollHeight
              ];

              for (const targetTop of scrollTargets) {
                const result = await scrollAndPick(targetTop);
                if (result) return result;
              }

              return `none:${visibleEpisodes().join(',')}`;
            }
            """,
            targets);
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
