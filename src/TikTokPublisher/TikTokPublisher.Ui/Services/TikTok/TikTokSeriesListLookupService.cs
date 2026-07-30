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
        CancellationToken ct)
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

        await page.WaitForTimeoutAsync(1400).ConfigureAwait(false);
        if (IsLoginPage(page.Url))
            throw new InvalidOperationException("TikTok 登录态失效，请先重新登录当前账号。");

        foreach (var selector in new[] { "tbody tr", "[role='row']", "tr" })
        {
            var rows = page.Locator(selector);
            var count = Math.Min(await rows.CountAsync().ConfigureAwait(false), 100);
            var matches = new List<TikTokSeriesListRow>();
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
                if (!lines.Contains(newTitle, StringComparer.Ordinal))
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

            if (matches.Count > 0)
                return matches
                    .DistinctBy(match => string.Join(
                        "\n",
                        match.Title,
                        match.SeriesId,
                        match.DetailUrl,
                        match.RawText))
                    .ToArray();
        }

        return [];
    }

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
}
