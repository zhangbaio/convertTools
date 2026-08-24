using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueStepArtifactConsistencyTests
{
    [Fact]
    public void ScanProjects_resets_explicit_completed_artifacts_that_are_missing()
    {
        using var fixture = new QueueProjectFixture();
        foreach (var step in new[]
                 {
                     QueueStepKeys.Download,
                     QueueStepKeys.GenerateEpisodeScript,
                     QueueStepKeys.GenerateAiScriptOutline,
                     QueueStepKeys.GenerateAiDramaMaterials,
                     QueueStepKeys.GenerateRoleVector,
                     QueueStepKeys.GenerateProjectImages,
                     QueueStepKeys.GenerateTimestampCertificate,
                     QueueStepKeys.MaterialValidate,
                 })
        {
            fixture.Item.StepStates[step] = QueueStepStatus.Completed;
        }

        WorkspaceQueueDatabase.Save(fixture.Workspace, [fixture.Item]);
        var scanned = WorkspaceQueueService.ScanProjects(fixture.Workspace).Should().ContainSingle().Subject;

        scanned.StepStates[QueueStepKeys.Download].Should().Be(QueueStepStatus.Pending);
        scanned.StepStates[QueueStepKeys.GenerateEpisodeScript].Should().Be(QueueStepStatus.Pending);
        scanned.StepStates[QueueStepKeys.GenerateAiScriptOutline].Should().Be(QueueStepStatus.Pending);
        scanned.StepStates[QueueStepKeys.GenerateAiDramaMaterials].Should().Be(QueueStepStatus.Pending);
        scanned.StepStates[QueueStepKeys.GenerateRoleVector].Should().Be(QueueStepStatus.Pending);
        scanned.StepStates[QueueStepKeys.GenerateProjectImages].Should().Be(QueueStepStatus.Pending);
        scanned.StepStates[QueueStepKeys.GenerateTimestampCertificate].Should().Be(QueueStepStatus.Pending);
        scanned.StepStates[QueueStepKeys.MaterialValidate].Should().Be(QueueStepStatus.Pending);
    }

    [Fact]
    public void Completed_ai_drama_material_step_runs_again_when_output_directory_is_missing()
    {
        using var fixture = new QueueProjectFixture();
        fixture.Item.StepStates[QueueStepKeys.GenerateAiDramaMaterials] = QueueStepStatus.Completed;

        QueueWorkerRunner.ShouldRunStep(
                fixture.Item,
                QueueStepKeys.GenerateAiDramaMaterials,
                new QueueRunOptions { EnabledSteps = [QueueStepKeys.GenerateAiDramaMaterials] })
            .Should().BeTrue();
    }

    [Fact]
    public void Completed_timestamp_step_runs_again_when_certificate_is_missing()
    {
        using var fixture = new QueueProjectFixture();
        fixture.Item.StepStates[QueueStepKeys.GenerateTimestampCertificate] = QueueStepStatus.Completed;
        var options = new QueueRunOptions
        {
            EnabledSteps = [QueueStepKeys.GenerateTimestampCertificate],
        };

        QueueWorkerRunner.ShouldRunStep(
                fixture.Item,
                QueueStepKeys.GenerateTimestampCertificate,
                options)
            .Should().BeTrue();

        var output = TikTokTimestampCertificateService.GetOutputPath(fixture.Item);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllBytes(output, "%PDF-1.7\n"u8.ToArray().Concat(new byte[128]).ToArray());

        QueueWorkerRunner.ShouldRunStep(
                fixture.Item,
                QueueStepKeys.GenerateTimestampCertificate,
                options)
            .Should().BeFalse();
    }

    [Fact]
    public void Completed_download_step_recovers_missing_video_only_for_pending_projects()
    {
        using var fixture = new QueueProjectFixture();
        fixture.Item.StepStates[QueueStepKeys.Download] = QueueStepStatus.Completed;
        var options = new QueueRunOptions { EnabledSteps = [QueueStepKeys.Download] };

        QueueWorkerRunner.ShouldRunStep(
                fixture.Item,
                QueueStepKeys.Download,
                options)
            .Should().BeTrue("待上传项目没有任何视频时必须重新下载");

        fixture.Item.StepStates[QueueStepKeys.DeleteSourceVideos] = QueueStepStatus.Completed;
        QueueWorkerRunner.ShouldRunStep(
                fixture.Item,
                QueueStepKeys.Download,
                options)
            .Should().BeFalse("主动删除源视频后不得自动下载回来");

        fixture.Item.StepStates[QueueStepKeys.DeleteSourceVideos] = QueueStepStatus.Pending;
        fixture.Item.StepStates[QueueStepKeys.UploadSeries] = QueueStepStatus.Completed;
        QueueWorkerRunner.ShouldRunStep(
                fixture.Item,
                QueueStepKeys.Download,
                options)
            .Should().BeFalse("已上传项目允许本地视频被清理");
    }

    private sealed class QueueProjectFixture : IDisposable
    {
        public string Workspace { get; }
        public string Source { get; }
        public string Workflow { get; }
        public QueueProjectItem Item { get; }

        public QueueProjectFixture()
        {
            Workspace = Path.Combine(Path.GetTempPath(), $"queue-artifact-consistency-{Guid.NewGuid():N}");
            Source = Path.Combine(Workspace, "source-project");
            Workflow = Path.Combine(Workspace, "workflow", "_新剧名");
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Workflow);
            var metadata = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["projectKey"] = "source-project",
                ["title"] = "原剧名",
                ["originalTitle"] = "原剧名",
                ["sourceProjectDir"] = Source,
                ["workflowProjectDir"] = Workflow,
                ["workflowDirName"] = Path.GetFileName(Workflow),
                ["episodeCount"] = 5,
            });
            File.WriteAllText(Path.Combine(Source, "shortdrama-project.json"), metadata);
            File.WriteAllText(Path.Combine(Workflow, "shortdrama-project.json"), metadata);
            Item = new QueueProjectItem
            {
                ProjectDir = Source,
                OriginalTitle = "原剧名",
                NewTitle = "新剧名",
                EpisodeCount = 5,
                StepStates = new Dictionary<string, string>(),
            };
            Item.NormalizeStepStates();
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Workspace)) Directory.Delete(Workspace, recursive: true);
            }
            catch
            {
                // Best effort cleanup for Windows file locks.
            }
        }
    }
}
