using FluentAssertions;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;
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
    public void Selected_failure_without_memory_logs_shows_history_and_current_error()
    {
        var logs = new LogService
        {
            LoadProjectHistory = _ =>
            [
                new TikTokExecutionHistoryService.TikTokProjectHistoryEvent(
                    new DateTime(2026, 10, 13, 22, 59, 52),
                    "烬九州：第三十一季",
                    "改写信息 失败：简介与历史/原简介过于相似",
                    true),
            ],
        };
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

        var text = logs.BuildCopyText();
        text.Should().Contain("最近 30 天内的失败记录");
        text.Should().Contain("简介与历史/原简介过于相似");
        text.Should().Contain("2026-10-13 22:59:52");
    }

    [Fact]
    public void Completed_project_without_current_failure_hides_old_error_history()
    {
        var logs = new LogService
        {
            LoadProjectHistory = _ =>
            [
                new TikTokExecutionHistoryService.TikTokProjectHistoryEvent(
                    new DateTime(2026, 10, 20, 20, 37, 49),
                    "หาบหกล้มที่ถูกซ่อนอยู่ในตำนาน",
                    "海报 失败：未找到可用于生成海报的封面图片。",
                    true),
            ],
        };
        var item = new QueueProjectItem
        {
            ProjectDir = @"E:\7.7\7.7\demo",
            OriginalTitle = "หาบหกล้มที่ถูกซ่อนอยู่ในตำนาน",
            NewTitle = "หาบหกล้มที่ถูกซ่อนอยู่ในตำนาน",
            StatusText = QueueStepStatus.Completed,
        };
        item.StepStates[QueueStepKeys.Download] = QueueStepStatus.Completed;
        item.StepStates[QueueStepKeys.RewriteInfo] = QueueStepStatus.Completed;
        item.StepStates[QueueStepKeys.GeneratePoster] = QueueStepStatus.Completed;
        logs.UpdateSnapshot([new QueueProjectRowViewModel(item)], @"E:\7.7\7.7", false);
        logs.SelectedProjectPath = item.ProjectDir;

        logs.BuildCopyText().Should().NotContain("失败记录");
        logs.BuildCopyText().Should().NotContain("未找到可用于生成海报的封面图片");
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
