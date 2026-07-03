using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Ui.Services.TikTok;

/// <summary>编辑流表单与视频补传（移植自 Python <c>fill_tiktok_edit_publish_form</c>）。</summary>
public static partial class TikTokBrowserActions
{
    private static readonly Regex UploadedEpisodeLinePattern = new(@"(?:^|\n)\s*第\s*\d+\s*集", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex EpisodeInNamePattern = new(@"第\s*(\d+)\s*集", RegexOptions.Compiled);
    private static readonly string[] CompletedUploadStatusMarkers =
    {
        "草稿", "已上传", "上传完成", "Draft", "Uploaded", "Upload complete", "Completed",
    };

    public sealed record EditVideoRow(int Slot, int Real);

    public sealed record EditFlowVideoState(
        IReadOnlyList<int> UploadedIndexes,
        int? UploadedCount,
        bool EmptyUpload);

    public static async Task FillEditPublishFormAsync(
        IPage page,
        TikTokPublishPayload payload,
        TikTokPublishOptions options,
        TikTokPublishRecommendation recommendation,
        string coverPath,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Log(log, "TikTok 已切换到草稿编辑流程，跳过合同、剧名、简介的重新填写。");
        await EnsureEditFlowVideosCompleteAsync(page, payload, options, log, ct);
        await UploadCoverAsync(page, coverPath, log, ct);
        await FillSharedPublishFieldsAsync(page, payload, options, recommendation, log, ct);
        Log(log, "TikTok 编辑页表单已填写完成。");
    }

    public static async Task EnsureEditFlowVideosCompleteAsync(
        IPage page,
        TikTokPublishPayload payload,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var uploadPaths = payload.UploadVideoPaths.Count > 0
            ? payload.UploadVideoPaths.ToList()
            : payload.VideoPaths.ToList();
        var expectedCount = Math.Max(payload.EpisodeCount, uploadPaths.Count);
        if (expectedCount <= 0) return;

        if (uploadPaths.Count < expectedCount)
        {
            throw new InvalidOperationException(
                $"TikTok 编辑补传缺少本地视频文件：短剧总集数 {expectedCount}，本地可上传视频 {uploadPaths.Count} 个。" +
                "请先补齐源视频后再执行编辑发布。");
        }

        List<EditVideoRow> rows;
        try
        {
            rows = await ReadEditVideoRowsAsync(page, ct);
        }
        catch (Exception ex)
        {
            rows = new List<EditVideoRow>();
            Log(log, $"读取草稿正片列表失败，回退按数量补传（{ex.Message}）。");
        }

        if (rows.Count > 0)
        {
            var aligned = 0;
            foreach (var row in rows)
            {
                if (row.Slot == row.Real)
                    aligned = row.Slot;
                else
                    break;
            }

            List<string> missingPaths;
            if (aligned < rows.Count)
            {
                var firstBad = aligned + 1;
                Log(log,
                    $"检测到集数错位：第{firstBad}集起与真实集数不一致（共 {rows.Count} 行），" +
                    $"删除第{firstBad}集及其后所有行并从第{firstBad}集起重传补齐。");
                await DeleteEditVideoRowsFromSlotAsync(page, aligned, log, ct);
                missingPaths = uploadPaths.Skip(aligned).ToList();
            }
            else if (rows.Count >= expectedCount)
            {
                Log(log, $"TikTok 草稿视频已对齐且完整：{rows.Count}/{expectedCount}。");
                return;
            }
            else
            {
                missingPaths = uploadPaths.Skip(rows.Count).ToList();
                Log(log, $"TikTok 草稿已对齐 {rows.Count}/{expectedCount}，补传剩余 {missingPaths.Count} 集。");
            }

            if (missingPaths.Count == 0) return;
            await UploadLocalVideosAsync(page, missingPaths, waitForFinish: false, log, ct);
            await WaitVideoUploadFinishedAsync(
                page,
                expectedCount,
                PayloadTitleCandidates(payload),
                options.UploadStallSeconds,
                log,
                ct);
            return;
        }

        var detected = await DetectEditFlowVideoStateAsync(page, payload, ct);
        List<string> pathsToUpload;
        if (detected.UploadedIndexes.Count > 0)
        {
            pathsToUpload = ResolveMissingUploadPathsByIndexes(uploadPaths, detected.UploadedIndexes);
            if (pathsToUpload.Count == 0)
            {
                Log(log, $"TikTok 草稿视频已完整：{detected.UploadedIndexes.Count}/{expectedCount}。");
                return;
            }
            Log(log,
                $"TikTok 草稿当前已上传 {detected.UploadedIndexes.Count}/{expectedCount} 个视频，缺失集数：" +
                $"{string.Join(", ", ExtractEpisodeIndexesFromPaths(pathsToUpload))}，开始补传。");
        }
        else if (detected.UploadedCount is null)
        {
            if (!detected.EmptyUpload)
            {
                Log(log, "未能识别 TikTok 草稿当前已上传视频数，跳过补传检查。");
                return;
            }
            pathsToUpload = uploadPaths.ToList();
            Log(log, $"TikTok 草稿当前未上传正片视频，开始重新上传全部 {pathsToUpload.Count} 个。");
        }
        else
        {
            if (detected.UploadedCount.Value >= expectedCount)
            {
                Log(log, $"TikTok 草稿视频已完整：{detected.UploadedCount}/{expectedCount}。");
                return;
            }
            pathsToUpload = ResolveMissingUploadPaths(uploadPaths, detected.UploadedCount.Value);
            if (pathsToUpload.Count == 0)
            {
                Log(log, $"TikTok 草稿视频已完整：{detected.UploadedCount}/{expectedCount}。");
                return;
            }
            Log(log,
                $"TikTok 草稿当前已上传 {detected.UploadedCount}/{expectedCount} 个视频，继续补传剩余 {pathsToUpload.Count} 个。");
        }

        await UploadLocalVideosAsync(page, pathsToUpload, waitForFinish: false, log, ct);
        Log(log, "TikTok 编辑流程已触发补传，开始等待视频补传完成。");
        await WaitVideoUploadFinishedAsync(
            page,
            expectedCount,
            PayloadTitleCandidates(payload),
            options.UploadStallSeconds,
            log,
            ct);
    }

    public static async Task<List<EditVideoRow>> ReadEditVideoRowsAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (await page.Locator(".semi-table-body").CountAsync() == 0)
            {
                var tab = page.GetByText("内容上传", new() { Exact = false }).First;
                if (await tab.CountAsync() > 0)
                {
                    await tab.ClickAsync(new() { Timeout = 5000 });
                    await page.WaitForTimeoutAsync(1500);
                }
            }
        }
        catch { /* ignore */ }

        try
        {
            await page.Locator(".semi-table-body").First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 8000 });
        }
        catch { /* continue */ }

        var rawRows = await page.EvaluateAsync<List<Dictionary<string, int>>>(
            """
            async () => {
              const body = document.querySelector(".semi-table-body");
              if (!body) return [];
              const collected = {};
              const grab = () => {
                body.querySelectorAll("tr.semi-table-row").forEach((tr) => {
                  const cell = tr.querySelector("td");
                  const txt = (cell && cell.textContent || "").trim();
                  const m = txt.match(/第\s*(\d+)\s*集.*?-第\s*(\d+)\s*集/);
                  if (m) collected[+m[1]] = { slot: +m[1], real: +m[2] };
                });
              };
              let last = -1, stable = 0;
              for (let i = 0; i < 80 && stable < 3; i++) {
                grab();
                const n = Object.keys(collected).length;
                if (n === last) stable++; else { stable = 0; last = n; }
                body.scrollTop = body.scrollTop + 400;
                await new Promise((r) => setTimeout(r, 150));
              }
              body.scrollTop = 0;
              return Object.values(collected).sort((a, b) => a.slot - b.slot);
            }
            """);

        return (rawRows ?? new List<Dictionary<string, int>>())
            .Select(item =>
            {
                if (!item.TryGetValue("slot", out var slot) || !item.TryGetValue("real", out var real))
                    return null;
                return new EditVideoRow(slot, real);
            })
            .Where(row => row is not null)
            .Cast<EditVideoRow>()
            .ToList();
    }

    public static async Task<int> DeleteEditVideoRowsFromSlotAsync(
        IPage page,
        int keepCount,
        Action<string>? log,
        CancellationToken ct)
    {
        var deleted = 0;
        for (var guard = 0; guard < 500; guard++)
        {
            ct.ThrowIfCancellationRequested();
            var count = await page.EvaluateAsync<int>(
                """
                () => {
                  const t = document.querySelector('.semi-table-body table');
                  if (t && t.getAttribute('aria-rowcount')) return +t.getAttribute('aria-rowcount');
                  return document.querySelectorAll('.semi-table-body tr.semi-table-row').length;
                }
                """);
            if (count <= keepCount) break;

            var clicked = await page.EvaluateAsync<bool>(
                """
                () => {
                  const body = document.querySelector('.semi-table-body');
                  if (!body) return false;
                  body.scrollTop = body.scrollHeight;
                  const rows = body.querySelectorAll('tr.semi-table-row');
                  const last = rows[rows.length - 1];
                  if (!last) return false;
                  const icon = last.querySelector('[data-icon="Backspace"],[data-testid="Backspace"]');
                  const btn = icon ? icon.closest('button') : null;
                  if (!btn) return false;
                  btn.click();
                  return true;
                }
                """);
            if (!clicked)
                throw new InvalidOperationException("未找到错位行的删除按钮（Backspace 图标）。");

            await page.WaitForTimeoutAsync(500);
            await ConfirmDeleteDialogIfPresentAsync(page, ct);
            await page.WaitForTimeoutAsync(800);

            var newCount = await page.EvaluateAsync<int>(
                """
                () => {
                  const t = document.querySelector('.semi-table-body table');
                  if (t && t.getAttribute('aria-rowcount')) return +t.getAttribute('aria-rowcount');
                  return document.querySelectorAll('.semi-table-body tr.semi-table-row').length;
                }
                """);
            if (newCount >= count)
                throw new InvalidOperationException("点击删除后行数未减少，可能删除控件失配，已中止。");
            deleted++;
        }

        if (deleted > 0)
            Log(log, $"已删除错位的 {deleted} 行（保留前 {keepCount} 集）。");
        return deleted;
    }

    public static async Task<EditFlowVideoState> DetectEditFlowVideoStateAsync(
        IPage page,
        TikTokPublishPayload payload,
        CancellationToken ct,
        int settleAttempts = 8,
        int settleIntervalMs = 800)
    {
        var titleCandidates = PayloadTitleCandidates(payload);
        IReadOnlyList<int> lastIndexes = Array.Empty<int>();
        int? lastCount = null;
        var lastEmpty = false;

        for (var attempt = 0; attempt < Math.Max(1, settleAttempts); attempt++)
        {
            ct.ThrowIfCancellationRequested();
            lastIndexes = await DetectUploadedEpisodeIndexesAsync(page, titleCandidates);
            if (lastIndexes.Count > 0)
                return new EditFlowVideoState(lastIndexes, null, false);

            lastCount = await DetectUploadedVideoCountAsync(page);
            if (lastCount is not null)
                return new EditFlowVideoState(Array.Empty<int>(), lastCount, false);

            lastEmpty = await DetectEmptyVideoUploadStateAsync(page);
            if (lastEmpty)
                return new EditFlowVideoState(Array.Empty<int>(), null, true);

            if (attempt < settleAttempts - 1)
                await page.WaitForTimeoutAsync(Math.Max(100, settleIntervalMs));
        }

        return new EditFlowVideoState(lastIndexes, lastCount, lastEmpty);
    }

    public static async Task<int?> DetectUploadedVideoCountAsync(IPage page)
    {
        try
        {
            var bodyText = await page.Locator("body").InnerTextAsync(new() { Timeout = 3000 });
            return ExtractUploadedVideoCountFromText(bodyText);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<IReadOnlyList<int>> DetectUploadedEpisodeIndexesAsync(
        IPage page,
        IReadOnlyList<string>? titleCandidates = null)
    {
        try
        {
            var bodyText = await page.Locator("body").InnerTextAsync(new() { Timeout = 3000 });
            return ExtractUploadedEpisodeIndexesFromText(bodyText, titleCandidates);
        }
        catch
        {
            return Array.Empty<int>();
        }
    }

    public static async Task<bool> DetectEmptyVideoUploadStateAsync(IPage page)
    {
        try
        {
            if (await page.GetByText("点击上传或拖拽视频到此处", new() { Exact = false }).CountAsync() > 0)
                return true;
        }
        catch { /* ignore */ }

        try
        {
            var bodyText = await page.Locator("body").InnerTextAsync(new() { Timeout = 3000 });
            if (bodyText.Contains("正片内容", StringComparison.Ordinal) &&
                !UploadedContentCountPattern.IsMatch(bodyText) &&
                bodyText.Contains("点击上传或拖拽视频到此处", StringComparison.Ordinal))
                return true;
        }
        catch { /* ignore */ }

        return false;
    }

    private static int? ExtractUploadedVideoCountFromText(string text)
    {
        var match = UploadedContentCountPattern.Match(text);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var headingCount))
            return headingCount;
        var lineMatches = UploadedEpisodeLinePattern.Matches(text);
        return lineMatches.Count > 0 ? lineMatches.Count : null;
    }

    private static List<int> ExtractUploadedEpisodeIndexesFromText(string text, IReadOnlyList<string>? titleCandidates)
    {
        var normalizedCandidates = titleCandidates?
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .ToList() ?? new List<string>();

        if (normalizedCandidates.Count > 0)
        {
            var filtered = new List<int>();
            var seen = new HashSet<int>();
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (!normalizedCandidates.Any(c => trimmed.Contains(c, StringComparison.Ordinal))) continue;
                var match = EpisodeInNamePattern.Match(trimmed);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out var value) || value <= 0 || seen.Contains(value))
                    continue;
                seen.Add(value);
                filtered.Add(value);
            }
            if (filtered.Count > 0) return filtered;
        }

        var values = new List<int>();
        var seenAll = new HashSet<int>();
        foreach (Match match in EpisodeInNamePattern.Matches(text))
        {
            if (!int.TryParse(match.Groups[1].Value, out var value) || value <= 0 || seenAll.Contains(value))
                continue;
            seenAll.Add(value);
            values.Add(value);
        }
        return values;
    }

    private static List<string> ResolveMissingUploadPaths(IReadOnlyList<string> videoPaths, int uploadedCount) =>
        videoPaths.Skip(Math.Max(0, uploadedCount)).ToList();

    private static List<string> ResolveMissingUploadPathsByIndexes(
        IReadOnlyList<string> videoPaths,
        IReadOnlyList<int> uploadedIndexes)
    {
        var uploadedSet = uploadedIndexes.Where(i => i > 0).Select(i => Math.Max(1, i)).ToHashSet();
        var missing = new List<string>();
        for (var i = 0; i < videoPaths.Count; i++)
        {
            var episodeIndex = ExtractEpisodeIndexFromPath(videoPaths[i]) ?? (i + 1);
            if (!uploadedSet.Contains(episodeIndex))
                missing.Add(videoPaths[i]);
        }
        return missing;
    }

    private static List<int> ExtractEpisodeIndexesFromPaths(IEnumerable<string> videoPaths)
    {
        var indexes = new List<int>();
        var fallback = 1;
        foreach (var path in videoPaths)
        {
            indexes.Add(ExtractEpisodeIndexFromPath(path) ?? fallback);
            fallback++;
        }
        return indexes;
    }

    private static int? ExtractEpisodeIndexFromPath(string path)
    {
        var match = EpisodeInNamePattern.Match(Path.GetFileName(path));
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) && value > 0 ? value : null;
    }

    private static async Task ConfirmDeleteDialogIfPresentAsync(IPage page, CancellationToken ct)
    {
        foreach (var text in new[] { "确认删除", "确认", "确定", "删除" })
        {
            try
            {
                var dlg = page.Locator("[role='dialog'] button").Filter(new() { HasText = text }).First;
                if (await dlg.CountAsync() > 0)
                {
                    await ClickLocatorAsync(dlg, ct);
                    return;
                }
            }
            catch { /* try next */ }
        }
    }
}
