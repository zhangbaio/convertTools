using System.Text.Json;
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

    private static readonly Regex EditVideoRowPattern = new(
        @"第\s*(\d+)\s*集.*?-第\s*(\d+)\s*集",
        RegexOptions.Compiled | RegexOptions.Singleline);

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
        Log(log, "TikTok 已切换到草稿编辑流程，保留已有合同和剧名；简介为空时自动补全。");
        await EnsureEditFlowVideosCompleteAsync(page, payload, options, log, ct);
        await EnsureEditCoverUploadedAsync(page, coverPath, log, ct);
        await EnsureEditDescriptionFilledAsync(page, payload.Description, log, ct);
        await FillSharedPublishFieldsAsync(page, payload, options, recommendation, log, ct);
        Log(log, "TikTok 编辑页表单已填写完成。");
    }

    private static async Task EnsureEditDescriptionFilledAsync(
        IPage page,
        string description,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureEditBaseInfoSectionAsync(page, log, ct);

        var field = page.Locator("#description").First;
        await field.WaitForAsync(new()
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10000,
        });
        await field.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });

        var currentDescription = await field.InputValueAsync();
        if (!string.IsNullOrWhiteSpace(currentDescription))
        {
            Log(log, "TikTok 编辑页已有剧集简介，保持原内容不变。");
            return;
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException(
                "TikTok 编辑页剧集简介为空，本地项目也没有可用于补全的简介。请先生成或填写简介后重试。");
        }

        await field.FillAsync(description);
        await BlurActiveElementAsync(page);
        await page.WaitForTimeoutAsync(500);

        var filledDescription = await field.InputValueAsync();
        if (string.IsNullOrWhiteSpace(filledDescription))
            throw new InvalidOperationException("TikTok 编辑页剧集简介自动补全失败，请检查页面后重试。");

        Log(log, "TikTok 编辑页原剧集简介为空，已使用本地简介自动补全。");
    }

    private static async Task EnsureEditCoverUploadedAsync(
        IPage page,
        string coverPath,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureEditBaseInfoSectionAsync(page, log, ct);

        if (await IsCoverAlreadyUploadedAsync(page))
        {
            Log(log, "TikTok 编辑页封面已存在，跳过补传。");
            await VerifyCoverUploadCompleteAsync(page, log, ct);
            return;
        }

        Log(log, "TikTok 编辑页未检测到封面，开始补传封面。");
        await UploadCoverAsync(page, coverPath, log, ct);
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

        await EnsureEditContentUploadTabAsync(page, ct);

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

        if (rows.Count == 0)
        {
            rows = await TryReadEditVideoRowsFromBodyAsync(page, ct);
            if (rows.Count > 0)
                Log(log, $"正片表格读取失败，已从页面正文解析 {rows.Count} 行 slot/real。");
        }

        var tableRowCount = await ReadEditVideoTableRowCountAsync(page);
        if (rows.Count > 0)
        {
            if (tableRowCount > expectedCount && rows.Count < tableRowCount)
            {
                throw new InvalidOperationException(
                    $"TikTok 草稿正片已有 {tableRowCount} 行，超过总集数 {expectedCount}，且只解析到 {rows.Count} 行。" +
                    "为避免误删或重复补传，请先在页面删除多余视频后重试。");
            }

            if (rows.Count > expectedCount)
            {
                Log(log, $"TikTok 草稿正片已有 {rows.Count}/{expectedCount} 行，删除第 {expectedCount + 1} 集及其后的多余行。");
                await DeleteEditVideoRowsFromSlotAsync(page, expectedCount, log, ct);
                return;
            }

            if (tableRowCount >= expectedCount && rows.Count < expectedCount)
            {
                Log(log,
                    $"TikTok 草稿正片表格行数已达 {tableRowCount}/{expectedCount}，但当前只解析到 {rows.Count} 行；" +
                    "跳过自动补传，避免把未解析到的现有视频重复上传。");
                return;
            }

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
                // 按真实集数索引精确比对，本地有文件的缺失集全部补传；
                // 仅对本地也没有源文件的集数给出警告（不能因本地少一集就整体放弃补传）。
                missingPaths = ResolveMissingUploadPathsByIndexes(
                    uploadPaths, rows.Select(r => r.Real).ToList());
                Log(log, missingPaths.Count > 0
                    ? $"TikTok 草稿已上传 {rows.Count}/{expectedCount}，缺失集数：" +
                      $"{string.Join(", ", ExtractEpisodeIndexesFromPaths(missingPaths))}，开始补传。"
                    : $"TikTok 草稿已上传 {rows.Count}/{expectedCount}。");
            }

            WarnLocallyUnavailableEpisodes(
                rows.Select(r => r.Real), missingPaths, uploadPaths, expectedCount, log);
            if (missingPaths.Count == 0)
                return;

            await UploadEditFlowMissingVideosAsync(
                page, missingPaths, expectedCount, payload, options, log, ct);
            return;
        }

        if (tableRowCount > expectedCount)
        {
            throw new InvalidOperationException(
                $"TikTok 草稿正片已有 {tableRowCount} 行，超过总集数 {expectedCount}。请先在页面删除多余视频后重试。");
        }

        if (tableRowCount >= expectedCount)
        {
            Log(log,
                $"TikTok 草稿正片表格行数已达 {tableRowCount}/{expectedCount}，但未解析到完整集号；" +
                "跳过自动补传，避免重复上传。");
            return;
        }

        var detected = await DetectEditFlowVideoStateAsync(page, payload, ct);
        List<string> pathsToUpload;
        if (detected.UploadedIndexes.Count > 0)
        {
            if (tableRowCount > detected.UploadedIndexes.Count)
            {
                pathsToUpload = ResolveMissingUploadPaths(uploadPaths, tableRowCount);
                Log(log,
                    $"TikTok 草稿正片表格有 {tableRowCount}/{expectedCount} 行，但只识别到 {detected.UploadedIndexes.Count} 个集号；" +
                    $"按尾部续传剩余 {pathsToUpload.Count} 个，避免重复补传已存在行。");
            }
            else
            {
                pathsToUpload = ResolveMissingUploadPathsByIndexes(uploadPaths, detected.UploadedIndexes);
            }
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

        WarnLocallyUnavailableEpisodes(
            detected.UploadedIndexes, pathsToUpload, uploadPaths, expectedCount, log);
        if (pathsToUpload.Count == 0)
            return;

        await UploadEditFlowMissingVideosAsync(
            page, pathsToUpload, expectedCount, payload, options, log, ct);
    }

    /// <summary>总集数大于「平台已传 + 本地可补传」时，提示哪些集连本地源文件都缺失。</summary>
    private static void WarnLocallyUnavailableEpisodes(
        IEnumerable<int> uploadedEpisodes,
        IReadOnlyList<string> pathsToUpload,
        IReadOnlyList<string> localPaths,
        int expectedCount,
        Action<string>? log)
    {
        if (localPaths.Count >= expectedCount)
            return;

        var covered = uploadedEpisodes.Where(i => i > 0).ToHashSet();
        foreach (var episode in ExtractEpisodeIndexesFromPaths(pathsToUpload))
            covered.Add(episode);
        foreach (var episode in ExtractEpisodeIndexesFromPaths(localPaths))
            covered.Add(episode);

        var unavailable = Enumerable.Range(1, expectedCount).Where(i => !covered.Contains(i)).ToList();
        if (unavailable.Count == 0)
            return;

        Log(log,
            $"⚠️ 短剧总集数 {expectedCount}，本地仅 {localPaths.Count} 个视频文件，" +
            $"第 {string.Join("、", unavailable)} 集缺少本地源文件无法补传，请补齐后重新执行上传。");
    }

    private static async Task EnsureEditContentUploadTabAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (await page.Locator(".semi-table-body").CountAsync() > 0)
                return;

            var tab = page.GetByText("内容上传", new() { Exact = false }).First;
            if (await tab.CountAsync() > 0)
            {
                await tab.ClickAsync(new() { Timeout = 5000 });
                await page.WaitForTimeoutAsync(1500);
            }
        }
        catch { /* ignore */ }

        try
        {
            await page.Locator(".semi-table-body").First.WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = 8000 });
        }
        catch { /* continue */ }
    }

    private static async Task EnsureEditBaseInfoSectionAsync(
        IPage page,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var tab = page.GetByText("基础信息", new() { Exact = false }).First;
            if (await tab.CountAsync() > 0 && await tab.IsVisibleAsync())
            {
                await ClickWithFallbackAsync(tab, ct);
                await page.WaitForTimeoutAsync(500);
            }
        }
        catch { /* ignore */ }

        try
        {
            await page.EvaluateAsync(
                """
                () => {
                  window.scrollTo(0, 0);
                  for (const el of document.querySelectorAll("*")) {
                    const style = getComputedStyle(el);
                    const overflowY = style.overflowY || "";
                    if (!/(auto|scroll)/.test(overflowY)) continue;
                    if (el.scrollHeight <= el.clientHeight + 10) continue;
                    el.scrollTop = 0;
                  }
                }
                """);
            await page.WaitForTimeoutAsync(300);
        }
        catch { /* ignore */ }

        foreach (var selector in new[]
                 {
                     "#coverStruct",
                     "[x-field-id='coverStruct']",
                     ".uploadField-Xm2Vjl",
                 })
        {
            try
            {
                var field = page.Locator(selector).First;
                if (await field.CountAsync() == 0) continue;
                await field.ScrollIntoViewIfNeededAsync(new() { Timeout = 5000 });
                await page.WaitForTimeoutAsync(300);
                return;
            }
            catch { /* try next */ }
        }

        try
        {
            var label = page.GetByText("封面图", new() { Exact = false }).First;
            if (await label.CountAsync() > 0 && await label.IsVisibleAsync())
            {
                await label.ScrollIntoViewIfNeededAsync(new() { Timeout = 5000 });
                await page.WaitForTimeoutAsync(300);
                return;
            }
        }
        catch { /* ignore */ }

        Log(log, "TikTok 编辑页未定位到封面区域，继续尝试通过上传控件补传。");
    }

    private static async Task UploadEditFlowMissingVideosAsync(
        IPage page,
        IReadOnlyList<string> missingPaths,
        int expectedCount,
        TikTokPublishPayload payload,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        if (missingPaths.Count == 0) return;

        var titleCandidates = PayloadTitleCandidates(payload);
        var baseline = await DetectUploadedVideoCountAsync(page)
            ?? Math.Max(0, expectedCount - missingPaths.Count);
        var batchSize = Math.Clamp(options.UploadBatchSize, 1, 20);

        if (missingPaths.Count == 1)
        {
            await UploadLocalVideosAsync(page, missingPaths, waitForFinish: false, log, ct);
            Log(log, "TikTok 编辑流程已触发补传，开始等待视频补传完成。");
            await WaitVideoUploadFinishedAsync(
                page, expectedCount, titleCandidates, options.UploadStallSeconds, log, ct,
                videoPaths: missingPaths);
            await EnsureEditVideoTableCountMatchesAsync(page, expectedCount, log);
            return;
        }

        Log(log,
            $"TikTok 编辑补传 {missingPaths.Count} 集，按配置每批 {batchSize} 个顺序上传（当前已就绪 {baseline}/{expectedCount}）。");
        await TikTokBatchUploadService.UploadPathsInBatchesAsync(
            page,
            missingPaths,
            options,
            titleCandidates,
            log,
            ct,
            baseline,
            expectedTotal: expectedCount);

        Log(log, "TikTok 编辑流程分批补传已提交，开始等待全部视频上传完成。");
        await WaitVideoUploadFinishedAsync(
            page, expectedCount, titleCandidates, options.UploadStallSeconds, log, ct,
            videoPaths: missingPaths);
        await EnsureEditVideoTableCountMatchesAsync(page, expectedCount, log);
    }

    private static async Task EnsureEditVideoTableCountMatchesAsync(
        IPage page,
        int expectedCount,
        Action<string>? log)
    {
        var rowCount = await ReadEditVideoTableRowCountAsync(page);
        if (rowCount <= 0)
            return;

        if (rowCount > expectedCount)
        {
            throw new InvalidOperationException(
                $"TikTok 编辑补传后正片已有 {rowCount} 行，超过总集数 {expectedCount}。" +
                "已中止完成标记，请删除多余视频后重新执行编辑剧集。");
        }

        if (rowCount < expectedCount)
        {
            throw new InvalidOperationException(
                $"TikTok 编辑补传后正片只有 {rowCount}/{expectedCount} 行，仍未补齐。" +
                "已中止完成标记，请重新执行编辑剧集。");
        }

        Log(log, $"TikTok 编辑补传后正片行数校验通过：{rowCount}/{expectedCount}。");
    }

    public static async Task<List<EditVideoRow>> ReadEditVideoRowsAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureEditContentUploadTabAsync(page, ct);

        JsonElement rawRows;
        try
        {
            rawRows = await page.EvaluateAsync<JsonElement>(
                """
                async () => {
                  const body = document.querySelector(".semi-table-body");
                  if (!body) return [];
                  const collected = {};
                  const grab = () => {
                    body.querySelectorAll("tr.semi-table-row").forEach((tr) => {
                      const txt = Array.from(tr.querySelectorAll("td"))
                        .map((td) => (td.textContent || "").trim())
                        .filter(Boolean)
                        .join(" ");
                      const m = txt.match(/第\s*(\d+)\s*集[\s\S]*?-第\s*(\d+)\s*集/);
                      if (m) collected[+m[1]] = { slot: +m[1], real: +m[2] };
                    });
                  };
                  body.scrollTop = 0;
                  let last = -1, stable = 0;
                  for (let i = 0; i < 100 && stable < 4; i++) {
                    grab();
                    const n = Object.keys(collected).length;
                    if (n === last) stable++; else { stable = 0; last = n; }
                    body.scrollTop = body.scrollTop + 350;
                    await new Promise((r) => setTimeout(r, 120));
                  }
                  body.scrollTop = 0;
                  return Object.values(collected).sort((a, b) => a.slot - b.slot);
                }
                """);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Evaluate 正片列表失败：{ex.Message}", ex);
        }

        return ParseEditVideoRows(rawRows);
    }

    private static async Task<int> ReadEditVideoTableRowCountAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<int>(
                """
                () => {
                  const table = document.querySelector('.semi-table-body table');
                  const raw = table && table.getAttribute('aria-rowcount');
                  if (raw) {
                    const parsed = Number.parseInt(raw, 10);
                    if (Number.isFinite(parsed) && parsed > 0) return parsed;
                  }
                  return document.querySelectorAll('.semi-table-body tr.semi-table-row').length;
                }
                """);
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<List<EditVideoRow>> TryReadEditVideoRowsFromBodyAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var bodyText = await page.Locator("body").InnerTextAsync(new() { Timeout = 10000 });
            return ParseEditVideoRowsFromText(bodyText);
        }
        catch
        {
            return new List<EditVideoRow>();
        }
    }

    internal static List<EditVideoRow> ParseEditVideoRows(JsonElement rawRows)
    {
        var rows = new List<EditVideoRow>();
        if (rawRows.ValueKind != JsonValueKind.Array)
            return rows;

        foreach (var item in rawRows.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (!TryReadIntProperty(item, "slot", out var slot) || !TryReadIntProperty(item, "real", out var real))
                continue;
            if (slot <= 0 || real <= 0)
                continue;
            rows.Add(new EditVideoRow(slot, real));
        }

        return rows.OrderBy(row => row.Slot).ToList();
    }

    internal static List<EditVideoRow> ParseEditVideoRowsFromText(string bodyText)
    {
        var bySlot = new Dictionary<int, EditVideoRow>();
        foreach (var line in bodyText.Split('\n'))
        {
            var match = EditVideoRowPattern.Match(line);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups[1].Value, out var slot) ||
                !int.TryParse(match.Groups[2].Value, out var real))
                continue;
            bySlot[slot] = new EditVideoRow(slot, real);
        }

        return bySlot.Values.OrderBy(row => row.Slot).ToList();
    }

    private static bool TryReadIntProperty(JsonElement item, string name, out int value)
    {
        value = 0;
        if (!item.TryGetProperty(name, out var prop))
            return false;

        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(prop.GetString(), out value),
            _ => false,
        };
    }

    public static async Task<int> DeleteEditVideoRowsFromSlotAsync(
        IPage page,
        int keepCount,
        Action<string>? log,
        CancellationToken ct)
    {
        await EnsureEditContentUploadTabAsync(page, ct);
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

            var clickResult = await ClickEditVideoDeleteButtonBeyondKeepAsync(page, keepCount, count);
            if (!clickResult.StartsWith("clicked:", StringComparison.Ordinal))
                throw new InvalidOperationException($"未找到错位行的删除按钮（{clickResult}）。");

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

    private static async Task<string> ClickEditVideoDeleteButtonBeyondKeepAsync(
        IPage page,
        int keepCount,
        int rowCount)
    {
        return await page.EvaluateAsync<string>(
            """
            async (args) => {
              const keepCount = Number(args.keepCount || 0);
              const rowCount = Number(args.rowCount || 0);
              const body = document.querySelector('.semi-table-body');
              if (!body) return 'no-table';

              const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const rowSlot = (tr) => {
                const text = normalize(Array.from(tr.querySelectorAll('td'))
                  .map((td) => td.textContent || '')
                  .join(' '));
                const textMatch = text.match(/第\s*(\d+)\s*集/);
                if (textMatch) return Number.parseInt(textMatch[1], 10) || 0;

                const raw = tr.getAttribute('aria-rowindex')
                  || tr.getAttribute('data-row-key')
                  || tr.getAttribute('data-row-index')
                  || '';
                const parsed = Number.parseInt(raw, 10);
                return Number.isFinite(parsed) ? parsed : 0;
              };
              const rows = () => Array.from(body.querySelectorAll('tr.semi-table-row'));
              const visibleSlots = () => rows().map(rowSlot).filter((slot) => slot > 0);
              const hoverRow = (tr) => {
                try { tr.scrollIntoView({ block: 'center', inline: 'nearest' }); } catch {}
                for (const type of ['mouseover', 'mouseenter']) {
                  try { tr.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, view: window })); } catch {}
                }
              };
              const findDeleteButton = (tr) => {
                const icon = tr.querySelector('[data-icon="Backspace"],[data-testid="Backspace"],[data-icon="X"],[data-testid="X"]');
                const iconButton = icon?.closest('button,[role="button"]');
                if (iconButton) return iconButton;

                return Array.from(tr.querySelectorAll('button,[role="button"]'))
                  .find((button) => {
                    const text = normalize(button.textContent);
                    return /删除|Delete|Remove/i.test(text)
                      || button.querySelector('[data-icon="Backspace"],[data-testid="Backspace"],[data-icon="X"],[data-testid="X"]');
                  }) || null;
              };
              const clickDelete = async (target) => {
                hoverRow(target.tr);
                await sleep(60);
                const button = findDeleteButton(target.tr);
                if (!button) return `no-button:${target.slot}`;
                if (button.disabled || button.getAttribute('aria-disabled') === 'true') {
                  return `disabled:${target.slot}`;
                }

                try { button.scrollIntoView({ block: 'center', inline: 'nearest' }); } catch {}
                for (const type of ['mouseover', 'mouseenter', 'mousedown', 'mouseup']) {
                  try { button.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, view: window })); } catch {}
                }
                button.click();
                return `clicked:${target.slot}`;
              };
              const pickTarget = () => {
                const candidates = rows()
                  .map((tr) => ({ tr, slot: rowSlot(tr) }))
                  .filter((item) => item.slot > keepCount)
                  .sort((a, b) => b.slot - a.slot);
                if (candidates.length === 0) return null;

                const nearTail = candidates.find((item) => rowCount <= 0 || item.slot >= rowCount - 2);
                return nearTail || candidates[0];
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

              const firstRow = rows()[0];
              const rowHeight = Math.max(32, Math.round(firstRow?.getBoundingClientRect().height || 48));
              const scrollTargets = [
                body.scrollHeight,
                Math.max(0, (rowCount - 1) * rowHeight),
                Math.max(0, (keepCount + 1) * rowHeight),
                body.scrollHeight
              ];

              for (const targetTop of scrollTargets) {
                const result = await scrollAndPick(targetTop);
                if (result) return result;
              }

              return `not-rendered:${visibleSlots().join(',')}`;
            }
            """,
            new { keepCount, rowCount });
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

    internal static async Task ConfirmDeleteDialogIfPresentAsync(IPage page, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var text in new[] { "确认删除", "确认", "确定", "删除" })
            {
                try
                {
                    var buttons = page.Locator("[role='dialog'] button").Filter(new() { HasText = text });
                    var count = Math.Min(await buttons.CountAsync(), 8);
                    for (var index = 0; index < count; index++)
                    {
                        var dlg = buttons.Nth(index);
                        if (!await dlg.IsVisibleAsync(new() { Timeout = 300 }))
                            continue;

                        await ClickLocatorAsync(dlg, ct);
                        await page.WaitForTimeoutAsync(200);
                        return;
                    }
                }
                catch { /* try next */ }
            }

            await page.WaitForTimeoutAsync(200);
        }
    }
}
