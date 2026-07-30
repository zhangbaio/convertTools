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
