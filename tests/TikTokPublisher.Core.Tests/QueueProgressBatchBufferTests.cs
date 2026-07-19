using FluentAssertions;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueProgressBatchBufferTests
{
    [Fact]
    public void Drain_preserves_every_download_message_in_arrival_order()
    {
        var buffer = new QueueProgressBatchBuffer();

        buffer.Enqueue(CreateProgress(QueueStepRegistry.Download, "[01/18] 开始下载第01集")).Should().BeTrue();
        buffer.Enqueue(CreateProgress(QueueStepRegistry.Download, "[02/18] 开始下载第02集")).Should().BeFalse();
        buffer.Enqueue(CreateProgress(QueueStepRegistry.Download, "[03/18] 开始下载第03集")).Should().BeFalse();

        buffer.Drain().Select(progress => progress.Message).Should().Equal(
            "[01/18] 开始下载第01集",
            "[02/18] 开始下载第02集",
            "[03/18] 开始下载第03集");
    }

    [Fact]
    public void Drain_still_coalesces_high_frequency_messages_for_other_steps()
    {
        var buffer = new QueueProgressBatchBuffer();

        buffer.Enqueue(CreateProgress(QueueStepRegistry.UploadSeries, "上传进度 10%")).Should().BeTrue();
        buffer.Enqueue(CreateProgress(QueueStepRegistry.UploadSeries, "上传进度 60%")).Should().BeFalse();
        buffer.Enqueue(CreateProgress(QueueStepRegistry.UploadSeries, "上传进度 100%")).Should().BeFalse();

        buffer.Drain().Select(progress => progress.Message).Should().Equal("上传进度 100%");
    }

    [Fact]
    public void Enqueue_requests_another_flush_after_the_previous_batch_is_drained()
    {
        var buffer = new QueueProgressBatchBuffer();

        buffer.Enqueue(CreateProgress(QueueStepRegistry.Download, "第一批")).Should().BeTrue();
        buffer.Drain().Should().ContainSingle();

        buffer.Enqueue(CreateProgress(QueueStepRegistry.Download, "第二批")).Should().BeTrue();
    }

    [Fact]
    public void Drain_preserves_daily_limit_notification_even_when_a_later_upload_message_arrives()
    {
        var buffer = new QueueProgressBatchBuffer();

        buffer.Enqueue(CreateProgress(
            QueueStepRegistry.UploadSeries,
            "已达单日创建剧集上限，任务队列已停止，请明天再继续上传"));
        buffer.Enqueue(CreateProgress(QueueStepRegistry.UploadSeries, "队列收尾完成"));

        buffer.Drain().Select(progress => progress.Message).Should().Equal(
            "已达单日创建剧集上限，任务队列已停止，请明天再继续上传",
            "队列收尾完成");
    }

    private static QueueWorkerProgress CreateProgress(string stepKey, string message) => new()
    {
        WorkspaceRoot = @"D:\work",
        StepKey = stepKey,
        Message = message,
    };
}
