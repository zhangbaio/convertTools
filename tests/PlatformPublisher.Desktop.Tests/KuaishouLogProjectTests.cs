using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Desktop.Views;
using Xunit;

namespace PlatformPublisher.Desktop.Tests;

public sealed class KuaishouLogProjectTests
{
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
}
