using FluentAssertions;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.ViewModels;
using TikTokPublisher.Ui.Views;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueRunLogResetTests
{
    [Fact]
    public void New_standalone_run_clears_the_previous_log_view()
    {
        var logs = new LogService();
        logs.Append("[10:00:00] ERROR [旧项目] 上一轮失败");
        logs.Append("[10:00:01] SUCCESS [旧项目] 上一轮结束");

        var shouldClear = TikTokQueueView.ShouldClearAllLogsForNewRun(
            anotherQueueIsRunning: false,
            preserveProjectLogsSince: null);
        var removed = shouldClear ? logs.ClearAllEntries() : 0;

        shouldClear.Should().BeTrue();
        removed.Should().Be(1);
        logs.BuildCopyText().Should().Contain("上一轮失败");
        logs.BuildCopyText().Should().NotContain("上一轮结束");

        logs.ClearProjectEntries([new QueueProjectItem { OriginalTitle = "旧项目", ProjectDir = @"E:\old" }])
            .Should().Be(1);
        logs.RenderedEntries.Should().BeEmpty();
    }

    [Fact]
    public void Overflow_keeps_an_older_error_and_drops_progress()
    {
        var logs = new LogService();
        var item = new QueueProjectItem { ProjectDir = @"E:\demo\甲", OriginalTitle = "甲" };
        logs.UpdateSnapshot([new QueueProjectRowViewModel(item)], @"E:\demo", false);
        logs.Append("[10:00:00] ERROR [甲] 下载失败");
        for (var index = 0; index < 5000; index++)
            logs.Append($"[10:00:01] INFO [乙] 进度 {index}");

        logs.SelectedProjectPath = item.ProjectDir;
        logs.BuildCopyText().Should().Contain("下载失败");
        logs.BuildCopyText().Should().NotContain("进度");
    }

    [Fact]
    public void Selected_project_without_generated_logs_does_not_show_history()
    {
        var logs = new LogService();
        var item = new QueueProjectItem
        {
            ProjectDir = @"E:\tiktok2\烬九州：第三十一季",
            OriginalTitle = "烬九州：第三十一季",
            NewTitle = "烬九州：第三十一季",
            LastError = "简介与历史/原简介过于相似",
            StatusText = QueueStepStatus.Failed,
            CurrentStep = QueueStepKeys.RewriteInfo,
        };
        item.StepStates[QueueStepKeys.RewriteInfo] = QueueStepStatus.Failed;
        logs.UpdateSnapshot([new QueueProjectRowViewModel(item)], @"E:\tiktok2", false);
        logs.SelectedProjectPath = item.ProjectDir;

        logs.BuildCopyText().Should().BeEmpty();
        logs.RenderedEntries.Should().BeEmpty();
    }

    [Fact]
    public void Selected_project_shows_only_logs_generated_for_that_project()
    {
        var logs = new LogService();
        var item = new QueueProjectItem
        {
            ProjectDir = @"E:\7.7\7.7\demo",
            OriginalTitle = "หาบหกล้มที่ถูกซ่อนอยู่ในตำนาน",
            NewTitle = "หาบหกล้มที่ถูกซ่อนอยู่ในตำนาน",
            StatusText = QueueStepStatus.Completed,
        };
        logs.UpdateSnapshot([new QueueProjectRowViewModel(item)], @"E:\7.7\7.7", false);
        logs.Append("[10:00:00] INFO [หาบหกล้มที่ถูกซ่อนอยู่ในตำนาน] 本轮开始下载");
        logs.Append("[10:00:01] INFO [其他项目] 另一部剧的日志");
        logs.SelectedProjectPath = item.ProjectDir;

        logs.BuildCopyText().Should().Contain("本轮开始下载");
        logs.BuildCopyText().Should().NotContain("另一部剧的日志");
        logs.BuildCopyText().Should().NotContain("失败记录");
    }

    [Fact]
    public void Parallel_or_preparation_run_keeps_unrelated_visible_logs()
    {
        TikTokQueueView.ShouldClearAllLogsForNewRun(
                anotherQueueIsRunning: true,
                preserveProjectLogsSince: null)
            .Should().BeFalse();
        TikTokQueueView.ShouldClearAllLogsForNewRun(
                anotherQueueIsRunning: false,
                preserveProjectLogsSince: DateTime.Now)
            .Should().BeFalse();
    }
}
