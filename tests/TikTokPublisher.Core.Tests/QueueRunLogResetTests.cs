using FluentAssertions;
using TikTokPublisher.Ui.Services;
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
        removed.Should().Be(2);
        logs.RenderedEntries.Should().BeEmpty();
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
