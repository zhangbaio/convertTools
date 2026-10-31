using PlatformPublisher.Desktop.ViewModels;
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
}
