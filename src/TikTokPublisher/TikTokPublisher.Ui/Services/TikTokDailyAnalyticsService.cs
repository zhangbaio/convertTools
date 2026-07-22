using System.Text.Json;
using Microsoft.Playwright;
using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Ui.Services;

public static class TikTokDailyAnalyticsService
{
    private const string DailyStatsPath = "/api/content-partner/analytics/institution/daily-stats";

    public static async Task<TikTokDailyAnalyticsReport> FetchAsync(
        IEmbeddedBrowser embeddedBrowser,
        DateOnly requestedStart,
        DateOnly requestedEnd,
        Action<string>? log,
        CancellationToken ct)
    {
        if (requestedEnd < requestedStart)
            throw new ArgumentException("结束日期不能早于开始日期。");

        IPlaywright? playwright = null;
        try
        {
            IPage page;
            (playwright, _, page) = await EmbeddedBrowserAutomationBridge
                .ConnectPageAsync(embeddedBrowser, TikTokUrls.ContentPerformanceUrl, log, ct)
                .ConfigureAwait(false);

            var responseSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            async void OnResponse(object? _, IResponse response)
            {
                if (!response.Url.Contains(DailyStatsPath, StringComparison.OrdinalIgnoreCase) || !response.Ok)
                    return;
                try
                {
                    responseSource.TrySetResult(await response.TextAsync().ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    responseSource.TrySetException(ex);
                }
            }

            page.Response += OnResponse;
            try
            {
                log?.Invoke("正在刷新剧集表现并读取每日播放数据...");
                await page.ReloadAsync(new PageReloadOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60000,
                }).ConfigureAwait(false);

                var json = await responseSource.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                return Parse(json, requestedStart, requestedEnd);
            }
            finally
            {
                page.Response -= OnResponse;
            }
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException("未能从剧集表现页面读取播放数据。请确认当前账号已登录，并能正常打开“剧集表现”页面。", ex);
        }
        finally
        {
            playwright?.Dispose();
        }
    }

    internal static TikTokDailyAnalyticsReport Parse(string json, DateOnly requestedStart, DateOnly requestedEnd)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("code", out var code) && code.GetInt32() != 0)
            throw new InvalidOperationException(root.TryGetProperty("message", out var message)
                ? $"TikTok 返回错误：{message.GetString()}"
                : "TikTok 返回了未知错误。");

        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("dailyRows", out var dailyRows))
            throw new InvalidOperationException("TikTok 返回的数据中缺少每日播放记录。");

        var actualStart = ReadDate(data, "actualDateRangeStart");
        var actualEnd = ReadDate(data, "actualDateRangeEnd");
        var latest = ReadDate(data, "latestEventDate");
        var returnedRows = new Dictionary<DateOnly, TikTokDailyAnalyticsRow>();
        foreach (var element in dailyRows.EnumerateArray())
        {
            if (!DateOnly.TryParse(element.GetProperty("eventDate").GetString(), out var date) ||
                date < requestedStart || date > requestedEnd)
                continue;

            var metrics = element.GetProperty("metrics");
            returnedRows[date] = new TikTokDailyAnalyticsRow(
                date,
                ReadLong(metrics, "vv"),
                ReadLong(metrics, "innerfeedVv"));
        }

        var lastDate = requestedEnd < latest ? requestedEnd : latest;
        if (lastDate < requestedStart)
            throw new InvalidOperationException($"{requestedStart:yyyy-MM-dd} 至 {requestedEnd:yyyy-MM-dd} 暂无播放数据。");

        // actualDateRangeStart/End 表示账号实际产生数据的边界，并不等于页面的查询边界。
        // 因此所选范围内接口未返回的日期应按 0 处理，而不是误报“日期范围不完整”。
        var rows = new List<TikTokDailyAnalyticsRow>();
        for (var date = requestedStart; date <= lastDate; date = date.AddDays(1))
        {
            rows.Add(returnedRows.GetValueOrDefault(date)
                ?? new TikTokDailyAnalyticsRow(date, TotalViews: 0, ValidViews: 0));
        }

        return new TikTokDailyAnalyticsReport(actualStart, actualEnd, latest, rows);
    }

    private static DateOnly ReadDate(JsonElement data, string name) =>
        DateOnly.TryParse(data.GetProperty(name).GetString(), out var value)
            ? value
            : throw new InvalidOperationException($"TikTok 返回的 {name} 日期无效。");

    private static long ReadLong(JsonElement data, string name)
    {
        var value = data.GetProperty(name);
        return value.ValueKind == JsonValueKind.String
            ? long.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            : value.GetInt64();
    }
}
