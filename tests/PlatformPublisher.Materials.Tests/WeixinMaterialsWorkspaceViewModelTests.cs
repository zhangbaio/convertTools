using ChannelsPublisher.Core.Models;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Desktop.Services;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using Xunit;

namespace PlatformPublisher.Materials.Tests;

public sealed class WeixinMaterialsWorkspaceViewModelTests
{
    [Fact]
    public async Task Scan_builds_project_inventory_and_filters_without_losing_selection()
    {
        var root = Temp();
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "原剧")).FullName;
            var workflow = Directory.CreateDirectory(Path.Combine(root, "workflow", "新剧")).FullName;
            var materials = Directory.CreateDirectory(Path.Combine(workflow, "materials", "adx")).FullName;
            await File.WriteAllTextAsync(Path.Combine(materials, "1.mp4"), "video");
            await File.WriteAllTextAsync(Path.Combine(materials, "2.mp4"), "video");
            var scan = new ProjectScanResult(root, null, 1, 1,
            [
                new ScannedProject("project-1", "原剧", "原剧", "新剧", source, workflow, null,
                    null, "未开始", 23, 0, 8, null, null, false),
            ]);
            var viewModel = new WeixinMaterialsWorkspaceViewModel(new StubScanner(scan));
            var account = new PublishAccount { Id = "acct-1", Name = "账号一", WorkRootDirectory = root };

            viewModel.ApplyAccounts([account], account);
            await viewModel.ScanAsync();

            var project = Assert.Single(viewModel.Projects);
            Assert.Equal("原剧", project.OriginalTitle);
            Assert.Equal("新剧", project.NewTitle);
            Assert.Equal(2, project.MaterialCount);
            project.IsSelected = true;
            viewModel.Query = "新剧";
            Assert.True(Assert.Single(viewModel.Projects).IsSelected);
            Assert.Equal("已选择 1 / 1", viewModel.SelectionSummary);
            viewModel.Query = "不存在";
            Assert.Empty(viewModel.Projects);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Account_context_uses_its_persisted_workspace()
    {
        var viewModel = new WeixinMaterialsWorkspaceViewModel(new StubScanner(
            new ProjectScanResult(string.Empty, null, 0, 0, [])));
        var first = new PublishAccount { Id = "a", Name = "甲", WorkRootDirectory = "D:\\甲" };
        var second = new PublishAccount { Id = "b", Name = "乙", WorkRootDirectory = "D:\\乙" };

        viewModel.ApplyAccounts([first, second], second);

        Assert.Same(second, viewModel.SelectedAccount);
        Assert.Equal("D:\\乙", viewModel.WorkspaceRoot);
    }

    [Fact]
    public void Highlight_interval_schedule_respects_last_run_time()
    {
        var now = new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.FromHours(8));
        var rule = WeixinHighlightScheduleRule.Create("account", "D:\\workspace") with
        {
            TriggerMode = "interval",
            IntervalMinutes = 30,
        };

        Assert.False(WeixinHighlightScheduleService.IsDue(rule,
            new WeixinHighlightScheduleState(now.AddMinutes(-10), string.Empty), now, startup: false));
        Assert.True(WeixinHighlightScheduleService.IsDue(rule,
            new WeixinHighlightScheduleState(now.AddMinutes(-31), string.Empty), now, startup: false));
    }

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "weixin-material-workspace-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubScanner(ProjectScanResult result) : IProjectScanner
    {
        public Task<ProjectScanResult> ScanAsync(string rootDir, string? backupRootDir,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
