using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class CopyrightProofQueuePreparationServiceTests
{
    [Fact]
    public void Prepare_ReopensOnlyTargetsAndReusesCurrentProofMaterial()
    {
        var root = Path.Combine(Path.GetTempPath(), $"copyright-proof-retry-{Guid.NewGuid():N}");
        var reusable = Project(
            Path.Combine(root, "reusable"),
            QueueStepStatus.Failed,
            QueueStepStatus.Failed);
        var pending = Project(
            Path.Combine(root, "pending"),
            QueueStepStatus.Stopped,
            QueueStepStatus.Stopped);
        var unrelated = Project(
            Path.Combine(root, "unrelated"),
            QueueStepStatus.Completed,
            QueueStepStatus.Completed);
        reusable.CurrentStep = QueueStepRegistry.GenerateProofMaterial;
        reusable.StatusText = QueueStepStatus.Failed;
        reusable.LastError = "旧错误";

        var summary = CopyrightProofQueuePreparationService.Prepare(
            [reusable, pending, unrelated],
            [reusable, pending],
            [reusable.ProjectDir]);

        Assert.Equal(2, summary.TargetCount);
        Assert.Equal(1, summary.ReusedProofMaterialCount);
        Assert.Equal(1, summary.PendingProofMaterialCount);
        Assert.True(reusable.Enabled);
        Assert.True(pending.Enabled);
        Assert.False(unrelated.Enabled);
        Assert.Equal(
            QueueStepStatus.Completed,
            reusable.StepStates[QueueStepRegistry.GenerateProofMaterial]);
        Assert.Equal(
            QueueStepStatus.Pending,
            pending.StepStates[QueueStepRegistry.GenerateProofMaterial]);
        Assert.Equal(
            QueueStepStatus.Pending,
            reusable.StepStates[QueueStepRegistry.UploadSeries]);
        Assert.Equal(
            QueueStepStatus.Pending,
            pending.StepStates[QueueStepRegistry.UploadSeries]);
        Assert.Equal(string.Empty, reusable.CurrentStep);
        Assert.Equal(QueueStepStatus.Pending, reusable.StatusText);
        Assert.Equal(string.Empty, reusable.LastError);
        Assert.Equal(
            QueueStepStatus.Completed,
            unrelated.StepStates[QueueStepRegistry.UploadSeries]);
    }

    [Fact]
    public void Prepare_generate_only_preserves_existing_upload_states()
    {
        var root = Path.Combine(Path.GetTempPath(), $"copyright-proof-generate-only-{Guid.NewGuid():N}");
        var completedUpload = Project(
            Path.Combine(root, "completed"),
            QueueStepStatus.Failed,
            QueueStepStatus.Completed);
        var stoppedUpload = Project(
            Path.Combine(root, "stopped"),
            QueueStepStatus.Stopped,
            QueueStepStatus.Stopped);

        var summary = CopyrightProofQueuePreparationService.Prepare(
            [completedUpload, stoppedUpload],
            [completedUpload, stoppedUpload],
            [],
            CopyrightProofExecutionMode.GenerateMaterialOnly);

        Assert.Equal(2, summary.PendingProofMaterialCount);
        Assert.Equal(
            QueueStepStatus.Completed,
            completedUpload.StepStates[QueueStepRegistry.UploadSeries]);
        Assert.Equal(
            QueueStepStatus.Stopped,
            stoppedUpload.StepStates[QueueStepRegistry.UploadSeries]);
        Assert.Equal(
            QueueStepStatus.Pending,
            completedUpload.StepStates[QueueStepRegistry.GenerateProofMaterial]);
        Assert.Equal(
            QueueStepStatus.Pending,
            stoppedUpload.StepStates[QueueStepRegistry.GenerateProofMaterial]);
    }

    [Fact]
    public void Prepare_reopens_skipped_material_steps_but_preserves_completed_steps()
    {
        var root = Path.Combine(Path.GetTempPath(), $"copyright-proof-planned-steps-{Guid.NewGuid():N}");
        var project = Project(
            Path.Combine(root, "target"),
            QueueStepStatus.Completed,
            QueueStepStatus.Completed);
        project.StepStates[QueueStepRegistry.GenerateAiScriptOutline] = QueueStepStatus.Skipped;
        project.StepStates[QueueStepRegistry.GenerateTimestampCertificate] = QueueStepStatus.Completed;
        project.StepStates[QueueStepRegistry.GenerateRoleVector] = QueueStepStatus.Completed;

        CopyrightProofQueuePreparationService.Prepare(
            [project],
            [project],
            [project.ProjectDir],
            CopyrightProofExecutionMode.GenerateAndEdit,
            [
                QueueStepRegistry.GenerateAiScriptOutline,
                QueueStepRegistry.GenerateRoleVector,
                QueueStepRegistry.GenerateProofMaterial,
                QueueStepRegistry.GenerateTimestampCertificate,
            ]);

        Assert.Equal(
            QueueStepStatus.Pending,
            project.StepStates[QueueStepRegistry.GenerateAiScriptOutline]);
        Assert.Equal(
            QueueStepStatus.Completed,
            project.StepStates[QueueStepRegistry.GenerateTimestampCertificate]);
        Assert.Equal(
            QueueStepStatus.Completed,
            project.StepStates[QueueStepRegistry.GenerateRoleVector]);
        Assert.Equal(
            QueueStepStatus.Completed,
            project.StepStates[QueueStepRegistry.GenerateProofMaterial]);
        Assert.Equal(
            QueueStepStatus.Pending,
            project.StepStates[QueueStepRegistry.UploadSeries]);
    }

    private static QueueProjectItem Project(
        string projectDir,
        string proofStatus,
        string uploadStatus) =>
        new()
        {
            ProjectDir = projectDir,
            NewTitle = Path.GetFileName(projectDir),
            Enabled = true,
            StepStates = new Dictionary<string, string>
            {
                [QueueStepRegistry.GenerateProofMaterial] = proofStatus,
                [QueueStepRegistry.UploadSeries] = uploadStatus,
            },
        };
}
