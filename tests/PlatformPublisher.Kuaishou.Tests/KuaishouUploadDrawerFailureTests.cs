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
}
