using PlatformPublisher.Analytics.Models;
using PlatformPublisher.Analytics.Services;
using PlatformPublisher.Analytics.Storage;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Kuaishou.Analytics;
using Xunit;

namespace PlatformPublisher.Analytics.Tests;

public sealed class KuaishouPlayStatisticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kuaishou-play-" + Guid.NewGuid().ToString("N"));
    private static readonly DateOnly Today = new(2026, 11, 1);

    [Fact]
    public void WindowIsThirtyDaysEndingYesterday()
    {
        var dates = KuaishouPlayWindow.Dates(Today);
        Assert.Equal(30, dates.Count);
        Assert.Equal(new DateOnly(2026, 10, 2), dates[0]);
        Assert.Equal(new DateOnly(2026, 10, 31), dates[^1]);
        var visible = KuaishouPlayWindow.Visible(Today, 7);
        Assert.Equal(new DateOnly(2026, 10, 25), visible[0]);
        Assert.Equal(new DateOnly(2026, 10, 31), visible[^1]);
    }

    [Fact]
    public void RefreshKeepsSavedDaysExceptTheLatest()
    {
        var window = KuaishouPlayWindow.Dates(Today);
        var refresh = KuaishouPlayWindow.DatesToRefresh(window, [window[0], window[^1]]);
        Assert.Contains(window[^1], refresh);
        Assert.DoesNotContain(window[0], refresh);
        Assert.Contains(window[1], refresh);
    }

    [Fact]
    public void StoreKeepsEditionsSeparateAndDropsDaysOutsideTheWindow()
    {
        var repository = Repository();
        var personal = Account(PublishPlatform.KuaishouPersonalRevenue);
        var enterprise = Account(PublishPlatform.KuaishouEnterpriseRevenue);
        repository.UpsertDaily(Record(personal, new DateOnly(2026, 9, 1), 9));
        repository.UpsertDaily(Record(enterprise, new DateOnly(2026, 9, 1), 8));
        repository.UpsertDaily(Record(personal, new DateOnly(2026, 10, 2), 5));
        repository.UpsertDaily(Record(personal, new DateOnly(2026, 10, 31), 1));

        var written = KuaishouPlayStore.Save(repository, personal, new Dictionary<DateOnly, long>
        {
            [new DateOnly(2026, 10, 1)] = 4,
            [new DateOnly(2026, 10, 2)] = 9,
            [new DateOnly(2026, 10, 31)] = 12,
            [new DateOnly(2026, 10, 30)] = 0,
        }, Today);

        Assert.Equal(2, written);
        var personalRows = repository.ListDaily(new DateOnly(2026, 9, 1), Today).Where(row => row.Platform == personal.Platform).ToArray();
        Assert.DoesNotContain(personalRows, row => row.MetricDate == new DateOnly(2026, 9, 1));
        Assert.Equal(5, personalRows.Single(row => row.MetricDate == new DateOnly(2026, 10, 2)).SeriesViewsTotal);
        Assert.Equal(12, personalRows.Single(row => row.MetricDate == new DateOnly(2026, 10, 31)).SeriesViewsTotal);
        Assert.Equal(0, personalRows.Single(row => row.MetricDate == new DateOnly(2026, 10, 30)).SeriesViewsTotal);
        Assert.Equal(8, repository.ListDaily(new DateOnly(2026, 9, 1), Today).Single(row => row.Platform == enterprise.Platform).SeriesViewsTotal);
    }

    [Fact]
    public void BoardSplitsPersonalAndEnterpriseAndLeavesGapsEmpty()
    {
        var personal = Account(PublishPlatform.KuaishouPersonalRevenue, "薄荷剧场");
        var enterprise = Account(PublishPlatform.KuaishouEnterpriseRevenue, "薄荷剧场");
        var records = new[]
        {
            Record(personal, new DateOnly(2026, 10, 30), 5),
            Record(personal, new DateOnly(2026, 10, 31), 10),
        };
        var board = KuaishouPlayBoard.Build([personal, enterprise], records, Today, 7, null);
        var personalCard = Assert.Single(board.Cards, card => card.Edition == "个人");
        var enterpriseCard = Assert.Single(board.Cards, card => card.Edition == "企业");
        Assert.Equal("15", personalCard.TotalText);
        Assert.Equal("10", personalCard.LatestText);
        Assert.Equal("暂无数据", enterpriseCard.TotalText);
        Assert.Equal("未出数", enterpriseCard.LatestText);
        Assert.Equal(2, board.Series.Count);
        Assert.Equal(7, board.Series[0].Points.Count);
        Assert.Equal(10, board.Series[0].Points[^1].Views);
        Assert.Null(board.Series[0].Points[0].Views);
        Assert.All(board.Series[1].Points, point => Assert.Null(point.Views));

        var selected = KuaishouPlayBoard.Build([personal, enterprise], records, Today, 30, KuaishouPlayBoard.Key(enterprise));
        Assert.Equal(2, selected.Cards.Count);
        Assert.Equal(KuaishouPlayBoard.Key(enterprise), Assert.Single(selected.Series).Key);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(14000, "1.4万")]
    [InlineData(20000, "2万")]
    public void PlayCountsUseWan(long value, string expected) => Assert.Equal(expected, KuaishouPlayFormat.Count(value));

    [Fact]
    public void ParserReadsDailyPlaysAndIgnoresVideoListsAndRangeSums()
    {
        var ms = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
        var trend = KuaishouWorksPlayParser.Parse("""
            {"result":1,"data":{"trendList":[
              {"date":"2026-10-30","playCnt":12000},
              {"date":"2026-10-31","playCnt":"2万"}
            ]}}
            """);
        Assert.Equal(12000, trend[new DateOnly(2026, 10, 30)]);
        Assert.Equal(20000, trend[new DateOnly(2026, 10, 31)]);

        var merged = KuaishouWorksPlayParser.Parse($$"""{"trendList":[{"date":"2026-10-31","playCnt":1}]}""");
        KuaishouWorksPlayParser.Merge(merged, $$"""{"data":[{"time":{{ms}},"playCount":8}]}""");
        Assert.Equal(8, merged[new DateOnly(2026, 10, 31)]);

        Assert.Empty(KuaishouWorksPlayParser.Parse("""
            {"list":[{"date":"2026-10-31","playCnt":10,"photoId":"a"},{"date":"2026-10-30","playCnt":20,"photoId":"b"}]}
            """));
        Assert.Empty(KuaishouWorksPlayParser.Parse("""
            {"list":[{"date":"2026-10-31","playCnt":10},{"date":"2026-10-31","playCnt":20}]}
            """));
        Assert.Empty(KuaishouWorksPlayParser.Parse("""
            {"startDate":"2026-10-01","endDate":"2026-10-31","playCount":900000}
            """));
        Assert.Empty(KuaishouWorksPlayParser.Parse("""
            {"likeTrend":[{"date":"2026-10-31","count":9}]}
            """));
        Assert.Equal(0, KuaishouWorksPlayParser.Parse("""{"list":[{"statDate":"20261031","viewCount":0}]}""")[new DateOnly(2026, 10, 31)]);
        Assert.Equal(20000, KuaishouWorksPlayParser.Parse("""
            {"startDate":"2026-10-31","endDate":"2026-10-31","playCount":20000}
            """)[new DateOnly(2026, 10, 31)]);
        Assert.Equal(5, KuaishouWorksPlayParser.Parse("""
            {"播放量":[{"date":"2026-10-31","value":5}]}
            """)[new DateOnly(2026, 10, 31)]);

        var overview = KuaishouWorksPlayParser.Parse("""
            {"data":{"basicData":[
              {"name":"播放量","tab":"PLAY","trendData":[{"date":"20261031","count":12000},{"date":"20261101","count":96640}]},
              {"name":"点赞量","tab":"LIKE","trendData":[{"date":"20261101","count":223}]}
            ]}}
            """);
        Assert.Equal(12000, overview[new DateOnly(2026, 10, 31)]);
        Assert.Equal(96640, overview[new DateOnly(2026, 11, 1)]);
        Assert.Equal(2, overview.Count);
    }

    [Fact]
    public void ScheduledRefreshWaitsUntilNoonForYesterdayOnly()
    {
        var window = KuaishouPlayWindow.Dates(Today);
        var saved = window.Take(window.Count - 1).ToArray();
        Assert.False(KuaishouPlayWindow.NeedsCollection(window, saved, 11));
        Assert.True(KuaishouPlayWindow.NeedsCollection(window, saved, 12));
        Assert.True(KuaishouPlayWindow.NeedsCollection(window, [window[^1]], 11));
        Assert.False(KuaishouPlayWindow.NeedsCollection(window, window, 15));
    }

    [Fact]
    public void PageTextReadsOneDayPlayAndRejectsARange()
    {
        const string page = "播放量\n超过96.5%同类\n播放量\n2万\n2026-10-31 ~ 2026-10-31";
        Assert.Equal(20000, KuaishouWorksPlayParser.ReadLabeledPlay(page));
        Assert.True(KuaishouWorksPlayParser.ShowsSingleDay(page, new DateOnly(2026, 10, 31)));
        Assert.False(KuaishouWorksPlayParser.ShowsSingleDay("2026-10-01 ~ 2026-10-31", new DateOnly(2026, 10, 31)));
    }

    private AnalyticsRepository Repository()
    {
        Directory.CreateDirectory(_root);
        return new AnalyticsRepository(Path.Combine(_root, "analytics.db"));
    }

    private static AnalyticsAccount Account(PublishPlatform platform, string name = "薄荷剧场") =>
        new("acct-1", platform, name, "");

    private static DailyAnalyticsRecord Record(AnalyticsAccount account, DateOnly date, long views) => new()
    {
        Platform = account.Platform,
        AccountId = account.Id,
        MetricDate = date,
        CollectedAt = DateTimeOffset.Now,
        Status = AnalyticsRecordStatus.Success,
        SeriesViewsTotal = views,
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
