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
    public void Running_poster_step_overrides_previous_failure_in_drama_title()
    {
        var job = KuaishouJob();
        job.Status = PublishJobStatus.Failed;
        job.StepStates["weixin-upload"] = new PublishJobStepState { Status = PublishJobStepStatus.Failed };
        job.StepStates["poster-rename"] = new PublishJobStepState { Status = PublishJobStepStatus.Running };
        var row = new PublishJobRowViewModel(job, loadArtifacts: false, loadTableInfo: false);

        Assert.Equal("执行中", row.PosterStepStatus);
        Assert.Same(row.PosterStepBrush, row.DramaTitleBrush);
    }

    [Fact]
    public void Queued_retry_hides_the_previous_upload_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), "ks-retry-status-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "黑道往事");
        var workflow = Path.Combine(root, "workflow", "_旧巷风起于飞");
        try
        {
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(workflow);
            File.WriteAllText(Path.Combine(source, "shortdrama-project.json"),
                $$"""
                { "workflowProjectDir": {{Json(workflow)}}, "sourceProjectDir": {{Json(source)}} }
                """);
            File.WriteAllText(Path.Combine(workflow, ".kuaishou-personal-upload-state.json"),
                """
                { "Status": "failed", "CurrentStage": "first_page", "LastError": "未知的男女频类型" }
                """);

            var waiting = KuaishouJob();
            waiting.ProjectDirectory = source;
            waiting.Status = PublishJobStatus.Pending;
            waiting.StepStates["weixin-upload"] = new PublishJobStepState
            {
                Key = "weixin-upload",
                Label = "上传剧集",
                Status = PublishJobStepStatus.Pending,
                Message = "未执行",
            };
            var waitingRow = new PublishJobRowViewModel(waiting, loadArtifacts: true, loadTableInfo: false);

            var failed = KuaishouJob();
            failed.ProjectDirectory = source;
            failed.Status = PublishJobStatus.Failed;
            failed.StepStates["weixin-upload"] = new PublishJobStepState
            {
                Key = "weixin-upload",
                Label = "上传剧集",
                Status = PublishJobStepStatus.Failed,
                Message = "未知的男女频类型",
            };
            var failedRow = new PublishJobRowViewModel(failed, loadArtifacts: true, loadTableInfo: false);

            Assert.Equal("未执行", waitingRow.UploadStepStatus);
            Assert.Equal("未执行", waitingRow.StatusText);
            Assert.Equal("失败", failedRow.UploadStepStatus);
            Assert.NotSame(failedRow.DramaTitleBrush, waitingRow.DramaTitleBrush);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string Json(string path) => System.Text.Json.JsonSerializer.Serialize(path);

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
