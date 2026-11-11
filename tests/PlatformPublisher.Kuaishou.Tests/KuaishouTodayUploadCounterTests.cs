using System.Text.Json;
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
        var personal = KuaishouTodayUploadCounter.ListToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", today.Date, store);
        var only = Assert.Single(personal);
        Assert.Equal("个人剧", only.Title);
        Assert.Empty(KuaishouTodayUploadCounter.ListToday(
            PublishPlatform.KuaishouEnterpriseRevenue, "acct-b", today.Date, store));
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
    public void Yesterday_uploads_are_not_counted_after_midnight()
    {
        var root = Directory.CreateTempSubdirectory();
        var store = Path.Combine(root.FullName, "records");
        var yesterday = new DateTimeOffset(2026, 10, 31, 23, 40, 0, TimeSpan.FromHours(8));
        KuaishouTodayUploadCounter.RecordSuccess(
            PublishPlatform.KuaishouPersonalRevenue,
            "acct-a",
            Path.Combine(root.FullName, "昨天的剧"),
            yesterday,
            store);

        Assert.Equal(0, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", root.FullName, new DateTime(2026, 11, 1), store));
        Assert.Equal(1, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", root.FullName, new DateTime(2026, 10, 31), store));
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
    public void Finished_run_counts_even_when_the_state_file_still_says_running()
    {
        var root = Directory.CreateTempSubdirectory();
        var store = Path.Combine(root.FullName, "records");
        var finished = Directory.CreateDirectory(Path.Combine(root.FullName, "workflow", "_已完成"));
        var draft = Directory.CreateDirectory(Path.Combine(root.FullName, "workflow", "_草稿"));
        File.WriteAllText(Path.Combine(finished.FullName, ".kuaishou-personal-upload-state.json"), """{"Status":"running"}""");
        File.WriteAllText(Path.Combine(draft.FullName, ".kuaishou-personal-upload-state.json"), """{"Status":"draft_saved"}""");
        var today = new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.FromHours(8));

        KuaishouTodayUploadCounter.AppendRun(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", "快手分账个人", finished.FullName, succeeded: true, error: "", store);
        KuaishouTodayUploadCounter.AppendRun(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", "快手分账个人", draft.FullName, succeeded: true, error: "", store);

        Assert.Equal(1, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", root.FullName, today.Date, store));
    }

    [Fact]
    public void Upload_state_completed_today_counts_without_a_ledger_row()
    {
        var root = Directory.CreateTempSubdirectory();
        var store = Path.Combine(root.FullName, "records");
        var today = new DateTimeOffset(2026, 11, 5, 9, 19, 0, TimeSpan.FromHours(8));
        var finished = Directory.CreateDirectory(Path.Combine(root.FullName, "workflow", "_娘家第二季"));
        var yesterday = Directory.CreateDirectory(Path.Combine(root.FullName, "workflow", "_昨天完成"));
        File.WriteAllText(
            Path.Combine(finished.FullName, ".kuaishou-personal-upload-state.json"),
            $$"""
            {
              "Status": "completed",
              "ReviewSubmitted": true,
              "UpdatedAt": "{{today:O}}"
            }
            """);
        File.WriteAllText(
            Path.Combine(yesterday.FullName, ".kuaishou-personal-upload-state.json"),
            """
            {
              "Status": "completed",
              "ReviewSubmitted": true,
              "UpdatedAt": "2026-11-04T18:00:00+08:00"
            }
            """);

        Assert.Equal(1, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue,
            "acct-a",
            root.FullName,
            today.Date,
            store,
            [finished.FullName, yesterday.FullName]));
    }

    [Fact]
    public void Archived_upload_completed_today_still_counts_for_the_same_account()
    {
        var root = Directory.CreateTempSubdirectory();
        var store = Path.Combine(root.FullName, "records");
        var today = new DateTimeOffset(2026, 11, 5, 12, 40, 0, TimeSpan.FromHours(8));
        var archive = Path.Combine(root.FullName, "archive");
        WriteArchivedUpload(archive, "acct-a", "_娘家第二季", today);
        WriteArchivedUpload(archive, "acct-a", "_昨天完成", new DateTimeOffset(2026, 11, 4, 18, 0, 0, TimeSpan.FromHours(8)));
        WriteArchivedUpload(archive, "acct-b", "_别人的剧", today);

        Assert.Equal(1, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue,
            "acct-a",
            root.FullName,
            today.Date,
            store,
            archiveRoot: archive));
        KuaishouTodayUploadCounter.RecordSuccess(
            PublishPlatform.KuaishouPersonalRevenue,
            "acct-a",
            Path.Combine(root.FullName, "workflow", "_娘家第二季"),
            today,
            store);
        Assert.Equal(1, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue,
            "acct-a",
            root.FullName,
            today.Date,
            store,
            archiveRoot: archive));
    }

    [Fact]
    public void Archived_workflow_copy_does_not_count_a_project_twice()
    {
        var root = Directory.CreateTempSubdirectory();
        var store = Path.Combine(root.FullName, "records");
        var today = new DateTimeOffset(2026, 11, 6, 18, 2, 0, TimeSpan.FromHours(8));
        var source = Path.Combine(root.FullName, "workspace", "娘家第二季");
        Directory.CreateDirectory(source);
        KuaishouTodayUploadCounter.RecordSuccess(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", source, today, store);
        var archivedWorkflow = Path.Combine(root.FullName, "archive", "workflow", "_娘家第二季-20261106-180200");
        Directory.CreateDirectory(archivedWorkflow);
        KuaishouTodayUploadCounter.RecordSuccess(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", archivedWorkflow, today.AddMinutes(1), store);

        var archive = Path.Combine(root.FullName, "archive");
        WriteArchivedUpload(archive, "acct-a", "_娘家第二季-20261106-180200", today);

        Assert.Equal(1, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue,
            "acct-a",
            root.FullName,
            today.Date,
            store,
            archiveRoot: archive));
        Assert.Single(KuaishouTodayUploadCounter.ListToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", today.Date, store));
    }

    [Fact]
    public void Hongguo_source_and_renamed_workflow_count_as_one_drama()
    {
        var root = Directory.CreateTempSubdirectory();
        var store = Path.Combine(root.FullName, "records");
        var today = new DateTimeOffset(2026, 11, 11, 9, 24, 35, TimeSpan.FromHours(8));
        WriteSuccess(store, "acct-a", Path.Combine(root.FullName, "archive", "workflow", "_加薪还是加糖"), "程野的护心抉择", "加薪还是加糖", today);
        WriteSuccess(store, "acct-a", Path.Combine(root.FullName, "红果_加薪还是加糖"), "红果_加薪还是加糖", "红果_加薪还是加糖", today.AddSeconds(3));
        WriteSuccess(store, "acct-a", Path.Combine(root.FullName, "archive", "workflow", "_陈茶为证赴旧约"), "陈茶为证赴旧约", "一罐陈茶赴三十年旧约", today.AddMinutes(12));
        WriteSuccess(store, "acct-a", Path.Combine(root.FullName, "红果_一罐陈茶赴三十年旧约"), "红果_一罐陈茶赴三十年旧约", "红果_一罐陈茶赴三十年旧约", today.AddMinutes(12).AddSeconds(3));
        WriteSuccess(store, "acct-a", Path.Combine(root.FullName, "红果_医魂觉醒：从乡镇医院到京都三甲第九季"), "杏林仁心行", "医魂觉醒：从乡镇医院到京都三甲第九季", today.AddMinutes(3));

        Assert.Equal(3, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", root.FullName, today.Date, store));
        var uploads = KuaishouTodayUploadCounter.ListToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", today.Date, store);
        Assert.Equal(3, uploads.Count);
        Assert.Contains(uploads, upload => upload.Title == "程野的护心抉择");
        Assert.Contains(uploads, upload => upload.Title == "陈茶为证赴旧约");
        Assert.Contains(uploads, upload => upload.Title == "杏林仁心行");
    }

    [Fact]
    public void Archived_hongguo_workflow_does_not_count_again_when_the_source_is_already_counted()
    {
        var root = Directory.CreateTempSubdirectory();
        var store = Path.Combine(root.FullName, "records");
        var today = new DateTimeOffset(2026, 11, 11, 9, 36, 34, TimeSpan.FromHours(8));
        var source = Directory.CreateDirectory(Path.Combine(root.FullName, "红果_一罐陈茶赴三十年旧约"));
        File.WriteAllText(Path.Combine(source.FullName, "短剧信息.txt"), "原剧名：一罐陈茶赴三十年旧约");
        File.WriteAllText(
            Path.Combine(source.FullName, ".kuaishou-personal-upload-state.json"),
            $$"""
            {
              "Status": "completed",
              "ReviewSubmitted": true,
              "SubmittedAt": "{{today:O}}"
            }
            """);
        var archive = Path.Combine(root.FullName, "archive");
        var workflow = Directory.CreateDirectory(Path.Combine(archive, "workflow", "_陈茶为证赴旧约"));
        File.WriteAllText(Path.Combine(workflow.FullName, "短剧信息.txt"), "原剧名：一罐陈茶赴三十年旧约");
        File.WriteAllText(
            Path.Combine(workflow.FullName, ".kuaishou-personal-upload-state.json"),
            $$"""
            {
              "Status": "completed",
              "ReviewSubmitted": true,
              "SubmittedAt": "{{today:O}}"
            }
            """);
        Directory.CreateDirectory(Path.Combine(archive, "meta"));
        File.WriteAllText(
            Path.Combine(archive, "meta", "红果_一罐陈茶赴三十年旧约.json"),
            JsonSerializer.Serialize(new
            {
                accountProfileId = "acct-a",
                originalTitle = "一罐陈茶赴三十年旧约",
                sourceProjectDir = source.FullName,
                archivedWorkflowDir = workflow.FullName,
            }));

        Assert.Equal(1, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue,
            "acct-a",
            root.FullName,
            today.Date,
            store,
            [source.FullName],
            archiveRoot: archive));
        Assert.Single(KuaishouTodayUploadCounter.ListToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", today.Date, store));
    }

    private static void WriteSuccess(
        string store,
        string accountId,
        string directory,
        string newTitle,
        string originalTitle,
        DateTimeOffset recordedAt)
    {
        KuaishouUploadRecordStore.Upsert(new KuaishouUploadRecord(
            "personal",
            accountId,
            "",
            recordedAt,
            "成功",
            string.IsNullOrWhiteSpace(newTitle) ? originalTitle : newTitle,
            originalTitle,
            newTitle,
            "",
            0,
            directory,
            ""), store);
    }

    private static void WriteArchivedUpload(string archive, string accountId, string name, DateTimeOffset updatedAt)
    {
        var workflow = Directory.CreateDirectory(Path.Combine(archive, "workflow", name));
        var state = Path.Combine(workflow.FullName, ".kuaishou-personal-upload-state.json");
        File.WriteAllText(state, $$"""
            {
              "Status": "completed",
              "ReviewSubmitted": true,
              "UpdatedAt": "{{updatedAt:O}}"
            }
            """);
        Directory.CreateDirectory(Path.Combine(archive, "meta"));
        File.WriteAllText(
            Path.Combine(archive, "meta", name + ".json"),
            JsonSerializer.Serialize(new { accountProfileId = accountId, archivedWorkflowDir = workflow.FullName }));
    }

    [Fact]
    public void Repair_does_not_move_an_earlier_upload_into_today()
    {
        var root = Directory.CreateTempSubdirectory();
        var store = Path.Combine(root.FullName, "records");
        var project = Directory.CreateDirectory(Path.Combine(root.FullName, "workflow", "_饺香满街"));
        var yesterday = new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.FromHours(8));
        var today = new DateTimeOffset(2026, 11, 9, 20, 48, 0, TimeSpan.FromHours(8));
        KuaishouTodayUploadCounter.RecordSuccess(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", project.FullName, yesterday, store);
        File.WriteAllText(
            Path.Combine(project.FullName, ".kuaishou-personal-upload-state.json"),
            $$"""
            {
              "Status": "completed",
              "ReviewSubmitted": true,
              "SubmittedAt": "{{yesterday:O}}",
              "UpdatedAt": "{{today:O}}"
            }
            """);
        KuaishouTodayUploadCounter.AppendRun(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", "快手分账个人", project.FullName, succeeded: true, error: "", store);

        Assert.Equal(0, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", root.FullName, today.Date, store, [project.FullName]));
        Assert.Empty(KuaishouTodayUploadCounter.ListToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", today.Date, store));
        Assert.Equal(1, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", root.FullName, yesterday.Date, store, [project.FullName]));
    }

    [Fact]
    public void First_review_submitted_today_still_counts()
    {
        var root = Directory.CreateTempSubdirectory();
        var store = Path.Combine(root.FullName, "records");
        var project = Directory.CreateDirectory(Path.Combine(root.FullName, "workflow", "_临危受命守大乾"));
        var today = new DateTimeOffset(2026, 11, 9, 22, 11, 0, TimeSpan.FromHours(8));
        File.WriteAllText(
            Path.Combine(project.FullName, ".kuaishou-personal-upload-state.json"),
            $$"""
            {
              "Status": "completed",
              "ReviewSubmitted": true,
              "SubmittedAt": "{{today:O}}",
              "UpdatedAt": "{{today:O}}"
            }
            """);

        Assert.Equal(1, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", root.FullName, today.Date, store, [project.FullName]));
        Assert.Single(KuaishouTodayUploadCounter.ListToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", today.Date, store));
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
        Assert.Equal(new[] { "快手分账个人版" }, names);
        Assert.Equal(
            Path.Combine(store, "kuaishou_personal_upload_records.xlsx"),
            path);
    }

    [Fact]
    public void Json_ledger_collapses_to_one_row_and_keeps_author_episode_count_and_nickname()
    {
        var root = Directory.CreateTempSubdirectory();
        var store = Path.Combine(root.FullName, "records");
        Directory.CreateDirectory(store);
        var project = Path.Combine(root.FullName, "旧名");
        File.WriteAllText(Path.Combine(store, "kuaishou_personal_upload_records.json"),
            """
            [
              {"记录时间":"2026-11-05 09:00:00","日期":"2026-11-05","上传状态":"成功","账号档案":"acct-a","项目名称":"旧名","原剧名":"旧名","新剧名":"","作者":"","项目路径":"PROJECT","剧集数":"","下载":"已完成","第一页提交动作":"下一步"},
              {"记录时间":"2026-11-05 10:00:00","日期":"2026-11-05","上传状态":"成功","账号档案":"acct-a","项目名称":"新名","原剧名":"旧名","新剧名":"新名","作者":"星燃","项目路径":"PROJECT","剧集数":"80","当前阶段":"review"}
            ]
            """.Replace("PROJECT", project.Replace("\\", "\\\\"), StringComparison.Ordinal));

        Assert.Equal(1, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", null, new DateTime(2026, 11, 5), store, nickname: "星燃漫剧"));
        Assert.Equal(1, KuaishouTodayUploadCounter.CountToday(
            PublishPlatform.KuaishouPersonalRevenue, "acct-a", null, new DateTime(2026, 11, 5), store, nickname: "后来改的昵称"));

        var record = Assert.Single(KuaishouUploadRecordStore.List("personal", store));
        Assert.Equal("星燃", record.Author);
        Assert.Equal(80, record.EpisodeCount);
        Assert.Equal("新名", record.NewTitle);
        Assert.Equal("星燃漫剧", record.Nickname);
        Assert.True(File.Exists(Path.Combine(store, "kuaishou_personal_upload_records.json")));
    }
}
