using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services.TikTok;

/// <summary>草稿编辑流程路由（移植自 Python <c>edit_flow_service.py</c>）。</summary>
public static class TikTokEditFlowService
{
    private static readonly Regex DetailIdPattern = new(@"\b(\d{16,20})\b", RegexOptions.Compiled);
    private static readonly string[] DuplicateWarningMarkers = { "合同剧名重复", "请修改后重试" };

    public static async Task<bool> TryEnterExistingDraftFlowAsync(
        IPage page,
        string workflowProjectDir,
        TikTokPublishPayload payload,
        TikTokPublishOptions options,
        TikTokPublishRecommendation recommendation,
        string coverPath,
        Action<string>? log,
        CancellationToken ct,
        bool allowPlatformSearch)
    {
        if (await MaybeRouteDuplicateToEditFlowAsync(
                page, workflowProjectDir, payload, options, recommendation, coverPath, log, ct))
            return true;

        if (await MaybeUseCachedEditFlowAsync(
                page, workflowProjectDir, payload, options, recommendation, coverPath, log, ct))
            return true;

        if (!allowPlatformSearch) return false;

        return await MaybeSearchExistingSeriesThenEditAsync(
            page, workflowProjectDir, payload, options, recommendation, coverPath, log, ct);
    }

    public static async Task<bool> MaybeRouteDuplicateToEditFlowAsync(
        IPage page,
        string workflowProjectDir,
        TikTokPublishPayload payload,
        TikTokPublishOptions options,
        TikTokPublishRecommendation recommendation,
        string coverPath,
        Action<string>? log,
        CancellationToken ct)
    {
        if (!await HasDuplicateContractTitleWarningAsync(page, ct)) return false;

        var titleCandidates = NormalizeTitleCandidates(payload.Title, payload.OriginalTitle);
        var detailUrl = await DiscoverEditableDraftDetailUrlAsync(page, titleCandidates, log, ct);
        if (string.IsNullOrWhiteSpace(detailUrl))
            throw new InvalidOperationException("TikTok 检测到合同剧名重复，但在平台列表中未找到可编辑草稿。");

        TikTokUploadStateStore.RecordPlatformSeriesFound(
            workflowProjectDir, detailUrl, payload.Title, "duplicate_warning", titleCandidates);
        log?.Invoke($"TikTok 检测到合同剧名重复，切换到草稿编辑流程：{detailUrl}");
        await OpenEditPublishFlowAsync(
            page, detailUrl, payload, options, recommendation, coverPath, log, ct);
        return true;
    }

    public static async Task<bool> MaybeUseCachedEditFlowAsync(
        IPage page,
        string workflowProjectDir,
        TikTokPublishPayload payload,
        TikTokPublishOptions options,
        TikTokPublishRecommendation recommendation,
        string coverPath,
        Action<string>? log,
        CancellationToken ct)
    {
        var detailUrl = TikTokUploadStateStore.LoadCachedEditDetailUrl(workflowProjectDir);
        if (string.IsNullOrWhiteSpace(detailUrl)) return false;

        log?.Invoke($"TikTok 已命中本地草稿缓存，直接走编辑流程：{detailUrl}");
        await OpenEditPublishFlowAsync(
            page, detailUrl, payload, options, recommendation, coverPath, log, ct);
        return true;
    }

    public static async Task<bool> MaybeSearchExistingSeriesThenEditAsync(
        IPage page,
        string workflowProjectDir,
        TikTokPublishPayload payload,
        TikTokPublishOptions options,
        TikTokPublishRecommendation recommendation,
        string coverPath,
        Action<string>? log,
        CancellationToken ct)
    {
        var titleCandidates = NormalizeTitleCandidates(payload.Title, payload.OriginalTitle);
        if (titleCandidates.Count == 0) return false;

        log?.Invoke($"TikTok 检测到该项目曾执行过上传，先在平台搜索是否已存在：{string.Join(" / ", titleCandidates)}");
        var detailUrl = await DiscoverEditableDraftDetailUrlAsync(page, titleCandidates, log, ct);
        if (string.IsNullOrWhiteSpace(detailUrl))
        {
            TikTokUploadStateStore.RecordPlatformSeriesNotFound(workflowProjectDir, "pre_upload_search", titleCandidates);
            log?.Invoke("TikTok 平台未找到同名草稿，改走新建剧集上传流程");
            return false;
        }

        TikTokUploadStateStore.RecordPlatformSeriesFound(
            workflowProjectDir, detailUrl, titleCandidates[0], "pre_upload_search", titleCandidates);
        if (!string.IsNullOrWhiteSpace(payload.Title) &&
            !string.Equals(payload.Title, titleCandidates[0], StringComparison.Ordinal))
        {
            log?.Invoke(
                $"警告：平台草稿标题「{titleCandidates[0]}」与当前本地新剧名「{payload.Title}」不一致，" +
                "后续剧集比对将优先按平台真实标题识别。");
        }
        log?.Invoke($"TikTok 已在平台搜索到同名草稿，直接进入编辑流程：{detailUrl}");
        await OpenEditPublishFlowAsync(
            page, detailUrl, payload, options, recommendation, coverPath, log, ct);
        return true;
    }

    public static async Task OpenEditPublishFlowAsync(
        IPage page,
        string detailUrl,
        TikTokPublishPayload payload,
        TikTokPublishOptions options,
        TikTokPublishRecommendation recommendation,
        string coverPath,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await page.GotoAsync(detailUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60000,
        });
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 20000 }); }
        catch { /* SPA */ }
        await TikTokBrowserActions.DismissFloatingAssistantAsync(page, log);
        await TikTokBrowserActions.FillEditPublishFormAsync(
            page, payload, options, recommendation, coverPath, log, ct);
    }

    public static async Task<bool> HasDuplicateContractTitleWarningAsync(IPage page, CancellationToken ct, int timeoutMs = 2500)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(500, timeoutMs));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var marker in DuplicateWarningMarkers)
            {
                try
                {
                    var loc = page.Locator($"text={marker}").First;
                    if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync(new() { Timeout = 300 }))
                        return true;
                }
                catch { /* try next */ }
            }

            string bodyText;
            try { bodyText = await page.Locator("body").InnerTextAsync(new() { Timeout = 3000 }); }
            catch { bodyText = ""; }
            if (DuplicateWarningMarkers.All(marker => bodyText.Contains(marker, StringComparison.Ordinal)))
                return true;

            await Task.Delay(250, ct);
        }
        return false;
    }

    public static async Task<string> DiscoverEditableDraftDetailUrlAsync(
        IPage page,
        IReadOnlyList<string>? titleCandidates,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await page.GotoAsync(TikTokUrls.DefaultSeriesListUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60000,
        });
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }); }
        catch { /* SPA */ }
        await TikTokBrowserActions.DismissFloatingAssistantAsync(page, log);

        var normalized = NormalizeTitleCandidates(titleCandidates?.ToArray() ?? Array.Empty<string>());
        var searchKeys = ExpandTitleSearchKeywords(normalized);
        if (searchKeys.Count > 0)
        {
            foreach (var title in searchKeys)
            {
                ct.ThrowIfCancellationRequested();
                log?.Invoke($"TikTok 在原剧管理搜索：{title}");
                await ApplySeriesListSearchAsync(page, title, ct);
                var detailUrl = await FindMatchingDetailUrlAsync(page, title);
                if (!string.IsNullOrWhiteSpace(detailUrl))
                    return detailUrl;
            }
            return "";
        }

        return await FindFirstVisibleDetailUrlAsync(page);
    }

    private static List<string> ExpandTitleSearchKeywords(IReadOnlyList<string> titles)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var title in titles)
        {
            foreach (var candidate in ExpandSingleTitleSearchKeywords(title))
            {
                if (seen.Add(candidate)) results.Add(candidate);
            }
        }
        return results;
    }

    private static IEnumerable<string> ExpandSingleTitleSearchKeywords(string title)
    {
        var text = (title ?? "").Trim();
        if (string.IsNullOrEmpty(text)) yield break;
        yield return text;
        if (text.Length >= 6)
            yield return text[..6];
        if (text.Length >= 4)
            yield return text[..4];
    }

    private static async Task ApplySeriesListSearchAsync(IPage page, string title, CancellationToken ct)
    {
        var input = await FindSeriesListSearchInputAsync(page);
        if (input is null) return;
        await input.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        await input.FillAsync("");
        await page.WaitForTimeoutAsync(200);
        await input.FillAsync(title);
        try { await input.PressAsync("Enter"); }
        catch { /* ignore */ }
        await page.WaitForTimeoutAsync(1200);
    }

    private static async Task<ILocator?> FindSeriesListSearchInputAsync(IPage page)
    {
        foreach (var selector in new[]
                 {
                     "input[placeholder*='短剧']",
                     "input[placeholder*='查询']",
                     "input[type='search']",
                     "input[placeholder]",
                     "input[type='text']",
                     "input:not([type])",
                     "input",
                 })
        {
            var locator = page.Locator(selector);
            int count;
            try { count = Math.Min(await locator.CountAsync(), 12); }
            catch { continue; }

            for (var i = 0; i < count; i++)
            {
                var candidate = locator.Nth(i);
                try
                {
                    if (!await candidate.IsVisibleAsync(new() { Timeout = 300 })) continue;
                    var inputType = (await candidate.GetAttributeAsync("type") ?? "").Trim().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(inputType) && inputType is not ("text" or "search")) continue;
                    if (await candidate.GetAttributeAsync("disabled") is not null) continue;
                    if (await candidate.GetAttributeAsync("readonly") is not null) continue;
                    return candidate;
                }
                catch { /* try next */ }
            }
        }
        return null;
    }

    private static async Task<string> FindMatchingDetailUrlAsync(IPage page, string title)
    {
        var fromRow = await FindDetailUrlInRowsAsync(page, title, requireDraft: true);
        if (!string.IsNullOrWhiteSpace(fromRow)) return fromRow;

        if (title.Length >= 4)
        {
            var prefix = title[..Math.Min(4, title.Length)];
            fromRow = await FindDetailUrlInRowsAsync(page, prefix, requireDraft: true);
            if (!string.IsNullOrWhiteSpace(fromRow)) return fromRow;
        }

        string bodyText;
        try { bodyText = NormalizeWhitespace(await page.Locator("body").InnerTextAsync(new() { Timeout = 3000 })); }
        catch { bodyText = ""; }
        if (bodyText.Contains(title, StringComparison.Ordinal) || (title.Length >= 4 && bodyText.Contains(title[..4], StringComparison.Ordinal)))
        {
            var ids = DetailIdPattern.Matches(bodyText).Select(m => m.Groups[1].Value).ToList();
            if (ids.Count == 1)
                return $"{TikTokUrls.DefaultSeriesDraftUrl}/{ids[0]}";
        }
        return "";
    }

    private static async Task<string> FindDetailUrlInRowsAsync(IPage page, string titleFragment, bool requireDraft)
    {
        foreach (var selector in new[] { "tbody tr", "tr", "[role='row']" })
        {
            var rows = page.Locator(selector);
            var count = Math.Min(await rows.CountAsync(), 100);
            for (var i = 0; i < count; i++)
            {
                var row = rows.Nth(i);
                string text;
                try { text = NormalizeWhitespace(await row.InnerTextAsync(new() { Timeout = 500 })); }
                catch { continue; }
                if (string.IsNullOrEmpty(text) || !text.Contains(titleFragment, StringComparison.Ordinal)) continue;
                if (requireDraft && !text.Contains("草稿", StringComparison.Ordinal)) continue;
                var ids = DetailIdPattern.Matches(text).Select(m => m.Groups[1].Value).ToList();
                if (ids.Count > 0)
                    return $"{TikTokUrls.DefaultSeriesDraftUrl}/{ids[0]}";
            }
        }
        return "";
    }

    private static async Task<string> FindFirstVisibleDetailUrlAsync(IPage page)
    {
        foreach (var selector in new[] { "tbody tr", "tr", "[role='row']" })
        {
            var rows = page.Locator(selector);
            var count = Math.Min(await rows.CountAsync(), 100);
            for (var i = 0; i < count; i++)
            {
                var row = rows.Nth(i);
                string text;
                try { text = NormalizeWhitespace(await row.InnerTextAsync(new() { Timeout = 500 })); }
                catch { continue; }
                if (string.IsNullOrEmpty(text) || !text.Contains("草稿", StringComparison.Ordinal)) continue;
                var ids = DetailIdPattern.Matches(text).Select(m => m.Groups[1].Value).ToList();
                if (ids.Count > 0)
                    return $"{TikTokUrls.DefaultSeriesDraftUrl}/{ids[0]}";
            }
        }

        string bodyText;
        try { bodyText = await page.Locator("body").InnerTextAsync(new() { Timeout = 3000 }); }
        catch { bodyText = ""; }
        var bodyIds = DetailIdPattern.Matches(bodyText).Select(m => m.Groups[1].Value).ToList();
        return bodyIds.Count > 0 ? $"{TikTokUrls.DefaultSeriesDraftUrl}/{bodyIds[0]}" : "";
    }

    private static List<string> NormalizeTitleCandidates(params string?[] values)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var text = (value ?? "").Trim();
            if (string.IsNullOrEmpty(text) || seen.Contains(text)) continue;
            seen.Add(text);
            results.Add(text);
        }
        return results;
    }

    private static string NormalizeWhitespace(string text) =>
        string.Join(' ', (text ?? "").Replace('\u00a0', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
