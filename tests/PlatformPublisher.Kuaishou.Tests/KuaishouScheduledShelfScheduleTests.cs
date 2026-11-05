using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouScheduledShelfScheduleTests
{
    private static readonly DateTimeOffset Ten = new(2026, 11, 5, 10, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Disabled_is_never_due()
    {
        Assert.False(KuaishouScheduledShelfSchedule.IsDue(false, "interval", 1, "10:00", null, Ten));
    }

    [Fact]
    public void Interval_runs_immediately_then_waits_for_the_gap()
    {
        Assert.True(KuaishouScheduledShelfSchedule.IntervalDue(1, null, Ten));
        Assert.False(KuaishouScheduledShelfSchedule.IntervalDue(1, Ten.AddMinutes(-30), Ten));
        Assert.True(KuaishouScheduledShelfSchedule.IntervalDue(1, Ten.AddMinutes(-61), Ten));
    }

    [Fact]
    public void Daily_runs_once_after_the_clock_time()
    {
        Assert.False(KuaishouScheduledShelfSchedule.DailyDue("10:00", null, Ten.AddMinutes(-1)));
        Assert.True(KuaishouScheduledShelfSchedule.DailyDue("10:00", null, Ten.AddMinutes(20)));
        Assert.False(KuaishouScheduledShelfSchedule.DailyDue("10:00", Ten.AddMinutes(5), Ten.AddHours(3)));
        Assert.True(KuaishouScheduledShelfSchedule.DailyDue("10:00", Ten, Ten.AddDays(1).AddMinutes(5)));
        Assert.False(KuaishouScheduledShelfSchedule.DailyDue("25:00", null, Ten.AddHours(2)));
    }

    [Fact]
    public void Clock_normalizes_to_hour_and_minute()
    {
        Assert.Equal("10:00", KuaishouScheduledShelfSchedule.NormalizeClock("10:00"));
        Assert.Equal("09:05", KuaishouScheduledShelfSchedule.NormalizeClock("9:5"));
        Assert.Equal("10:00", KuaishouScheduledShelfSchedule.NormalizeClock("明早"));
    }

    [Fact]
    public void Risk_offline_rows_are_not_shelved()
    {
        const string listed = "北疆风雪定九州\n短剧ID：49648910\n已下架\n发布成功\n查看\n编辑\n上架\n预约上架";
        const string risk = "归宗真女续篇\n短剧ID：49144514\n平台风控下架\n发布成功\n查看\n编辑\n上架\n预约上架";
        Assert.True(KuaishouScheduledShelfRow.CanShelf(listed));
        Assert.False(KuaishouScheduledShelfRow.CanShelf(risk));
        Assert.Equal("49648910", KuaishouScheduledShelfRow.Key(listed));
        Assert.Equal("归宗真女续篇", KuaishouScheduledShelfRow.Title(risk));
    }

    [Fact]
    public void Last_run_roundtrips_beside_the_auth_state()
    {
        var root = Directory.CreateTempSubdirectory();
        var config = new KuaishouPersonalConfig { AuthStatePath = Path.Combine(root.FullName, "auth.json") };
        Assert.Null(KuaishouScheduledShelfStateStore.ReadLastRun(config));
        KuaishouScheduledShelfStateStore.WriteLastRun(config, Ten);
        Assert.Equal(Ten, KuaishouScheduledShelfStateStore.ReadLastRun(config));
    }
}
