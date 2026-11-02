using PlatformPublisher.Common.Models;
using PlatformPublisher.Desktop.ViewModels;
using Xunit;

namespace PlatformPublisher.Desktop.Tests;

public sealed class PublishJobRowStatusTests
{
    [Fact]
    public void Failed_rewrite_shows_failed_status_when_artifacts_are_still_pending()
    {
        var row = RowWith(PublishJobStepStatus.Failed, "快手发布信息不合格：命中禁用词：契约婚姻、逆袭");

        Assert.Equal("失败", row.StatusText);
        Assert.Equal("失败", row.RewriteStepStatus);
    }

    [Fact]
    public void Pending_rewrite_stays_pending()
    {
        var row = RowWith(PublishJobStepStatus.Pending, "");

        Assert.Equal("等待执行", row.StatusText);
    }

    [Fact]
    public void Succeeded_rewrite_shows_completed_status()
    {
        var job = KuaishouJob();
        job.Status = PublishJobStatus.Succeeded;
        job.StepStates["rewrite"] = new PublishJobStepState
        {
            Key = "rewrite",
            Status = PublishJobStepStatus.Succeeded,
            Message = "执行完成",
        };
        var row = new PublishJobRowViewModel(job, loadArtifacts: false, loadTableInfo: false);

        Assert.Equal("已完成", row.StatusText);
    }

    private static PublishJobRowViewModel RowWith(PublishJobStepStatus rewriteStatus, string message)
    {
        var job = KuaishouJob();
        job.StepStates["rewrite"] = new PublishJobStepState
        {
            Key = "rewrite",
            Label = "改写信息",
            Status = rewriteStatus,
            Message = message,
        };
        return new PublishJobRowViewModel(job, loadArtifacts: false, loadTableInfo: false);
    }

    private static PublishJob KuaishouJob() => new()
    {
        Platform = PublishPlatform.KuaishouPersonalRevenue,
        ProjectName = "旧爱归零：科研大佬她不伺候了3",
        ProjectDirectory = @"E:\kuaishou\红果_旧爱归零：科研大佬她不伺候了3",
        Status = PublishJobStatus.Pending,
    };
}
