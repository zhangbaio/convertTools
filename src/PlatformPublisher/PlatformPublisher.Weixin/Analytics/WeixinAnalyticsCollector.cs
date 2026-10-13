using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using PlatformPublisher.Analytics.Models;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Weixin.Analytics;

public sealed class WeixinAnalyticsCollector
{
    public const string HomeUrl = "https://channels.weixin.qq.com";
    public const string IncomeUrl = HomeUrl + "/platform/playlet/statistic";

    public async Task<AccountAnalyticsSnapshot> CollectSnapshotAsync(string cdpEndpoint, string accountId,
        CancellationToken cancellationToken)
    {
        return await WithPageAsync(cdpEndpoint, async page =>
        {
            await page.GotoAsync(HomeUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            await EnsureLoggedInAsync(page);
            var home = await ReadHomeAsync(page);
            await page.GotoAsync(IncomeUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            var income = await ReadIncomeAsync(page);
            var yesterday = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
            await SelectDateAsync(page, yesterday);
            var daily = await ReadIncomeAsync(page);
            return new AccountAnalyticsSnapshot
            {
                Platform = PublishPlatform.WeixinChannel, AccountId = accountId, CollectedAt = DateTimeOffset.UtcNow,
                VideoTotal = Whole(home.VideoTotal), FollowerTotal = Whole(home.FollowerTotal),
                YesterdayNetFollowers = Whole(home.NetFollowers), YesterdayViews = Whole(home.Views),
                YesterdayLikes = Whole(home.Likes), YesterdayComments = Whole(home.Comments),
                ListedSeriesTotal = checked((int)Whole(income.ListedSeries)), MountedVideoTotal = Whole(income.MountedVideos),
                SeriesViewsTotal = Whole(income.SeriesViews), AdMonetizationIncomeFen = Fen(income.AdIncome),
                YesterdayAdMonetizationIncomeFen = Fen(daily.AdIncome), HeatingIncomeFen = Fen(income.HeatingIncome),
                MountedIncomeFen = Fen(income.MountedIncome), EstimatedViolationDeductionFen = Fen(income.Deduction),
                RangeStart = income.RangeStart, RangeEnd = income.RangeEnd,
            };
        }, cancellationToken);
    }

    public async Task<DailyAnalyticsRecord> CollectDailyAsync(string cdpEndpoint, string accountId,
        DateOnly date, CancellationToken cancellationToken)
    {
        return await WithPageAsync(cdpEndpoint, async page =>
        {
            await page.GotoAsync(IncomeUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            await EnsureLoggedInAsync(page);
            await SelectDateAsync(page, date);
            var value = await ReadIncomeAsync(page);
            return new DailyAnalyticsRecord
            {
                Platform = PublishPlatform.WeixinChannel, AccountId = accountId, MetricDate = date,
                CollectedAt = DateTimeOffset.UtcNow, Status = AnalyticsRecordStatus.Success,
                ListedSeriesTotal = checked((int)Whole(value.ListedSeries)), MountedVideoTotal = Whole(value.MountedVideos),
                SeriesViewsTotal = Whole(value.SeriesViews), AdMonetizationIncomeFen = Fen(value.AdIncome),
                HeatingIncomeFen = Fen(value.HeatingIncome), MountedIncomeFen = Fen(value.MountedIncome),
                EstimatedViolationDeductionFen = Fen(value.Deduction),
            };
        }, cancellationToken);
    }

    private static async Task<T> WithPageAsync<T>(string endpoint, Func<IPage, Task<T>> action,
        CancellationToken cancellationToken)
    {
        using var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.ConnectOverCDPAsync(endpoint);
        var context = browser.Contexts.FirstOrDefault() ?? throw new InvalidOperationException("视频号浏览器上下文不可用。");
        var page = await context.NewPageAsync();
        using var registration = cancellationToken.Register(() => _ = page.CloseAsync());
        try { return await action(page); }
        catch (PlaywrightException ex) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("视频号数据采集已取消。", ex, cancellationToken); }
        finally { await page.CloseAsync().Catch(); }
    }

    private static async Task EnsureLoggedInAsync(IPage page)
    {
        if (page.Url.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            await page.Locator("input[type=password]").First.IsVisibleAsync())
            throw new InvalidOperationException("视频号登录态无效，请先登录当前账号。");
    }

    private static async Task<HomeMetrics> ReadHomeAsync(IPage page)
    {
        var account = page.Locator(".finder-content-info");
        await account.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20_000 });
        var text = await account.InnerTextAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        Dictionary<string, string> metrics = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            metrics = await MetricMapAsync(page, ".admin-area .data-item");
            if (new[] { "净增关注", "新增播放", "新增", "新增评论" }.All(metrics.ContainsKey)) break;
            await Task.Delay(200);
        }
        return new(Parse(DisplayMatch(text, "视频")), Parse(DisplayMatch(text, "关注者")),
            Parse(metrics.GetValueOrDefault("净增关注")), Parse(metrics.GetValueOrDefault("新增播放")),
            Parse(metrics.GetValueOrDefault("新增")), Parse(metrics.GetValueOrDefault("新增评论")));
    }

    private static async Task<IncomeMetrics> ReadIncomeAsync(IPage page)
    {
        await page.Locator(".data-item").First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20_000 });
        var metrics = await MetricMapAsync(page, ".data-item");
        var (start, end) = await IncomeInputsAsync(page);
        return new(await start.InputValueAsync(), await end.InputValueAsync(),
            Parse(metrics.GetValueOrDefault("上架剧集数")),
            Parse(metrics.GetValueOrDefault("发表自营挂载视频数") ?? metrics.GetValueOrDefault("发表挂载视频数")),
            Parse(metrics.GetValueOrDefault("剧集播放量")),
            Parse(metrics.GetValueOrDefault("剧集广告变现收入") ?? metrics.GetValueOrDefault("广告变现总收入")),
            Parse(metrics.GetValueOrDefault("加热变现收入")), Parse(metrics.GetValueOrDefault("挂载变现收入")),
            Parse(metrics.GetValueOrDefault("预估违规扣除收入")));
    }

    private static async Task SelectDateAsync(IPage page, DateOnly date)
    {
        var (start, end) = await IncomeInputsAsync(page);
        await start.ClickAsync();
        var panel = page.Locator(".weui-desktop-picker__panel:visible")
            .Filter(new() { HasText = date.Year + "年" }).Filter(new() { HasText = $"{date.Month:00}月" });
        var day = panel.Locator("a:not(.weui-desktop-picker__disabled):not(.weui-desktop-picker__faded)")
            .Filter(new() { HasTextRegex = new Regex($@"^\s*{date.Day}\s*$") }).First;
        await day.ClickAsync();
        var response = page.WaitForResponseAsync(response => response.Url.Contains("get-finder-native-drama-overview-statistics"), new() { Timeout = 20_000 });
        await day.ClickAsync();
        await response;
        await page.WaitForTimeoutAsync(250);
        var expected = date.ToString("yyyy-MM-dd");
        if (await start.InputValueAsync() != expected || await end.InputValueAsync() != expected)
            throw new InvalidOperationException($"视频号收入统计未切换到 {expected}。");
    }

    private static async Task<(ILocator Start, ILocator End)> IncomeInputsAsync(IPage page)
    {
        var starts = page.GetByPlaceholder("开始日期"); var ends = page.GetByPlaceholder("结束日期");
        var count = Math.Min(await starts.CountAsync(), await ends.CountAsync());
        if (count == 0) throw new InvalidOperationException("视频号收入统计未找到日期范围控件。");
        var index = count > 1 ? 1 : 0;
        return (starts.Nth(index), ends.Nth(index));
    }

    private static async Task<Dictionary<string, string>> MetricMapAsync(IPage page, string selector)
    {
        var result = new Dictionary<string, string>();
        var items = page.Locator(selector);
        for (var i = 0; i < await items.CountAsync(); i++)
        {
            var item = items.Nth(i);
            var name = Regex.Replace((await item.Locator(".data-name").InnerTextAsync()).Trim(), @"\s+", "");
            var value = (await item.Locator(".data-number, .data").First.InnerTextAsync()).Trim();
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value)) result[name] = value;
        }
        return result;
    }

    public static decimal Parse(string? value)
    {
        var normalized = Regex.Replace(value ?? "", "[¥￥,，\\s]", "");
        var match = Regex.Match(normalized, @"^(-?\d+(?:\.\d+)?)(万|亿)?$");
        if (!decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)) return 0;
        return number * (match.Groups[2].Value == "亿" ? 100_000_000m : match.Groups[2].Value == "万" ? 10_000m : 1m);
    }

    private static string DisplayMatch(string text, string label) => Regex.Match(text, Regex.Escape(label) + @"\s*([\d.,万亿]+)").Groups[1].Value;
    private static long Fen(decimal yuan) => checked((long)Math.Round(yuan * 100m, MidpointRounding.AwayFromZero));
    private static long Whole(decimal value) => checked((long)Math.Round(value, MidpointRounding.AwayFromZero));
    private sealed record HomeMetrics(decimal VideoTotal, decimal FollowerTotal, decimal NetFollowers, decimal Views, decimal Likes, decimal Comments);
    private sealed record IncomeMetrics(string RangeStart, string RangeEnd, decimal ListedSeries, decimal MountedVideos, decimal SeriesViews, decimal AdIncome, decimal HeatingIncome, decimal MountedIncome, decimal Deduction);
}

internal static class PlaywrightTaskExtensions
{
    public static async Task Catch(this Task task) { try { await task; } catch { } }
}
