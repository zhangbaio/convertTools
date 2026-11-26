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

    [Theory]
    [InlineData(PublishPlatform.KuaishouPersonalRevenue, PublishJobStepStatus.Skipped, true, true, "已完成")]
    [InlineData(PublishPlatform.KuaishouEnterpriseRevenue, PublishJobStepStatus.Skipped, true, true, "已完成")]
    [InlineData(PublishPlatform.KuaishouPersonalRevenue, PublishJobStepStatus.Skipped, true, false, "已跳过")]
    [InlineData(PublishPlatform.KuaishouPersonalRevenue, PublishJobStepStatus.Skipped, false, true, "已跳过")]
    [InlineData(PublishPlatform.KuaishouPersonalRevenue, PublishJobStepStatus.Failed, true, true, "失败")]
    [InlineData(PublishPlatform.KuaishouPersonalRevenue, PublishJobStepStatus.Running, true, true, "进行中")]
    public void Dedup_history_recovers_completed_submissions_without_masking_current_failures(
        PublishPlatform platform, PublishJobStepStatus savedStatus, bool submitted, bool usedDedup, string expected)
    {
        var root = Directory.CreateTempSubdirectory("ks-dedup-status-");
        try
        {
            var edition = platform == PublishPlatform.KuaishouEnterpriseRevenue ? "enterprise" : "personal";
            var state = System.Text.Json.JsonSerializer.Serialize(new
            {
                Status = submitted ? "completed" : "draft_saved",
                ReviewSubmitted = submitted,
                VideoBatchIdentity = usedDedup ? new string('A', 64) : "",
            });
            File.WriteAllText(Path.Combine(root.FullName, $".kuaishou-{edition}-upload-state.json"), state);
            var job = KuaishouJob();
            job.Platform = platform;
            job.ProjectDirectory = root.FullName;
            job.StepStates["kuaishou-video-dedup"] = new PublishJobStepState { Status = savedStatus };

            var row = new PublishJobRowViewModel(job, loadArtifacts: true, loadTableInfo: false);

            Assert.Equal(expected, row.VideoDedupStepStatus);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(true, true, "已完成")]
    [InlineData(true, false, "失败")]
    [InlineData(false, true, "失败")]
    public void Bound_draft_keeps_historical_dedup_success_when_only_the_batch_is_missing(
        bool hasBoundBatch, bool missingBatchError, string expected)
    {
        var root = Directory.CreateTempSubdirectory("ks-draft-dedup-status-");
        try
        {
            var upload = System.Text.Json.JsonSerializer.Serialize(new
            {
                Status = "failed",
                MiniSeriesId = "100",
                VideoBatchIdentity = hasBoundBatch ? new string('A', 64) : "",
                ReviewSubmitted = false,
            });
            File.WriteAllText(Path.Combine(root.FullName, ".kuaishou-personal-upload-state.json"), upload);
            var job = KuaishouJob();
            job.ProjectDirectory = root.FullName;
            job.StepStates["kuaishou-video-dedup"] = new PublishJobStepState
            {
                Status = PublishJobStepStatus.Failed,
                Message = missingBatchError
                    ? "已有快手草稿绑定的去重成片缺失或未通过校验。请恢复原批次后续传，或按新分集另建草稿。"
                    : "视频编码失败",
            };
            job.StepStates["weixin-upload"] = new PublishJobStepState { Status = PublishJobStepStatus.Failed };

            var row = new PublishJobRowViewModel(job, loadArtifacts: true, loadTableInfo: false);

            Assert.Equal(expected, row.VideoDedupStepStatus);
            Assert.Equal("失败", row.UploadStepStatus);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Draft_waiting_for_confirmation_replaces_an_old_upload_failure_without_scheduling_a_retry()
    {
        var root = Directory.CreateTempSubdirectory("ks-draft-review-hold-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, ".kuaishou-personal-upload-state.json"),
                """{"status":"failed","current_stage":"episode_upload"}""");
            var job = KuaishouJob();
            job.ProjectDirectory = root.FullName;
            job.Status = PublishJobStatus.Blocked;
            job.ScheduledAt = DateTimeOffset.Now.AddMinutes(-1);
            job.StepStates["weixin-upload"] = new PublishJobStepState
            {
                Status = PublishJobStepStatus.Pending,
                Message = "待确认",
            };

            var row = new PublishJobRowViewModel(job, loadArtifacts: true, loadTableInfo: false);

            Assert.Equal("待确认", row.StatusText);
            Assert.Equal("待确认", row.UploadStepStatus);
            Assert.False(PlatformPublisher.Common.Services.PublishSchedulePolicy.IsDue(job, DateTimeOffset.Now));
        }
        finally
        {
            root.Delete(recursive: true);
        }
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
