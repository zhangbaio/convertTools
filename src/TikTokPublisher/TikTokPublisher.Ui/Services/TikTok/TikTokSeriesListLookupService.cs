using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Ui.Services.TikTok;

internal sealed record TikTokSeriesListRow(
    string Title,
    string PlatformStatus,
    string SeriesId,
    string DetailUrl,
    string RawText);

internal static class TikTokSeriesListLookupService
{
    private static readonly TimeSpan SearchResultTimeout = TimeSpan.FromSeconds(15);
    private const int SearchPollIntervalMs = 350;

    private static readonly Regex SeriesIdPattern =
        new(@"\b(\d{16,20})\b", RegexOptions.Compiled);

    private static readonly string[] StatusMarkers =
    [
        "视频检测中",
        "检测中",
        "待审核",
        "审核中",
        "发布中",
        "已发布",
        "草稿",
        "分发受限",
        "Published",
        "Reviewing",
        "In review",
        "Draft",
    ];

    public static async Task OpenAsync(
        IPage page,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await page.GotoAsync(TikTokUrls.DefaultSeriesListUrl, new PageGotoOptions
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
            // TikTok 原创管理是 SPA，持续网络请求不影响列表检索。
        }

        await TikTokBrowserActions.DismissFloatingAssistantAsync(page, log).ConfigureAwait(false);
        if (IsLoginPage(page.Url))
            throw new InvalidOperationException("TikTok 登录态失效，请先重新登录当前账号。");

        var search = await FindSearchInputAsync(page).ConfigureAwait(false);
        if (search is null)
            throw new InvalidOperationException("原创管理页面未找到剧集搜索框。");
    }

    public static async Task<IReadOnlyList<TikTokSeriesListRow>> EnumerateAllAsync(
        IPage page,
        Action<string>? log,
        CancellationToken ct)
    {
        var search = await FindSearchInputAsync(page).ConfigureAwait(false)
            ?? throw new InvalidOperationException("原创管理页面未找到剧集搜索框。");
        await search.FillAsync(string.Empty).ConfigureAwait(false);
        try { await search.PressAsync("Enter").ConfigureAwait(false); }
        catch { /* 部分版本清空后会自动查询。 */ }
        await page.WaitForTimeoutAsync(500).ConfigureAwait(false);
        await GoToFirstPageAsync(page, ct).ConfigureAwait(false);

        var collected = new List<TikTokSeriesListRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? previousFingerprint = null;
        int? expectedTotal = null;

        for (var pageNumber = 1; pageNumber <= 1000; pageNumber++)
        {
            ct.ThrowIfCancellationRequested();
            var pageRows = await ScanCurrentPageRowsAsync(page, ct).ConfigureAwait(false);
            if (pageRows.Count == 0)
            {
                if (pageNumber == 1)
                    throw new InvalidOperationException("原创管理列表没有读取到任何剧集行。");
                break;
            }

            foreach (var row in pageRows)
            {
                var key = !string.IsNullOrWhiteSpace(row.SeriesId)
                    ? $"id:{row.SeriesId}"
                    : $"url:{row.DetailUrl}|title:{NormalizeTitle(row.Title)}";
                if (seen.Add(key))
                    collected.Add(row);
            }

            log?.Invoke(
                $"已读取原创管理第 {pageNumber} 页：本页 {pageRows.Count} 个，累计 {collected.Count} 个。");

            expectedTotal ??= await TryReadTotalCountAsync(page).ConfigureAwait(false);
            var fingerprint = BuildPageFingerprint(pageRows);
            if (string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("原创管理分页未发生变化，已停止以避免重复扫描。");
            previousFingerprint = fingerprint;

            var next = await FindNextPageButtonAsync(page).ConfigureAwait(false);
            var isLastPage = next is not null &&
                             await IsDisabledAsync(next).ConfigureAwait(false);
            if (next is null || isLastPage)
            {
                if (expectedTotal is > 0 && collected.Count < expectedTotal.Value)
                {
                    var reason = next is null
                        ? "未找到“下一页”按钮"
                        : "“下一页”按钮已禁用";
                    throw new InvalidOperationException(
                        $"原创管理分页读取不完整：页面显示共 {expectedTotal.Value} 个，" +
                        $"当前只读取到 {collected.Count} 个，且{reason}。本次检查已停止，避免遗漏剧集。");
                }
                break;
            }

            await next.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 }).ConfigureAwait(false);
            await next.ClickAsync(new() { Timeout = 10000 }).ConfigureAwait(false);
            var changed = await WaitForPageFingerprintChangeAsync(page, fingerprint, ct)
                .ConfigureAwait(false);
            if (!changed)
                throw new InvalidOperationException("点击原创管理下一页后，列表内容未在规定时间内更新。");
        }

        return collected;
    }

    public static async Task<IReadOnlyList<TikTokSeriesListRow>> SearchExactAsync(
        IPage page,
        string newTitle,
        CancellationToken ct,
        Action<string>? log = null)
    {
        ct.ThrowIfCancellationRequested();
        var search = await FindSearchInputAsync(page).ConfigureAwait(false)
            ?? throw new InvalidOperationException("原创管理页面未找到剧集搜索框。");

        await search.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 }).ConfigureAwait(false);
        await search.FillAsync(string.Empty).ConfigureAwait(false);
        await page.WaitForTimeoutAsync(150).ConfigureAwait(false);
        await search.FillAsync(newTitle).ConfigureAwait(false);
        try
        {
            await search.PressAsync("Enter").ConfigureAwait(false);
        }
        catch
        {
            // 部分版本使用输入防抖，不依赖回车。
        }

        var stopwatch = Stopwatch.StartNew();
        var attempt = 0;
        var maxObservedRows = 0;
        while (stopwatch.Elapsed < SearchResultTimeout)
        {
            ct.ThrowIfCancellationRequested();
            if (IsLoginPage(page.Url))
                throw new InvalidOperationException("TikTok 登录态失效，请先重新登录当前账号。");

            attempt++;
            var scan = await ScanExactRowsAsync(page, newTitle, ct).ConfigureAwait(false);
            maxObservedRows = Math.Max(maxObservedRows, scan.ObservedRowCount);
            if (scan.Matches.Count > 0)
            {
                if (attempt > 1)
                {
                    log?.Invoke(
                        $"TikTok 搜索结果已加载：{newTitle}，等待 {stopwatch.Elapsed:mm\\:ss\\.f}，" +
                        $"重试 {attempt - 1} 次。");
                }
                return scan.Matches;
            }

            if (attempt == 1)
                log?.Invoke($"TikTok 搜索结果尚未出现，继续等待精确剧名：{newTitle}");

            await page.WaitForTimeoutAsync(SearchPollIntervalMs).ConfigureAwait(false);
        }

        log?.Invoke(
            $"TikTok 搜索等待超时，未读取到完全一致的新剧名：{newTitle}；" +
            $"等待 {SearchResultTimeout.TotalSeconds:0} 秒，最多观察到 {maxObservedRows} 行。");
        return [];
    }

    private static async Task<IReadOnlyList<TikTokSeriesListRow>> ScanCurrentPageRowsAsync(
        IPage page,
        CancellationToken ct)
    {
        ILocator? rows = null;
        foreach (var selector in new[] { "tbody tr", "[role='rowgroup'] [role='row']", "tr" })
        {
            var candidate = page.Locator(selector);
            if (await candidate.CountAsync().ConfigureAwait(false) == 0)
                continue;
            rows = candidate;
            break;
        }

        if (rows is null)
            return [];

        var results = new List<TikTokSeriesListRow>();
        var count = Math.Min(await rows.CountAsync().ConfigureAwait(false), 200);
        for (var index = 0; index < count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows.Nth(index);
            string rawText;
            try
            {
                if (!await row.IsVisibleAsync().ConfigureAwait(false))
                    continue;
                rawText = await row.InnerTextAsync(new() { Timeout = 1500 }).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            var lines = rawText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var title = ExtractTitle(lines, rawText);
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var urls = await FindSeriesUrlsAsync(page, row).ConfigureAwait(false);
            var ids = urls
                .Select(ExtractSeriesId)
                .Concat(SeriesIdPattern.Matches(rawText).Select(match => match.Groups[1].Value))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var detailUrl = urls.FirstOrDefault() ?? BuildFallbackUrl(rawText, ids);
            results.Add(new TikTokSeriesListRow(
                title,
                ExtractStatus(lines, rawText),
                ids.Length == 1 ? ids[0] : string.Empty,
                detailUrl,
                rawText));
        }

        return results
            .DistinctBy(row => string.Join(
                "\n",
                row.Title,
                row.SeriesId,
                row.DetailUrl))
            .ToArray();
    }

    private static string ExtractTitle(IReadOnlyList<string> lines, string rawText)
    {
        var idLineIndex = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (!SeriesIdPattern.IsMatch(lines[index]))
                continue;
            idLineIndex = index;
            break;
        }

        if (idLineIndex > 0)
            return NormalizeTitle(lines[idLineIndex - 1]);

        var inline = Regex.Match(
            rawText,
            @"^\s*(?<title>.+?)\s+ID\s*\d{16,20}\b",
            RegexOptions.IgnoreCase);
        if (inline.Success)
            return NormalizeTitle(inline.Groups["title"].Value);

        return lines
            .Select(NormalizeTitle)
            .FirstOrDefault(line =>
                !string.IsNullOrWhiteSpace(line) &&
                !SeriesIdPattern.IsMatch(line) &&
                !Regex.IsMatch(line, @"^\d+\s*集$")) ?? string.Empty;
    }

    private static string BuildPageFingerprint(IReadOnlyList<TikTokSeriesListRow> rows) =>
        string.Join(
            "|",
            rows.Select(row =>
                !string.IsNullOrWhiteSpace(row.SeriesId)
                    ? row.SeriesId
                    : $"{NormalizeTitle(row.Title)}:{row.DetailUrl}"));

    private static async Task GoToFirstPageAsync(IPage page, CancellationToken ct)
    {
        foreach (var selector in new[]
                 {
                     ".semi-page .semi-page-item",
                     ".semi-pagination .semi-pagination-item",
                     "[class*='pagination'] button",
                 })
        {
            var candidates = page.Locator(selector);
            var count = await candidates.CountAsync().ConfigureAwait(false);
            for (var index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = candidates.Nth(index);
                string text;
                try
                {
                    if (!await candidate.IsVisibleAsync().ConfigureAwait(false))
                        continue;
                    text = (await candidate.InnerTextAsync(new() { Timeout = 1200 })
                            .ConfigureAwait(false))
                        .Trim();
                }
                catch
                {
                    continue;
                }

                if (!string.Equals(text, "1", StringComparison.Ordinal))
                    continue;

                var className =
                    await candidate.GetAttributeAsync("class").ConfigureAwait(false) ??
                    string.Empty;
                var ariaCurrent =
                    await candidate.GetAttributeAsync("aria-current").ConfigureAwait(false);
                if (className.Contains("active", StringComparison.OrdinalIgnoreCase) ||
                    className.Contains("selected", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ariaCurrent, "page", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                await candidate.ScrollIntoViewIfNeededAsync(
                        new() { Timeout = 10000 })
                    .ConfigureAwait(false);
                await candidate.ClickAsync(new() { Timeout = 10000 }).ConfigureAwait(false);
                await page.WaitForTimeoutAsync(500).ConfigureAwait(false);
                return;
            }
        }
    }

    private static async Task<ILocator?> FindNextPageButtonAsync(IPage page)
    {
        foreach (var selector in new[]
                 {
                     ".semi-page-item-next",
                     ".semi-page-next",
                     ".semi-pagination-item-next",
                     ".semi-page-item[aria-label*='next' i]",
                     ".semi-pagination [class*='next' i]",
                     "[aria-label='Next page']",
                     "[aria-label='Next Page']",
                     "[aria-label='下一页']",
                     "button[aria-label='next']",
                     "button[title='下一页']",
                 })
        {
            var candidates = page.Locator(selector);
            var count = await candidates.CountAsync().ConfigureAwait(false);
            for (var index = 0; index < count; index++)
            {
                var candidate = candidates.Nth(index);
                try
                {
                    if (await candidate.IsVisibleAsync().ConfigureAwait(false))
                        return candidate;
                }
                catch
                {
                    // 分页可能正在重绘，继续尝试其他候选。
                }
            }
        }

        return null;
    }

    private static async Task<int?> TryReadTotalCountAsync(IPage page)
    {
        string bodyText;
        try
        {
            bodyText = await page.Locator("body").InnerTextAsync(
                    new() { Timeout = 3000 })
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        var totals = new List<int>();
        foreach (var pattern in new[]
                 {
                     @"共\s*(?<count>\d[\d,]*)\s*条",
                     @"共\s*(?<count>\d[\d,]*)\s*个",
                     @"\btotal\s*:?\s*(?<count>\d[\d,]*)\b",
                 })
        {
            var matches = Regex.Matches(
                bodyText,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (Match match in matches)
            {
                var raw = match.Groups["count"].Value.Replace(",", string.Empty);
                if (int.TryParse(raw, out var count) && count > 0)
                    totals.Add(count);
            }
        }

        return totals.Count == 0 ? null : totals.Max();
    }

    private static async Task<bool> IsDisabledAsync(ILocator locator)
    {
        try
        {
            if (await locator.IsDisabledAsync().ConfigureAwait(false))
                return true;
        }
        catch
        {
            // li 等非表单节点不支持 IsDisabled，继续检查属性。
        }

        var ariaDisabled = await locator.GetAttributeAsync("aria-disabled").ConfigureAwait(false);
        var className = await locator.GetAttributeAsync("class").ConfigureAwait(false) ?? string.Empty;
        return string.Equals(ariaDisabled, "true", StringComparison.OrdinalIgnoreCase) ||
               className.Contains("disabled", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> WaitForPageFingerprintChangeAsync(
        IPage page,
        string previousFingerprint,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(15))
        {
            ct.ThrowIfCancellationRequested();
            var rows = await ScanCurrentPageRowsAsync(page, ct).ConfigureAwait(false);
            if (rows.Count > 0 &&
                !string.Equals(
                    BuildPageFingerprint(rows),
                    previousFingerprint,
                    StringComparison.Ordinal))
            {
                return true;
            }

            await page.WaitForTimeoutAsync(300).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<SeriesRowsScanResult> ScanExactRowsAsync(
        IPage page,
        string newTitle,
        CancellationToken ct)
    {
        var matches = new List<TikTokSeriesListRow>();
        var observedRowCount = 0;
        foreach (var selector in new[] { "tbody tr", "[role='row']", "tr" })
        {
            var rows = page.Locator(selector);
            var count = Math.Min(await rows.CountAsync().ConfigureAwait(false), 100);
            observedRowCount = Math.Max(observedRowCount, count);
            for (var index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var row = rows.Nth(index);
                string rawText;
                try
                {
                    rawText = await row.InnerTextAsync(new() { Timeout = 1200 }).ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }

                var lines = rawText
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!await ContainsExactTitleAsync(row, lines, newTitle).ConfigureAwait(false))
                    continue;

                var urls = await FindSeriesUrlsAsync(page, row).ConfigureAwait(false);
                var ids = urls
                    .Select(ExtractSeriesId)
                    .Concat(SeriesIdPattern.Matches(rawText).Select(match => match.Groups[1].Value))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var detailUrl = urls.FirstOrDefault() ?? BuildFallbackUrl(rawText, ids);
                matches.Add(new TikTokSeriesListRow(
                    newTitle,
                    ExtractStatus(lines, rawText),
                    ids.Length == 1 ? ids[0] : string.Empty,
                    detailUrl,
                    rawText));
            }
        }

        return new SeriesRowsScanResult(
            matches
                .DistinctBy(match => string.Join(
                    "\n",
                    match.Title,
                    match.SeriesId,
                    match.DetailUrl,
                    match.RawText))
                .ToArray(),
            observedRowCount);
    }

    private static async Task<bool> ContainsExactTitleAsync(
        ILocator row,
        IReadOnlyList<string> lines,
        string newTitle)
    {
        var normalizedTitle = NormalizeTitle(newTitle);
        if (lines.Any(line =>
                string.Equals(
                    NormalizeTitle(line),
                    normalizedTitle,
                    StringComparison.Ordinal)))
        {
            return true;
        }

        try
        {
            return await row
                .GetByText(newTitle, new() { Exact = true })
                .CountAsync()
                .ConfigureAwait(false) > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeTitle(string value) =>
        Regex.Replace(
                (value ?? string.Empty)
                .Replace('\u00A0', ' ')
                .Replace("\u200B", string.Empty, StringComparison.Ordinal)
                .Replace("\u200C", string.Empty, StringComparison.Ordinal)
                .Replace("\u200D", string.Empty, StringComparison.Ordinal)
                .Replace("\uFEFF", string.Empty, StringComparison.Ordinal),
                @"\s+",
                " ")
            .Trim();

    private static async Task<ILocator?> FindSearchInputAsync(IPage page)
    {
        foreach (var selector in new[]
                 {
                     "input[placeholder*='短剧']",
                     "input[placeholder*='搜索']",
                     "input[placeholder*='查询']",
                     "input[type='search']",
                 })
        {
            var locator = page.Locator(selector).First;
            if (await locator.CountAsync().ConfigureAwait(false) == 0)
                continue;
            try
            {
                if (await locator.IsVisibleAsync().ConfigureAwait(false))
                    return locator;
            }
            catch
            {
                // 尝试下一个定位器。
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<string>> FindSeriesUrlsAsync(
        IPage page,
        ILocator row)
    {
        var links = row.Locator("a[href*='/series/detail/'], a[href*='/series/draft/']");
        var count = await links.CountAsync().ConfigureAwait(false);
        var urls = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var href = await links.Nth(index).GetAttributeAsync("href").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(href))
                continue;
            urls.Add(new Uri(new Uri(page.Url), href).ToString());
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ExtractStatus(IReadOnlyList<string> lines, string rawText)
    {
        foreach (var marker in StatusMarkers)
        {
            var exact = lines.FirstOrDefault(line =>
                string.Equals(line, marker, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
                return marker;
        }

        foreach (var marker in StatusMarkers.Where(marker =>
                     !string.Equals(marker, "Published", StringComparison.OrdinalIgnoreCase)))
        {
            if (rawText.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return marker;
        }

        return Regex.IsMatch(rawText, @"\bPublished\b", RegexOptions.IgnoreCase)
            ? "Published"
            : "状态未知";
    }

    private static string ExtractSeriesId(string url)
    {
        var match = SeriesIdPattern.Match(url ?? string.Empty);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string BuildFallbackUrl(string rawText, IReadOnlyList<string> ids)
    {
        if (ids.Count != 1)
            return string.Empty;
        var segment = rawText.Contains("草稿", StringComparison.OrdinalIgnoreCase) ||
                      rawText.Contains("Draft", StringComparison.OrdinalIgnoreCase)
            ? "draft"
            : "detail";
        return $"https://www.tiktokdramacenter.com/series/{segment}/{ids[0]}";
    }

    private static bool IsLoginPage(string? url) =>
        (url ?? string.Empty).Contains("/login", StringComparison.OrdinalIgnoreCase);

    private sealed record SeriesRowsScanResult(
        IReadOnlyList<TikTokSeriesListRow> Matches,
        int ObservedRowCount);
}
