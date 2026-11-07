using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouUploadDrawerFailureTests
{
    [Fact]
    public void Finished_summary_with_failures_stops_the_upload_wait()
    {
        var message = KuaishouPersonalEpisodeUploadService.ReadFinishedFailure("上传完成 0集成功，152集失败");
        Assert.Equal("快手个人版视频上传结束：0 集成功，152 集失败。", message);
    }

    [Fact]
    public void Zero_failures_and_warning_text_do_not_stop_the_wait()
    {
        Assert.Null(KuaishouPersonalEpisodeUploadService.ReadFinishedFailure("上传完成 152集成功，0集失败"));
        Assert.Null(KuaishouPersonalEpisodeUploadService.ReadFinishedFailure("为避免上传失败，请检查网络"));
        Assert.Null(KuaishouPersonalEpisodeUploadService.ReadFinishedFailure("上传进度 46%"));
    }

    [Fact]
    public void Stale_failure_summary_does_not_fail_before_this_upload_starts()
    {
        var stale = KuaishouPersonalEpisodeUploadService.ReadFinishedFailure("上传完成 0集成功，152集失败");
        Assert.False(KuaishouPersonalEpisodeUploadService.ShouldStopForUploadFailure(
            stale,
            baselineFailure: stale,
            sawThisUpload: false,
            stillUploading: false));
    }

    [Fact]
    public void Failure_summary_after_this_upload_stops_the_wait()
    {
        var failure = KuaishouPersonalEpisodeUploadService.ReadFinishedFailure("上传完成 0集成功，152集失败");
        Assert.True(KuaishouPersonalEpisodeUploadService.ShouldStopForUploadFailure(
            failure,
            baselineFailure: null,
            sawThisUpload: true,
            stillUploading: false));
        Assert.False(KuaishouPersonalEpisodeUploadService.ShouldStopForUploadFailure(
            failure,
            baselineFailure: null,
            sawThisUpload: true,
            stillUploading: true));
    }

    [Fact]
    public void Repair_page_keeps_the_old_upload_failure_banner_without_failing_submit()
    {
        const string banner = "上传失败：第10集上传失败，请重新上传。待提交";
        Assert.False(KuaishouPersonalEpisodeUploadService.ReviewPageShowsNewFailure(banner, banner));
    }

    [Fact]
    public void A_new_submit_failure_still_stops_the_review_wait()
    {
        Assert.True(KuaishouPersonalEpisodeUploadService.ReviewPageShowsNewFailure(
            "上传失败：第10集上传失败，请重新上传。提交失败",
            "上传失败：第10集上传失败，请重新上传。"));
        Assert.True(KuaishouPersonalEpisodeUploadService.ReviewPageShowsNewFailure("提交失败", null));
    }

    [Fact]
    public void Preview_on_a_card_finishes_the_gap_upload_even_if_an_old_error_class_remains()
    {
        Assert.Equal("uploaded", KuaishouPersonalEpisodeUploadService.ClassifyGapCard("第10集 视频预览"));
        Assert.Equal("uploading", KuaishouPersonalEpisodeUploadService.ClassifyGapCard("第10集 上传中 46%"));
        Assert.Equal("pending", KuaishouPersonalEpisodeUploadService.ClassifyGapCard("第10集 视频无法正常播放，建议重新制作"));
    }

    [Fact]
    public void Finished_drawer_with_no_failures_can_be_closed()
    {
        Assert.False(KuaishouPersonalEpisodeUploadService.GapDrawerReadyToClose("上传中 46% 上传完成 0集成功，1集失败"));
        Assert.True(KuaishouPersonalEpisodeUploadService.GapDrawerReadyToClose(
            "上传完成 0集成功，1集失败 上传完成 1集成功，0集失败"));
    }
}
