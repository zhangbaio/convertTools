using PlatformPublisher.Common.Models;
using Xunit;
using PlatformPublisher.Kuaishou.Publishing;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouTodayUploadCounterTests
{
    [Fact]
    public void Personal_and_enterprise_counts_stay_separate_for_the_same_account()
    {
        var root = Directory.CreateTempSubdirectory();
        var store = Path.Combine(root.FullName, "records");
        var today = new DateTimeOffset(2026, 10, 30, 18, 0, 0, TimeSpan.FromHours(8));
        KuaishouTodayUploadCounter.RecordSuccess(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", Path.Combine(root.FullName, "个人剧"), today, store);
        KuaishouTodayUploadCounter.RecordSuccess(
            PublishPlatform.KuaishouEnterpriseRevenue, "acct-a", Path.Combine(root.FullName, "企业剧"), today, store);
        KuaishouTodayUploadCounter.RecordSuccess(
            PublishPlatform.KuaishouPersonalRevenue, "acct-b", Path.Combine(root.FullName, "别人的剧"), today, store);

        Assert.Equal(1, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", root.FullName, today.Date, store));
        Assert.Equal(1, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouEnterpriseRevenue, "acct-a", root.FullName, today.Date, store));
    }

    [Fact]
    public void Same_project_uploaded_twice_today_counts_once_and_workspace_state_fills_missing_records()
    {
        var root = Directory.CreateTempSubdirectory();
        var store = Path.Combine(root.FullName, "records");
        var workflow = Directory.CreateDirectory(Path.Combine(root.FullName, "workflow", "_红线尽头不是你"));
        var today = new DateTimeOffset(2026, 10, 30, 17, 24, 0, TimeSpan.FromHours(8));
        File.WriteAllText(
            Path.Combine(workflow.FullName, ".kuaishou-personal-upload-state.json"),
            $$"""
            {
              "ReviewSubmitted": true,
              "UpdatedAt": "{{today:O}}"
            }
            """);
        KuaishouTodayUploadCounter.RecordSuccess(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", workflow.FullName, today, store);
        KuaishouTodayUploadCounter.RecordSuccess(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", workflow.FullName, today.AddMinutes(5), store);

        Assert.Equal(1, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", root.FullName, today.Date, store));
        Assert.Equal(0, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouEnterpriseRevenue, "acct-a", root.FullName, today.Date, store));
    }
}
