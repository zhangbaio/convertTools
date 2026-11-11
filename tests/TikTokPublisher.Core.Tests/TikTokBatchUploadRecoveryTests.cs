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

    private static List<TikTokBrowserActions.EditVideoRow> Rows(params int[] reals) =>
        reals.Select((real, index) => new TikTokBrowserActions.EditVideoRow(index + 1, real)).ToList();

    [Fact]
    public void Batch_order_check_passes_when_every_uploaded_episode_is_in_place()
    {
        var check = TikTokBatchUploadService.CheckUploadedOrder(Rows(1, 2, 3, 4, 5, 6), [1, 2, 3, 4, 5, 6]);

        check.Should().Be(new TikTokBatchUploadService.UploadOrderCheck(6, 6, 0));
    }

    [Fact]
    public void Batch_order_check_finds_the_first_misplaced_episode()
    {
        // 第 3 集重试后被接到了末尾。
        var check = TikTokBatchUploadService.CheckUploadedOrder(Rows(1, 2, 4, 5, 6, 3), [1, 2, 3, 4, 5, 6]);

        check.Should().Be(new TikTokBatchUploadService.UploadOrderCheck(6, 2, 4));
    }

    [Fact]
    public void Batch_order_check_compares_only_the_rows_after_existing_draft_episodes()
    {
        // 编辑补传：草稿已有 1～3 集，本次补 4～6 集，第 5 集重复。
        var check = TikTokBatchUploadService.CheckUploadedOrder(Rows(1, 2, 3, 4, 5, 5), [4, 5, 6]);

        check.Should().Be(new TikTokBatchUploadService.UploadOrderCheck(6, 2, 5));
        TikTokBatchUploadService.CheckUploadedOrder(Rows(1, 2, 3, 4, 3, 6), [4, 5, 6])
            .Should().Be(new TikTokBatchUploadService.UploadOrderCheck(6, 1, 3));
    }

    [Fact]
    public void Batch_order_check_gives_up_when_rows_or_episode_numbers_are_incomplete()
    {
        TikTokBatchUploadService.CheckUploadedOrder(Rows(1, 2), [1, 2, 3]).Should().BeNull();
        TikTokBatchUploadService.CheckUploadedOrder(
                [new(1, 1), new(3, 3)],
                [1, 2])
            .Should().BeNull();
        TikTokBatchUploadService.CheckUploadedOrder(Rows(1, 2, 3), [1, 0, 3]).Should().BeNull();
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
