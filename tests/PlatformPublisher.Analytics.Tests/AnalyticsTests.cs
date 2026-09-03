using Microsoft.Data.Sqlite;
using PlatformPublisher.Analytics.Models;
using PlatformPublisher.Analytics.Services;
using PlatformPublisher.Analytics.Storage;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Kuaishou.Analytics;
using PlatformPublisher.Weixin.Analytics;
using Xunit;

namespace PlatformPublisher.Analytics.Tests;

public sealed class AnalyticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "analytics-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RepositoryIsIdempotentAndFailureDoesNotOverwriteSuccess()
    {
        var repository = Repository(); var date = new DateOnly(2026, 9, 2);
        repository.UpsertDaily(new DailyAnalyticsRecord { Platform=PublishPlatform.WeixinChannel,AccountId="a1",MetricDate=date,CollectedAt=DateTimeOffset.UtcNow,Status=AnalyticsRecordStatus.Success,SeriesViewsTotal=123 });
        repository.UpsertDaily(new DailyAnalyticsRecord { Platform=PublishPlatform.WeixinChannel,AccountId="a1",MetricDate=date,CollectedAt=DateTimeOffset.UtcNow,Status=AnalyticsRecordStatus.Failed,Message="temporary" });
        var saved = Assert.Single(repository.ListDaily(date,date));
        Assert.Equal(AnalyticsRecordStatus.Success,saved.Status); Assert.Equal(123,saved.SeriesViewsTotal);
    }

    [Fact]
    public void QueryKeepsPlatformIncomeSeparated()
    {
        var repository=Repository(); var date=new DateOnly(2026,9,2);
        repository.UpsertSnapshot(new AccountAnalyticsSnapshot{Platform=PublishPlatform.WeixinChannel,AccountId="w",CollectedAt=DateTimeOffset.UtcNow,AdMonetizationIncomeFen=1000});
        repository.UpsertSubjects([new SubjectDailyAnalyticsRecord{Platform=PublishPlatform.KuaishouPersonalRevenue,AccountId="k",SubjectId="s",SubjectName="剧",MetricDate=date,CollectedAt=DateTimeOffset.UtcNow,Status=AnalyticsRecordStatus.Success,AdIncomeFen=2000}]);
        var data=new AnalyticsQueryService(repository).Query([new("w",PublishPlatform.WeixinChannel,"微信",""),new("k",PublishPlatform.KuaishouPersonalRevenue,"快手","")],date,date);
        Assert.Equal(1000,data.Summary.WeixinIncomeFen); Assert.Equal(2000,data.Summary.KuaishouIncomeFen);
    }

    [Theory]
    [InlineData("1.4万",14000)] [InlineData("2亿",200000000)]
    public void WeixinNumbersAreParsed(string value, decimal expected)=>Assert.Equal(expected,WeixinAnalyticsCollector.Parse(value));

    [Theory]
    [InlineData("￥1.25万",12500)] [InlineData("123",123)]
    public void KuaishouNumbersAreParsed(string value, decimal expected)=>Assert.Equal(expected,KuaishouAnalyticsCollector.ParseNumber(value));

    [Fact]
    public void LegacyDatabaseImportsByAccountName()
    {
        Directory.CreateDirectory(Path.Combine(_root,"data"));
        File.WriteAllText(Path.Combine(_root,"accounts.json"),"{\"accounts\":[{\"id\":\"old\",\"platform\":\"weixin\",\"name\":\"主账号\"}]}");
        using(var connection=new SqliteConnection("Data Source="+Path.Combine(_root,"data","yunfan.sqlite3")+";Pooling=False")){connection.Open();using var command=connection.CreateCommand();command.CommandText="CREATE TABLE analytics_daily_metrics(platform_id TEXT,account_id TEXT,metric_date TEXT,collected_at TEXT,status TEXT,listed_series_total REAL,mounted_video_total REAL,series_views_total REAL,ad_monetization_income_yuan REAL,heating_income_yuan REAL,mounted_income_yuan REAL,estimated_violation_deduction_yuan REAL,message TEXT);INSERT INTO analytics_daily_metrics VALUES('weixin','old','2026-09-02','2026-09-03T00:00:00Z','success',1,2,3,4.5,0,0,0,'');";command.ExecuteNonQuery();}
        var repository=Repository("target.db"); var report=new YunfanAnalyticsImporter(repository).Import(_root,[new("new",PublishPlatform.WeixinChannel,"主账号","")]);
        Assert.Equal(1,report.DailyRecords); Assert.Equal(450,Assert.Single(repository.ListDaily(new(2026,9,2),new(2026,9,2))).AdMonetizationIncomeFen);
    }

    [Fact]
    public void DateRangeRejectsMoreThanThirtyOneDays()=>Assert.Throws<ArgumentOutOfRangeException>(()=>AnalyticsDatePolicy.Range(new(2026,1,1),new(2026,2,1)));

    private AnalyticsRepository Repository(string name="analytics.db"){Directory.CreateDirectory(_root);return new(Path.Combine(_root,name));}
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
