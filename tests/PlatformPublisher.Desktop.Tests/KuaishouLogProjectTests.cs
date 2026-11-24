using PlatformPublisher.Common.Models;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Desktop.Views;
using TikTokPublisher.Core.Services;
using Xunit;

namespace PlatformPublisher.Desktop.Tests;

public sealed class KuaishouLogProjectTests
{
    [Theory]
    [InlineData("[16:06:43] [69/110] 开始下载第69集", "离职后前东家慌了", "[16:06:43] [离职后前东家慌了] [69/110] 开始下载第69集")]
    [InlineData("[16:06:43] [青瑶入海] 下载完成", "青瑶入海", "[16:06:43] [青瑶入海] 下载完成")]
    [InlineData("[16:06:43] 已开始执行队列", null, "[16:06:43] 已开始执行队列")]
    public void Formats_project_log_lines_for_display_and_copy(string text, string? projectName, string expected)
    {
        Assert.Equal(expected, KuaishouQueueLogView.FormatProjectLogLine(text, projectName));
    }

    [Fact]
    public void Failed_project_keeps_its_own_log_after_other_projects_fill_the_shared_limit()
    {
        var failed = new KuaishouLogProject("青瑶入海", @"E:\kuaishou4\青瑶入海", "failed");
        var later = new KuaishouLogProject("黄蛇还命", @"E:\kuaishou4\黄蛇还命", "ok");
        failed.AppendLine("改写信息失败：接口返回错误");
        for (var index = 0; index < 600; index++)
            later.AppendLine($"下载分集 {index}");

        var lines = failed.CopyLines();
        Assert.Single(lines);
        Assert.Contains("改写信息失败", lines[0]);
    }

    [Fact]
    public void Project_log_keeps_the_latest_failure_when_its_own_lines_overflow()
    {
        var project = new KuaishouLogProject("青瑶入海", @"E:\kuaishou4\青瑶入海", "failed");
        for (var index = 0; index < KuaishouLogProject.MaxLines + 20; index++)
            project.AppendLine($"过程 {index}");
        project.AppendLine("共享项目流水线失败：改写信息失败");

        var lines = project.CopyLines();
        Assert.Equal(KuaishouLogProject.MaxLines, lines.Count);
        Assert.Contains("改写信息失败", lines[^1]);
    }

    [Fact]
    public void Saved_upload_limit_failure_appears_above_progress_logs()
    {
        const string reason = "快手分账今日新建短剧已达 10 部。还没有草稿的剧今天不再创建，上传失败的草稿可以继续。";
        const string projectName = "测试短剧原剧名";
        var job = new PublishJob { ProjectName = projectName, Status = PublishJobStatus.Failed, StatusMessage = reason };
        job.StepStates["weixin-upload"] = new PublishJobStepState
        {
            Label = "上传剧集",
            Status = PublishJobStepStatus.Failed,
            Message = reason,
            UpdatedAt = new DateTimeOffset(2026, 1, 1, 16, 35, 56, TimeSpan.FromHours(8)),
        };
        var progress = ("[16:35:55] 快手分账 · 企业：单集封面：快手竖屏海报.jpg", projectName);

        var lines = KuaishouQueueLogView.WithSavedFailures(job, [progress]).ToArray();

        Assert.Equal(2, lines.Length);
        Assert.Equal(("[16:35:56] 上传剧集失败：今日的短剧上传已达上限", projectName), lines[0]);
        Assert.Equal("error", LogMessageLevelClassifier.InferLevel(lines[0].Text));
        Assert.Equal(progress, lines[1]);
    }

    [Fact]
    public void Saved_job_error_appears_when_no_step_error_or_progress_log_exists()
    {
        var job = new PublishJob
        {
            ProjectName = "测试短剧",
            Status = PublishJobStatus.Failed,
            StatusMessage = "账号登录状态已失效",
        };

        var line = Assert.Single(KuaishouQueueLogView.WithSavedFailures(job, []));

        Assert.EndsWith("任务失败：账号登录状态已失效", line.Text);
        Assert.Equal(job.ProjectName, line.ProjectName);
    }

    [Fact]
    public void Project_list_shows_the_new_title_and_keeps_the_folder_name_when_it_is_missing()
    {
        Assert.Equal("晚灯归人", KuaishouQueueLogView.ProjectListTitle(" 晚灯归人 ", "凰权若梦"));
        Assert.Equal("凰权若梦", KuaishouQueueLogView.ProjectListTitle("  ", "凰权若梦"));
        Assert.Equal("凰权若梦", KuaishouQueueLogView.ProjectListTitle(null, "凰权若梦"));
    }

    [Fact]
    public void A_later_run_lists_only_the_projects_checked_for_that_run()
    {
        var next = new KuaishouLogProject("烟火照青云", @"E:\kuaishou4\烟火照青云", "waiting");

        var kept = KuaishouLogProject.ForThisRun([next]);

        var only = Assert.Single(kept);
        Assert.Equal("烟火照青云", only.ProjectName);
    }

    [Fact]
    public void Retrying_a_failed_project_replaces_the_old_log_entry()
    {
        var retry = new KuaishouLogProject("万兽认我当干妈", @"E:\kuaishou4\万兽认我当干妈", "waiting");

        var kept = KuaishouLogProject.ForThisRun([retry]);

        Assert.Single(kept);
        Assert.Equal("waiting", kept[0].Tone);
    }

    [Fact]
    public void Log_row_uses_this_run_and_keeps_history_for_projects_not_started()
    {
        Assert.Equal("ok", KuaishouQueueLogView.RowTone("ok", "failed"));
        Assert.Equal("failed", KuaishouQueueLogView.RowTone("failed", "ok"));
        Assert.Equal("running", KuaishouQueueLogView.RowTone("running", "failed"));
        Assert.Equal("none", KuaishouQueueLogView.RowTone("none", "failed"));
        Assert.Equal("failed", KuaishouQueueLogView.RowTone("waiting", "failed"));
    }

    [Fact]
    public void Log_list_counts_only_the_dramas_still_checked()
    {
        var jobs = new (string Name, string? Title, string Directory, bool IsChecked)[]
        {
            ("婚礼当天，我将未婚夫七天无理由退货", null, @"E:\kuaishou3\婚礼当天，我将未婚夫七天无理由退货", true),
            ("心正则刀正", null, @"E:\kuaishou3\心正则刀正", true),
            ("打破那道家规", "知夏的家宅新篇", @"E:\kuaishou3\打破那道家规", true),
            ("前世债今生不还表妹的八十万担保书", null, @"E:\kuaishou3\前世债今生不还表妹的八十万担保书", true),
            ("说好退婚却动心了", null, @"E:\kuaishou3\说好退婚却动心了", false),
            ("拒嫁病秧子，我娶王爷当赘婿", null, @"E:\kuaishou3\拒嫁病秧子，我娶王爷当赘婿", false),
        };

        var listed = KuaishouQueueLogView.WhereChecked(
            jobs,
            job => job.IsChecked,
            job => job.Name,
            job => job.Directory);

        Assert.Equal(4, listed.Count);
        Assert.Equal("知夏的家宅新篇", KuaishouQueueLogView.ProjectListTitle(listed[2].Title, listed[2].Name));
        Assert.DoesNotContain(listed, job => job.Name == "说好退婚却动心了");
        Assert.False(KuaishouQueueLogView.MatchesCheckedJob(
            "说好退婚却动心了",
            @"E:\kuaishou3\workflow\说好退婚却动心了",
            "打破那道家规",
            @"E:\kuaishou3\打破那道家规"));
        Assert.True(KuaishouQueueLogView.MatchesCheckedJob(
            "打破那道家规",
            @"E:\kuaishou3\打破那道家规",
            "打破那道家规",
            @"E:\kuaishou3\打破那道家规"));
    }

    [Fact]
    public void A_project_waiting_for_the_upload_slot_stays_on_the_log_list_and_is_not_failed()
    {
        var existing = new List<KuaishouLogProject>
        {
            new("渔村炊烟起，闺女笑声甜", @"E:\kuaishou4\渔村炊烟起，闺女笑声甜", "running"),
        };
        existing[0].AppendLine("[09:42:46] 上传进度：94%");
        KuaishouLogProject.MergeInto(existing,
        [
            new KuaishouLogProject("这碗面八万", @"E:\kuaishou4\这碗面八万", "running"),
        ]);

        Assert.Equal(2, existing.Count);
        Assert.Contains("94%", existing[0].CopyLines()[0]);
        Assert.Equal("running", KuaishouQueueLogView.ToneFromStatuses("失败", "等待槽位", ["失败", "已完成", "等待槽位"]));
        Assert.Equal("running", KuaishouQueueLogView.RowTone("waiting", "running"));
    }
}
