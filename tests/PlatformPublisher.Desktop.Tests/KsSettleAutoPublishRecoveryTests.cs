using PlatformPublisher.Desktop.Services;
using Xunit;

namespace PlatformPublisher.Desktop.Tests;

public sealed class KsSettleAutoPublishRecoveryTests
{
    [Theory]
    [InlineData("success", true, "", true, KsSettleAutoPublishRecovery.Action.None)]
    [InlineData("accepted", true, "", true, KsSettleAutoPublishRecovery.Action.Resume)]
    [InlineData("running", true, "success", false, KsSettleAutoPublishRecovery.Action.ReportSuccess)]
    [InlineData("accepted", false, "", false, KsSettleAutoPublishRecovery.Action.ReportInterrupted)]
    [InlineData("dispatched", false, "", false, KsSettleAutoPublishRecovery.Action.None)]
    [InlineData("running", false, "failed", true, KsSettleAutoPublishRecovery.Action.ReportFailed)]
    [InlineData("retry_wait", false, "failed", true, KsSettleAutoPublishRecovery.Action.None)]
    public void Decide_follows_the_server_and_local_marker(
        string serverStatus,
        bool markerActive,
        string markerFinal,
        bool projectExists,
        KsSettleAutoPublishRecovery.Action expected)
    {
        var actual = KsSettleAutoPublishRecovery.Decide(serverStatus, markerActive, markerFinal, projectExists);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("第2集无法播放，当前 FFmpeg 不支持该编码。下载未完成", "download")]
    [InlineData("没有 FFmpeg 可播放的下载地址，已跳过。", "download")]
    [InlineData("下载剧集失败。", "download")]
    [InlineData("改写信息失败", "")]
    [InlineData("账号未授权或积分不足", "")]
    public void FailureKindForPipeline_marks_only_download_failures(string error, string expected)
    {
        Assert.Equal(expected, KsSettleAutoPublishRecovery.FailureKindForPipeline(error));
    }

    [Fact]
    public void Decide_leaves_a_scheduled_retry_for_the_command()
    {
        var actual = KsSettleAutoPublishRecovery.Decide("dispatched", false, "failed", true, 1);
        Assert.Equal(KsSettleAutoPublishRecovery.Action.None, actual);
    }
}
