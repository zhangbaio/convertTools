using System.Diagnostics;
using System.Text.Json;
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

internal sealed record TikTokSeriesListEnumerationAttempt(
    IReadOnlyList<TikTokSeriesListRow> Rows,
    int? ExpectedTotal,
    int RawVisibleRowCount,
    int SkippedRowCount,
    IReadOnlyDictionary<string, int> DuplicateKeyCounts,
    string EndReason)
{
    public bool IsComplete => ExpectedTotal is null or <= 0 || Rows.Count >= ExpectedTotal.Value;
}

internal sealed record TikTokSeriesPageScanResult(
    IReadOnlyList<TikTokSeriesListRow> Rows,
    int VisibleRowCount,
    int SkippedRowCount);

internal sealed record TikTokSeriesPageReadinessSnapshot(
    int? ActivePageNumber,
    int VisibleRowCount,
    string Fingerprint,
    string RangeText);

internal sealed record TikTokSeriesListEnumerationProgress(
    string PlatformStatus,
    int AttemptNumber,
    int CurrentPage,
    int? TotalPages,
    int? CurrentPageRowCount,
    int CollectedUniqueCount);

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
        CancellationToken ct,
        IReadOnlyList<string>? statusFilters = null,
        int? preferredPageSize = null,
        IProgress<TikTokSeriesListEnumerationProgress>? progress = null)
    {
        TikTokSeriesListEnumerationAttempt? previousAttempt = null;
        for (var attemptNumber = 1; attemptNumber <= 2; attemptNumber++)
        {
            await ConfigureListAsync(
                    page,
                    statusFilters,
                    preferredPageSize,
                    log,
                    ct)
                .ConfigureAwait(false);
            var attempt = await EnumerateAllOnceAsync(
                    page,
                    log,
                    ct,
                    attemptNumber,
                    statusFilters,
                    progress)
                .ConfigureAwait(false);
            if (attempt.IsComplete)
                return attempt.Rows;

            if (attemptNumber == 1)
            {
                log?.Invoke(
                    "原创管理分页首次读取不完整，等待列表稳定后自动从第 1 页重试：" +
                    DescribeAttempt(attempt));
                previousAttempt = attempt;
                await page.WaitForTimeoutAsync(1200).ConfigureAwait(false);
                // Reload the SPA before retrying. This obtains a fresh, stable
                // pagination snapshot and avoids driving the stale page-73 React
                // component back to page 1 after the underlying list has changed.
                await OpenAsync(page, log, ct).ConfigureAwait(false);
                continue;
            }

            if (previousAttempt is not null &&
                HasStableDuplicateOnlyShortfall(previousAttempt, attempt))
            {
                log?.Invoke(
                    "原创管理连续两次读取到相同的稳定重复行；将按唯一剧集继续检查：" +
                    DescribeAttempt(attempt));
                return attempt.Rows;
            }

            throw new InvalidOperationException(
                "原创管理分页读取不完整：" + DescribeAttempt(attempt) +
                "。已自动重试 1 次，仍无法确认全部剧集，本次检查已停止，避免遗漏剧集。");
        }

        return [];
    }

    private static async Task<TikTokSeriesListEnumerationAttempt> EnumerateAllOnceAsync(
        IPage page,
        Action<string>? log,
        CancellationToken ct,
        int attemptNumber,
        IReadOnlyList<string>? statusFilters,
        IProgress<TikTokSeriesListEnumerationProgress>? progress)
    {
        var search = await FindSearchInputAsync(page).ConfigureAwait(false)
            ?? throw new InvalidOperationException("原创管理页面未找到剧集搜索框。");
        await search.FillAsync(string.Empty).ConfigureAwait(false);
        try { await search.PressAsync("Enter").ConfigureAwait(false); }
        catch { /* 部分版本清空后会自动查询。 */ }
        await page.WaitForTimeoutAsync(500).ConfigureAwait(false);
        var expectedTotal = await TryReadTotalCountAsync(page).ConfigureAwait(false);
        var pageSize = await TryReadPageSizeAsync(page).ConfigureAwait(false);
        await GoToFirstPageAsync(page, expectedTotal, pageSize, ct).ConfigureAwait(false);

        var collected = new List<TikTokSeriesListRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicateKeyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var rawVisibleRowCount = 0;
        var skippedRowCount = 0;
        string? previousFingerprint = null;
        var endReason = "达到分页安全上限";

        for (var pageNumber = 1; pageNumber <= 1000; pageNumber++)
        {
            ct.ThrowIfCancellationRequested();
            var totalPages = ExpectedPageCount(expectedTotal, pageSize);
            progress?.Report(new TikTokSeriesListEnumerationProgress(
                FormatStatusFilters(statusFilters),
                attemptNumber,
                pageNumber,
                totalPages,
                CurrentPageRowCount: null,
                collected.Count));
            var scan = await ScanCurrentPageRowsAsync(page, ct).ConfigureAwait(false);
            var pageRows = scan.Rows;
            rawVisibleRowCount += scan.VisibleRowCount;
            skippedRowCount += scan.SkippedRowCount;
            if (pageRows.Count == 0)
            {
                if (pageNumber == 1)
                    throw new InvalidOperationException("原创管理列表没有读取到任何剧集行。");
                endReason = "当前页没有可解析剧集行";
                break;
            }

            foreach (var row in pageRows)
            {
                var key = !string.IsNullOrWhiteSpace(row.SeriesId)
                    ? $"id:{row.SeriesId}"
                    : $"url:{row.DetailUrl}|title:{NormalizeTitle(row.Title)}";
                if (seen.Add(key))
                    collected.Add(row);
                else
                    duplicateKeyCounts[key] = duplicateKeyCounts.GetValueOrDefault(key) + 1;
            }

            progress?.Report(new TikTokSeriesListEnumerationProgress(
                FormatStatusFilters(statusFilters),
                attemptNumber,
                pageNumber,
                totalPages,
                scan.VisibleRowCount,
                collected.Count));

            log?.Invoke(
                $"第 {attemptNumber} 次扫描原创管理第 {pageNumber} 页：" +
                $"可见 {scan.VisibleRowCount} 行，解析 {pageRows.Count} 个，" +
                $"累计唯一 {collected.Count} 个，重复 {duplicateKeyCounts.Values.Sum()} 个，" +
                $"跳过 {skippedRowCount} 行。");

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
                endReason = next is null
                    ? "未找到“下一页”按钮"
                    : "“下一页”按钮已禁用";
                break;
            }

            await next.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 }).ConfigureAwait(false);
            await next.ClickAsync(new() { Timeout = 10000 }).ConfigureAwait(false);
            var nextPageNumber = pageNumber + 1;
            var changed = await WaitForPageReadyAsync(
                    page,
                    nextPageNumber,
                    ExpectedRowCount(expectedTotal, pageSize, nextPageNumber),
                    fingerprint,
                    ct)
                .ConfigureAwait(false);
            if (!changed)
                throw new InvalidOperationException(
                    $"点击原创管理下一页后，第 {nextPageNumber} 页未在规定时间内稳定加载。");
        }

        return new TikTokSeriesListEnumerationAttempt(
            collected,
            expectedTotal,
            rawVisibleRowCount,
            skippedRowCount,
            duplicateKeyCounts,
            endReason);
    }

    internal static bool HasStableDuplicateOnlyShortfall(
        TikTokSeriesListEnumerationAttempt first,
        TikTokSeriesListEnumerationAttempt second)
    {
        if (first.ExpectedTotal is not > 0 ||
            second.ExpectedTotal != first.ExpectedTotal ||
            first.SkippedRowCount != 0 || second.SkippedRowCount != 0 ||
            first.RawVisibleRowCount < first.ExpectedTotal ||
            second.RawVisibleRowCount < second.ExpectedTotal ||
            first.DuplicateKeyCounts.Count == 0 ||
            first.Rows.Count != second.Rows.Count)
        {
            return false;
        }

        var duplicateCount = second.DuplicateKeyCounts.Values.Sum();
        if (second.Rows.Count + duplicateCount < second.ExpectedTotal)
            return false;

        return first.DuplicateKeyCounts.Count == second.DuplicateKeyCounts.Count &&
               first.DuplicateKeyCounts.All(pair =>
                   second.DuplicateKeyCounts.TryGetValue(pair.Key, out var count) &&
                   count == pair.Value);
    }

    private static string DescribeAttempt(TikTokSeriesListEnumerationAttempt attempt)
    {
        var duplicateSample = attempt.DuplicateKeyCounts.Keys
            .Take(3)
            .Select(DescribeSeriesKey)
            .ToArray();
        var duplicateText = duplicateSample.Length == 0
            ? "无"
            : string.Join("、", duplicateSample);
        return
            $"页面显示共 {attempt.ExpectedTotal?.ToString() ?? "未知"} 个，" +
            $"扫描可见行 {attempt.RawVisibleRowCount} 个，唯一剧集 {attempt.Rows.Count} 个，" +
            $"重复 {attempt.DuplicateKeyCounts.Values.Sum()} 个（{duplicateText}），" +
            $"跳过 {attempt.SkippedRowCount} 行，结束原因：{attempt.EndReason}";
    }

    private static string DescribeSeriesKey(string key) =>
        key.StartsWith("id:", StringComparison.Ordinal) ? key[3..] : key;

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

    private static async Task<TikTokSeriesPageScanResult> ScanCurrentPageRowsAsync(
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
            return new TikTokSeriesPageScanResult([], 0, 0);

        var results = new List<TikTokSeriesListRow>();
        var visibleRowCount = 0;
        var skippedRowCount = 0;
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
                visibleRowCount++;
                rawText = await row.InnerTextAsync(new() { Timeout = 1500 }).ConfigureAwait(false);
            }
            catch
            {
                skippedRowCount++;
                continue;
            }

            var lines = rawText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var title = ExtractTitle(lines, rawText);
            if (string.IsNullOrWhiteSpace(title))
            {
                skippedRowCount++;
                continue;
            }

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

        var distinctRows = results
            .DistinctBy(row => string.Join(
                "\n",
                row.Title,
                row.SeriesId,
                row.DetailUrl))
            .ToArray();
        skippedRowCount += results.Count - distinctRows.Length;
        return new TikTokSeriesPageScanResult(
            distinctRows,
            visibleRowCount,
            skippedRowCount);
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

    private static async Task GoToFirstPageAsync(
        IPage page,
        int? expectedTotal,
        int? pageSize,
        CancellationToken ct)
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
                    if (await WaitForPageReadyAsync(
                            page,
                            1,
                            ExpectedRowCount(expectedTotal, pageSize, 1),
                            previousFingerprint: null,
                            ct).ConfigureAwait(false))
                    {
                        return;
                    }

                    throw new InvalidOperationException("原创管理第 1 页未在规定时间内稳定加载。");
                }

                await candidate.ScrollIntoViewIfNeededAsync(
                        new() { Timeout = 10000 })
                    .ConfigureAwait(false);
                await candidate.ClickAsync(new() { Timeout = 10000 }).ConfigureAwait(false);
                if (await WaitForPageReadyAsync(
                        page,
                        1,
                        ExpectedRowCount(expectedTotal, pageSize, 1),
                        previousFingerprint: null,
                        ct).ConfigureAwait(false))
                {
                    return;
                }

                throw new InvalidOperationException("返回原创管理第 1 页后，列表未在规定时间内稳定加载。");
            }
        }

        if (!await WaitForPageReadyAsync(
                page,
                expectedPageNumber: null,
                ExpectedRowCount(expectedTotal, pageSize, 1),
                previousFingerprint: null,
                ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("原创管理列表未在规定时间内稳定加载。");
        }
    }

    private static async Task ConfigureListAsync(
        IPage page,
        IReadOnlyList<string>? statusFilters,
        int? preferredPageSize,
        Action<string>? log,
        CancellationToken ct)
    {
        if (statusFilters is { Count: > 0 })
            await ApplyStatusFiltersAsync(page, statusFilters, log, ct).ConfigureAwait(false);

        if (preferredPageSize is > 0)
            await TrySetPageSizeAsync(page, preferredPageSize.Value, log, ct).ConfigureAwait(false);
    }

    private static string FormatStatusFilters(IReadOnlyList<string>? statuses) =>
        statuses is { Count: > 0 }
            ? string.Join("、", statuses.Where(value => !string.IsNullOrWhiteSpace(value)))
            : "全部";

    private static async Task ApplyStatusFiltersAsync(
        IPage page,
        IReadOnlyList<string> statuses,
        Action<string>? log,
        CancellationToken ct)
    {
        var expected = statuses
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (expected.Length == 0)
            return;

        ct.ThrowIfCancellationRequested();
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var combo = await FindComboboxContainingTextAsync(page, "状态").ConfigureAwait(false)
                ?? throw new InvalidOperationException("原创管理页面未找到“状态”筛选器。");
            if (!string.Equals(
                    await combo.GetAttributeAsync("aria-expanded").ConfigureAwait(false),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                await combo.ClickAsync(new() { Timeout = 10000 }).ConfigureAwait(false);
                await page.WaitForTimeoutAsync(200).ConfigureAwait(false);
                combo = await FindComboboxContainingTextAsync(page, "状态").ConfigureAwait(false)
                    ?? throw new InvalidOperationException("原创管理状态筛选器在展开后丢失。");
            }

            var selected = await ReadSelectedStatusOptionTextsAsync(page, combo)
                .ConfigureAwait(false);
            if (IsExactStatusSelection(selected, expected))
            {
                try { await combo.PressAsync("Escape").ConfigureAwait(false); }
                catch { /* 下拉框可能已自动关闭。 */ }
                log?.Invoke($"原创管理已选择状态：{string.Join("、", expected)}。");
                return;
            }

            var extra = selected.FirstOrDefault(value =>
                !expected.Contains(value, StringComparer.Ordinal));
            var missing = expected.FirstOrDefault(value =>
                !selected.Contains(value, StringComparer.Ordinal));
            var optionText = extra ?? missing;
            if (string.IsNullOrWhiteSpace(optionText))
                continue;
            var option = await FindStatusOptionByTextAsync(page, combo, optionText)
                .ConfigureAwait(false);
            if (option is null)
                throw new InvalidOperationException($"原创管理状态筛选器没有“{optionText}”选项。");
            await option.ClickAsync(new() { Timeout = 10000 }).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(250).ConfigureAwait(false);
        }

        var finalCombo = await FindComboboxContainingTextAsync(page, "状态").ConfigureAwait(false);
        var finalSelected = finalCombo is null
            ? []
            : await ReadSelectedStatusOptionTextsAsync(page, finalCombo).ConfigureAwait(false);
        throw new InvalidOperationException(
            $"原创管理未能切换到状态“{string.Join("、", expected)}”；" +
            $"当前选中：{(finalSelected.Count == 0 ? "无" : string.Join("、", finalSelected))}。");
    }

    private static async Task TrySetPageSizeAsync(
        IPage page,
        int pageSize,
        Action<string>? log,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var combo = await FindComboboxContainingTextAsync(page, "每页条数")
                .ConfigureAwait(false);
            if (combo is null)
            {
                log?.Invoke("WARN 原创管理未找到每页条数选择器，将沿用网页当前分页数量。");
                return;
            }

            var expected = $"每页条数：{pageSize}";
            var currentText = await ReadInnerTextAsync(combo).ConfigureAwait(false);
            if (currentText.Contains(expected, StringComparison.Ordinal))
                return;

            await combo.ClickAsync(new() { Timeout = 10000 }).ConfigureAwait(false);
            var option = await FindVisibleOptionByTextAsync(page, expected).ConfigureAwait(false);
            if (option is null)
            {
                try { await combo.PressAsync("Escape").ConfigureAwait(false); }
                catch { /* 下拉框可能已经自动关闭。 */ }
                log?.Invoke($"WARN 原创管理没有“{expected}”选项，将沿用网页当前分页数量。");
                return;
            }

            await option.ClickAsync(new() { Timeout = 10000 }).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(700).ConfigureAwait(false);
            currentText = await ReadInnerTextAsync(combo).ConfigureAwait(false);
            log?.Invoke(currentText.Contains(expected, StringComparison.Ordinal)
                ? $"原创管理每页条数已设置为 {pageSize}。"
                : $"WARN 原创管理每页条数未确认切换为 {pageSize}，继续使用网页当前设置。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"WARN 设置原创管理每页条数失败，将沿用网页当前设置：{ex.Message}");
        }
    }

    private static async Task<ILocator?> FindComboboxContainingTextAsync(
        IPage page,
        string text)
    {
        var candidates = page.Locator("[role='combobox']");
        var count = await candidates.CountAsync().ConfigureAwait(false);
        for (var index = 0; index < count; index++)
        {
            var candidate = candidates.Nth(index);
            try
            {
                if (!await candidate.IsVisibleAsync().ConfigureAwait(false))
                    continue;
                var candidateText = await ReadInnerTextAsync(candidate).ConfigureAwait(false);
                if (candidateText.Contains(text, StringComparison.Ordinal))
                    return candidate;
            }
            catch
            {
                // 下拉框可能正在重绘，继续尝试其他候选。
            }
        }

        return null;
    }

    internal static bool IsExactStatusSelection(
        IEnumerable<string> selectedStatuses,
        IEnumerable<string> expectedStatuses)
    {
        var selected = selectedStatuses
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var expected = expectedStatuses
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return selected.Length == expected.Length &&
               selected.All(value => expected.Contains(value, StringComparer.Ordinal));
    }

    private static async Task<ILocator> ResolveStatusOptionsAsync(
        IPage page,
        ILocator combo)
    {
        var controlId = await combo.GetAttributeAsync("aria-controls").ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(controlId))
        {
            var escaped = controlId.Replace("'", "\\'", StringComparison.Ordinal);
            var scoped = page.Locator($"[id='{escaped}'] [role='option']");
            if (await scoped.CountAsync().ConfigureAwait(false) > 0)
                return scoped;
        }

        return page.Locator("[role='option']");
    }

    private static async Task<IReadOnlyList<string>> ReadSelectedStatusOptionTextsAsync(
        IPage page,
        ILocator combo)
    {
        var options = await ResolveStatusOptionsAsync(page, combo).ConfigureAwait(false);
        var selected = new List<string>();
        var count = await options.CountAsync().ConfigureAwait(false);
        for (var index = 0; index < count; index++)
        {
            var option = options.Nth(index);
            try
            {
                if (!await option.IsVisibleAsync().ConfigureAwait(false) ||
                    !string.Equals(
                        await option.GetAttributeAsync("aria-selected").ConfigureAwait(false),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                selected.Add((await option.InnerTextAsync(new() { Timeout = 1200 })
                        .ConfigureAwait(false))
                    .Trim());
            }
            catch
            {
                // 选项可能正在重绘，下一轮会重新读取完整状态。
            }
        }

        return selected;
    }

    private static async Task<ILocator?> FindStatusOptionByTextAsync(
        IPage page,
        ILocator combo,
        string expectedText)
    {
        var options = await ResolveStatusOptionsAsync(page, combo).ConfigureAwait(false);
        var count = await options.CountAsync().ConfigureAwait(false);
        for (var index = 0; index < count; index++)
        {
            var option = options.Nth(index);
            try
            {
                if (!await option.IsVisibleAsync().ConfigureAwait(false))
                    continue;
                var text = (await option.InnerTextAsync(new() { Timeout = 1200 })
                        .ConfigureAwait(false))
                    .Trim();
                if (string.Equals(text, expectedText, StringComparison.Ordinal))
                    return option;
            }
            catch
            {
                // 选项可能正在重绘，继续尝试其他候选。
            }
        }

        return null;
    }

    private static async Task<ILocator?> FindVisibleOptionByTextAsync(
        IPage page,
        string expectedText)
    {
        var options = page.Locator("[role='option']");
        var count = await options.CountAsync().ConfigureAwait(false);
        for (var index = 0; index < count; index++)
        {
            var option = options.Nth(index);
            try
            {
                if (!await option.IsVisibleAsync().ConfigureAwait(false))
                    continue;
                var text = await ReadInnerTextAsync(option).ConfigureAwait(false);
                if (string.Equals(text, expectedText.Replace(" ", string.Empty),
                        StringComparison.Ordinal))
                {
                    return option;
                }
            }
            catch
            {
                // 下拉选项可能正在重绘，继续尝试其他候选。
            }
        }

        return null;
    }

    private static async Task<string> ReadInnerTextAsync(ILocator locator)
    {
        try
        {
            return (await locator.InnerTextAsync(new() { Timeout = 1500 }).ConfigureAwait(false))
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .Trim();
        }
        catch
        {
            return string.Empty;
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

    private static async Task<int?> TryReadPageSizeAsync(IPage page)
    {
        var combo = await FindComboboxContainingTextAsync(page, "每页条数").ConfigureAwait(false);
        if (combo is null)
            return null;
        var text = await ReadInnerTextAsync(combo).ConfigureAwait(false);
        var match = Regex.Match(text, @"每页条数[：:]?(?<count>\d+)");
        return match.Success && int.TryParse(match.Groups["count"].Value, out var count) && count > 0
            ? count
            : null;
    }

    internal static int? ExpectedRowCount(
        int? expectedTotal,
        int? pageSize,
        int pageNumber)
    {
        if (expectedTotal is not > 0 || pageSize is not > 0 || pageNumber <= 0)
            return null;
        var remaining = expectedTotal.Value - ((pageNumber - 1) * pageSize.Value);
        return remaining <= 0 ? 0 : Math.Min(pageSize.Value, remaining);
    }

    internal static int? ExpectedPageCount(int? expectedTotal, int? pageSize)
    {
        if (expectedTotal is not > 0 || pageSize is not > 0)
            return null;
        return (int)Math.Ceiling(expectedTotal.Value / (double)pageSize.Value);
    }

    internal static bool IsPageReadinessSampleAcceptable(
        int? expectedPageNumber,
        int? activePageNumber,
        int? expectedVisibleRowCount,
        int actualVisibleRowCount,
        string? previousFingerprint,
        string currentFingerprint)
    {
        if (expectedPageNumber.HasValue && activePageNumber != expectedPageNumber)
            return false;
        if (actualVisibleRowCount <= 0 || string.IsNullOrWhiteSpace(currentFingerprint))
            return false;
        if (expectedVisibleRowCount.HasValue &&
            actualVisibleRowCount != expectedVisibleRowCount.Value)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(previousFingerprint) ||
               !string.Equals(previousFingerprint, currentFingerprint, StringComparison.Ordinal);
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

    private static async Task<TikTokSeriesPageReadinessSnapshot>
        ReadPageReadinessSnapshotAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(
            """
            () => {
              const visible = element => {
                if (!(element instanceof HTMLElement)) return false;
                const style = getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden' &&
                  Number(style.opacity || '1') > 0 && rect.width > 0 && rect.height > 0;
              };
              let rows = [...document.querySelectorAll('tbody tr')].filter(visible);
              if (rows.length === 0)
                rows = [...document.querySelectorAll('[role="rowgroup"] [role="row"]')]
                  .filter(visible);
              if (rows.length === 0)
                rows = [...document.querySelectorAll('tr')].filter(visible);
              const keys = rows.map(row => {
                const text = (row.innerText || row.textContent || '').trim();
                const id = text.match(/\b\d{16,20}\b/)?.[0];
                return id || text.replace(/\s+/g, ' ').slice(0, 160);
              });
              const active = [...document.querySelectorAll(
                '.semi-page-item-active, .semi-pagination-item-active, [aria-current="page"]')]
                .find(visible);
              const activeText = (active?.innerText || active?.textContent || '').trim();
              const activePageNumber = /^\d+$/.test(activeText) ? Number(activeText) : null;
              const bodyText = document.body?.innerText || '';
              const rangeText = bodyText.match(
                /显示第\s*\d+\s*条-第\s*\d+\s*条，?共\s*\d+\s*条/)?.[0] || '';
              return JSON.stringify({
                activePageNumber,
                visibleRowCount: rows.length,
                fingerprint: keys.join('|'),
                rangeText,
              });
            }
            """).ConfigureAwait(false);
        return ParsePageReadinessSnapshot(json);
    }

    internal static TikTokSeriesPageReadinessSnapshot ParsePageReadinessSnapshot(
        string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("原创管理分页就绪快照为空。");
        return JsonSerializer.Deserialize<TikTokSeriesPageReadinessSnapshot>(
                   json,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidDataException("原创管理分页就绪快照无法解析。");
    }

    private static async Task<bool> WaitForPageReadyAsync(
        IPage page,
        int? expectedPageNumber,
        int? expectedVisibleRowCount,
        string? previousFingerprint,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string? stableFingerprint = null;
        var stableSamples = 0;
        const int requiredStableSamples = 2;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(20))
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await ReadPageReadinessSnapshotAsync(page).ConfigureAwait(false);
            var fingerprint = snapshot.Fingerprint;
            if (IsPageReadinessSampleAcceptable(
                    expectedPageNumber,
                    snapshot.ActivePageNumber,
                    expectedVisibleRowCount,
                    snapshot.VisibleRowCount,
                    previousFingerprint,
                    fingerprint))
            {
                if (string.Equals(stableFingerprint, fingerprint, StringComparison.Ordinal))
                    stableSamples++;
                else
                {
                    stableFingerprint = fingerprint;
                    stableSamples = 1;
                }

                if (stableSamples >= requiredStableSamples)
                    return true;
            }
            else
            {
                stableFingerprint = null;
                stableSamples = 0;
            }

            await page.WaitForTimeoutAsync(250).ConfigureAwait(false);
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
