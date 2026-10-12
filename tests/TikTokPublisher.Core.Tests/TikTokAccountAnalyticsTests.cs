using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public class TikTokAccountAnalyticsTests
{
    [Fact]
    public void First_sync_starts_at_the_platform_earliest_day_and_splits_180_day_windows()
    {
        var today = new DateOnly(2026, 10, 12);
        var start = TikTokAccountAnalyticsLogic.NextDailyStart(null, today);
        Assert.Equal(new DateOnly(2026, 3, 1), start);
        var windows = TikTokAccountAnalyticsLogic.Windows(start, today).ToList();
        Assert.Equal(2, windows.Count);
        Assert.Equal(new DateOnly(2026, 3, 1), windows[0].Start);
        Assert.Equal(windows[0].End.AddDays(1), windows[1].Start);
        Assert.Equal(today, windows[^1].End);
        Assert.True(windows.All(item => item.End.DayNumber - item.Start.DayNumber + 1 <= 180));
    }

    [Fact]
    public void Later_sync_refetches_the_latest_stored_day()
    {
        var today = new DateOnly(2026, 10, 12);
        var start = TikTokAccountAnalyticsLogic.NextDailyStart(new DateOnly(2026, 10, 8), today);
        Assert.Equal(new DateOnly(2026, 10, 8), start);
    }

    [Fact]
    public void Daily_parser_keeps_valid_views_and_fills_missing_days_inside_the_window()
    {
        const string json = """
            {"code":0,"data":{"latestEventDate":"2026-10-08","dailyRows":[
              {"eventDate":"2026-10-07","metrics":{"vv":"10","innerfeedVv":"8"}},
              {"eventDate":"2026-10-08","metrics":{"vv":"3","innerfeedVv":"2"}}
            ]}}
            """;
        var rows = TikTokAccountAnalyticsLogic.ParseDailyStats(json, new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 8));
        Assert.Equal(3, rows.Count);
        Assert.Equal(0, rows[0].ValidViews);
        Assert.Equal(8, rows[1].ValidViews);
        Assert.Equal(2, rows[2].ValidViews);
    }

    [Fact]
    public void Series_page_reads_cumulative_valid_views()
    {
        const string json = """
            {"code":0,"data":{"total":"2","contractBlocks":[{
              "contractID":"7658895079998198791",
              "aggregatedMetrics":{"innerfeedVv":"78966310","avgPlayDurationPerUser":"1818","favouriteCnt":"258161","commentCnt":"11709","likeCnt":"1803855"},
              "contractInfo":{"contractTitle":"农家致富路灵鸡助我改人生","publishTimeSec":"1752750000"}
            }]}}
            """;
        var parsed = TikTokAccountAnalyticsLogic.ParseSeriesPage(json, new DateOnly(2026, 10, 12));
        Assert.Equal(2, parsed.Total);
        Assert.Equal("农家致富路灵鸡助我改人生", parsed.Items[0].Title);
        Assert.Equal(78966310, parsed.Items[0].ValidViews);
        Assert.Equal(1818, parsed.Items[0].AverageWatchSeconds);
    }

    [Fact]
    public void Revenue_parser_reads_total_and_completed_amounts()
    {
        const string json = """
            {"code":0,"data":{
              "enterprises":[{"name":"湖北元通和科技有限公司"}],
              "totalBalance":{
                "totalAmount":{"amount":"9575.34","currency":"USD"},
                "pendingSettlementAmount":{"amount":"0.00","currency":"USD"},
                "paymentProcessingAmount":{"amount":"0.00","currency":"USD"},
                "paidAmount":{"amount":"9575.34","currency":"USD"}
              }}}
            """;
        var revenue = TikTokAccountAnalyticsLogic.ParseRevenue(json);
        Assert.Equal("9575.34", revenue.TotalAmount);
        Assert.Equal("9575.34", revenue.CompletedAmount);
        Assert.Equal("湖北元通和科技有限公司", revenue.CompanyName);
    }

    [Fact]
    public void Growth_uses_the_whole_gap_when_a_day_was_not_snapshotted()
    {
        var snapshots = new[]
        {
            Snapshot("1", new DateOnly(2026, 10, 10), 100),
            Snapshot("1", new DateOnly(2026, 10, 12), 160),
        };
        var growth = TikTokAccountAnalyticsLogic.BuildGrowth(snapshots);
        Assert.Single(growth);
        Assert.Equal(60, growth[0].Growth);
        Assert.Equal(2, growth[0].SpanDays);
    }

    [Fact]
    public void Store_replaces_the_same_day_and_reports_the_latest_date()
    {
        var path = Path.Combine(Path.GetTempPath(), "tiktok-analytics-tests", Guid.NewGuid().ToString("N") + ".db");
        var store = new TikTokAccountAnalyticsStore(path);
        store.UpsertDaily("acct", [new TikTokAccountDailyStat(new DateOnly(2026, 10, 8), 10, 8)]);
        store.UpsertDaily("acct", [new TikTokAccountDailyStat(new DateOnly(2026, 10, 8), 12, 9)]);
        Assert.Equal(new DateOnly(2026, 10, 8), store.LatestDailyDate("acct"));
        Assert.Equal(9, store.ListDaily("acct").Single().ValidViews);
    }

    private static TikTokSeriesSnapshot Snapshot(string id, DateOnly date, long views) =>
        new(id, "剧", date, null, views, 0, 0, 0, 0);
}
