using FluentAssertions;
using TikTokPublisher.Core.Services;
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

        check.Should().Be(new TikTokBatchUploadService.UploadOrderCheck(6, 2, 5, Duplicated: true));
        TikTokBatchUploadService.CheckUploadedOrder(Rows(1, 2, 3, 4, 3, 6), [4, 5, 6])
            .Should().Be(new TikTokBatchUploadService.UploadOrderCheck(6, 1, 3, Duplicated: true));
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

    private static readonly TikTokUploadActivity Idle = new(false, 0, true);

    [Fact]
    public void Episodes_are_ready_only_when_every_episode_is_processed()
    {
        TikTokBatchUploadService.JudgeAllEpisodesReady(62, 62, 62, 0, Idle)
            .Should().Be(TikTokBatchUploadService.EpisodeReadyVerdict.Ready);
        // 行都在，但平台只处理完 47 集。
        TikTokBatchUploadService.JudgeAllEpisodesReady(47, 62, 62, 0, Idle)
            .Should().Be(TikTokBatchUploadService.EpisodeReadyVerdict.Waiting);
        TikTokBatchUploadService.JudgeAllEpisodesReady(62, 62, 62, 0, new TikTokUploadActivity(true, 0, true))
            .Should().Be(TikTokBatchUploadService.EpisodeReadyVerdict.Waiting);
        TikTokBatchUploadService.JudgeAllEpisodesReady(62, 62, 62, 1, Idle)
            .Should().Be(TikTokBatchUploadService.EpisodeReadyVerdict.Waiting);
        TikTokBatchUploadService.JudgeAllEpisodesReady(null, 62, 62, 0, Idle)
            .Should().Be(TikTokBatchUploadService.EpisodeReadyVerdict.Ready);
    }

    [Fact]
    public void Missing_rows_are_reported_once_the_page_is_idle()
    {
        TikTokBatchUploadService.JudgeAllEpisodesReady(60, 60, 62, 0, Idle)
            .Should().Be(TikTokBatchUploadService.EpisodeReadyVerdict.RowsMissing);
        TikTokBatchUploadService.JudgeAllEpisodesReady(58, 60, 62, 0, new TikTokUploadActivity(false, 2, true))
            .Should().Be(TikTokBatchUploadService.EpisodeReadyVerdict.Waiting);
        TikTokBatchUploadService.JudgeAllEpisodesReady(59, 60, 62, 1, Idle)
            .Should().Be(TikTokBatchUploadService.EpisodeReadyVerdict.Waiting);
        TikTokBatchUploadService.JudgeAllEpisodesReady(63, 63, 62, 0, Idle)
            .Should().Be(TikTokBatchUploadService.EpisodeReadyVerdict.RowsExtra);
        TikTokBatchUploadService.JudgeAllEpisodesReady(60, 63, 62, 0, Idle)
            .Should().Be(TikTokBatchUploadService.EpisodeReadyVerdict.RowsExtra);
    }

    [Fact]
    public void Reorder_moves_a_late_episode_back_with_one_drag()
    {
        // 第 13 集补传后接在第 27 集后面。
        var reals = Enumerable.Range(1, 12).Concat(Enumerable.Range(14, 14)).Append(13).ToArray();

        TikTokBrowserActions.PlanEpisodeReorder(reals)
            .Should().Equal(new TikTokBrowserActions.EpisodeReorderMove(13, 26, 12));
    }

    [Fact]
    public void Reorder_plan_sorts_by_episode_number()
    {
        TikTokBrowserActions.PlanEpisodeReorder([1, 2, 3]).Should().BeEmpty();
        TikTokBrowserActions.PlanEpisodeReorder([3, 1, 2])
            .Should().Equal(
                new TikTokBrowserActions.EpisodeReorderMove(1, 1, 0),
                new TikTokBrowserActions.EpisodeReorderMove(2, 2, 1));
        TikTokBrowserActions.PlanEpisodeReorder([1, 2, 4, 5, 3])
            .Should().Equal(new TikTokBrowserActions.EpisodeReorderMove(3, 4, 2));
    }

    [Fact]
    public void Reorder_is_refused_for_duplicate_or_unreadable_cards()
    {
        TikTokBrowserActions.PlanEpisodeReorder([1, 3, 3]).Should().BeNull();
        TikTokBrowserActions.PlanEpisodeReorder([1, 0, 2]).Should().BeNull();
    }

    [Theory]
    [InlineData("{\"code\":0,\"message\":\"success\"}", false)]
    [InlineData("{\"code\":3016056,\"msg\":\"bad\"}", true)]
    [InlineData("{\"status_code\":7}", true)]
    [InlineData("", false)]
    [InlineData("not json", false)]
    public void Reorder_response_with_a_non_zero_code_is_rejected(string body, bool rejected)
    {
        TikTokBrowserActions.ReorderResponseRejected(body).Should().Be(rejected);
    }

    [Fact]
    public void Missing_or_disabled_delete_button_can_be_retried()
    {
        TikTokBrowserActions.IsRetryableDeleteMiss("need-hover:61:10:20").Should().BeTrue();
        TikTokBrowserActions.IsRetryableDeleteMiss("disabled:60\t01:20第 60 集").Should().BeTrue();
        TikTokBrowserActions.IsRetryableDeleteMiss("clicked:60\tuid\ttext").Should().BeFalse();
    }
}
