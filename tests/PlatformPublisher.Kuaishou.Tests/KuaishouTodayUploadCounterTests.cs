using DocumentFormat.OpenXml.Packaging;
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

    [Fact]
    public void Shared_workspace_does_not_add_other_accounts_uploads()
    {
        var root = Directory.CreateTempSubdirectory();
        var store = Path.Combine(root.FullName, "records");
        var today = new DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.FromHours(8));
        var mine = Directory.CreateDirectory(Path.Combine(root.FullName, "workflow", "_我的剧"));
        var theirs = Directory.CreateDirectory(Path.Combine(root.FullName, "workflow", "_别人的剧"));
        var untagged = Directory.CreateDirectory(Path.Combine(root.FullName, "workflow", "_没有账号记录"));
        foreach (var directory in new[] { mine, theirs, untagged })
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, ".kuaishou-personal-upload-state.json"),
                $$"""
                {
                  "ReviewSubmitted": true,
                  "UpdatedAt": "{{today:O}}"
                }
                """);
        }

        KuaishouTodayUploadCounter.RecordSuccess(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", mine.FullName, today, store);
        KuaishouTodayUploadCounter.RecordSuccess(
            PublishPlatform.KuaishouPersonalRevenue, "acct-b", theirs.FullName, today, store);

        Assert.Equal(1, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", root.FullName, today.Date, store));
        Assert.Equal(1, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-b", root.FullName, today.Date, store));
    }

    [Fact]
    public void Excel_has_dashboard_and_timeline_sheets_for_the_current_edition()
    {
        var root = Directory.CreateTempSubdirectory();
        var store = Path.Combine(root.FullName, "records");
        var today = new DateTimeOffset(2026, 10, 30, 18, 0, 0, TimeSpan.FromHours(8));
        KuaishouTodayUploadCounter.RecordSuccess(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", Path.Combine(root.FullName, "腕上红线"), today, store);

        var path = KuaishouTodayUploadCounter.ExportExcel(PublishPlatform.KuaishouPersonalRevenue, store);

        using var workbook = SpreadsheetDocument.Open(path, false);
        var names = workbook.WorkbookPart!.Workbook.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
            .Select(sheet => sheet.Name?.Value)
            .ToArray();
        Assert.Equal(new[] { "快手分账个人版看板", "执行流水" }, names);
        Assert.Equal(
            Path.Combine(store, "kuaishou_personal_upload_records.xlsx"),
            path);
    }
}
