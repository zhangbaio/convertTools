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
