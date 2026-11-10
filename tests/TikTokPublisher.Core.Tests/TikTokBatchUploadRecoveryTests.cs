using FluentAssertions;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokBatchUploadRecoveryTests
{
    [Fact]
    public void Card_failure_log_names_the_episode()
    {
        TikTokBatchUploadService.DescribeFailedEpisodeCards([61])
            .Should().Be("第 61 集卡片显示上传失败");
        TikTokBatchUploadService.DescribeFailedEpisodeCards([61, 62, 63])
            .Should().Be("第 61、62、63 集卡片显示上传失败");
    }

    [Fact]
    public void Hover_point_is_read_from_the_delete_result()
    {
        TikTokBrowserActions.TryReadHoverPoint("need-hover:61:120:340", out var x, out var y)
            .Should().BeTrue();
        x.Should().Be(120);
        y.Should().Be(340);

        TikTokBrowserActions.TryReadHoverPoint("no-button:61", out _, out _).Should().BeFalse();
        TikTokBrowserActions.TryReadHoverPoint("need-hover:61:-1:20", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Stalled_delete_retries_then_refreshes_before_aborting()
    {
        TikTokBrowserActions.DecideDeleteStall(1, 0).Should().Be(new TikTokBrowserActions.DeleteStallDecision(false, false));
        TikTokBrowserActions.DecideDeleteStall(2, 0).Should().Be(new TikTokBrowserActions.DeleteStallDecision(false, false));
        TikTokBrowserActions.DecideDeleteStall(3, 0).Should().Be(new TikTokBrowserActions.DeleteStallDecision(true, false));
        TikTokBrowserActions.DecideDeleteStall(3, 1).Should().Be(new TikTokBrowserActions.DeleteStallDecision(true, false));
        TikTokBrowserActions.DecideDeleteStall(3, 2).Should().Be(new TikTokBrowserActions.DeleteStallDecision(false, true));
    }

    [Fact]
    public void Delete_target_prefers_the_row_text()
    {
        TikTokBrowserActions.DescribeDeleteTarget(123, "第 123 集 重回十八-第124集")
            .Should().Be("第 123 集 重回十八-第124集");
        TikTokBrowserActions.DescribeDeleteTarget(58, "  ")
            .Should().Be("第 58 集");
    }

    [Fact]
    public void Missing_or_disabled_delete_button_can_be_retried()
    {
        TikTokBrowserActions.IsRetryableDeleteMiss("need-hover:61:10:20").Should().BeTrue();
        TikTokBrowserActions.IsRetryableDeleteMiss("disabled:60\t01:20第 60 集").Should().BeTrue();
        TikTokBrowserActions.IsRetryableDeleteMiss("clicked:60\tuid\ttext").Should().BeFalse();
    }
}
