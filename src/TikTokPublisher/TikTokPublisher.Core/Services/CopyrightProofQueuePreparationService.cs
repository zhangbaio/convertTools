using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public sealed record CopyrightProofQueuePreparationSummary(
    int TargetCount,
    int ReusedProofMaterialCount,
    int PendingProofMaterialCount);

/// <summary>
/// Prepares an exact set of current queue projects for the copyright-proof-only workflow.
/// Existing generated artifacts are kept; only queue execution states are normalized.
/// </summary>
public static class CopyrightProofQueuePreparationService
{
    public static CopyrightProofQueuePreparationSummary Prepare(
        IEnumerable<QueueProjectItem> allProjects,
        IEnumerable<QueueProjectItem> targetProjects,
        IEnumerable<string> reusableProofMaterialProjectDirs)
    {
        ArgumentNullException.ThrowIfNull(allProjects);
        ArgumentNullException.ThrowIfNull(targetProjects);
        ArgumentNullException.ThrowIfNull(reusableProofMaterialProjectDirs);

        var projects = allProjects.ToArray();
        var targetDirs = targetProjects
            .Where(item => !item.Archived && !string.IsNullOrWhiteSpace(item.ProjectDir))
            .Select(item => Path.GetFullPath(item.ProjectDir))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reusableDirs = reusableProofMaterialProjectDirs
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var targets = projects
            .Where(item =>
                !item.Archived &&
                !string.IsNullOrWhiteSpace(item.ProjectDir) &&
                targetDirs.Contains(Path.GetFullPath(item.ProjectDir)))
            .GroupBy(item => Path.GetFullPath(item.ProjectDir), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var matchedTargetDirs = targets
            .Select(item => Path.GetFullPath(item.ProjectDir))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in projects)
        {
            item.Enabled =
                !string.IsNullOrWhiteSpace(item.ProjectDir) &&
                matchedTargetDirs.Contains(Path.GetFullPath(item.ProjectDir));
        }

        var reusedCount = 0;
        foreach (var item in targets)
        {
            item.NormalizeStepStates();
            var proofMaterialCurrent = reusableDirs.Contains(Path.GetFullPath(item.ProjectDir));
            item.StepStates[QueueStepRegistry.GenerateProofMaterial] = proofMaterialCurrent
                ? QueueStepStatus.Completed
                : QueueStepStatus.Pending;
            if (proofMaterialCurrent)
                reusedCount++;

            // UploadSeries is the execution slot used by copyright_proof_only to edit
            // an existing TikTok project. Always reopen it for the selected retry.
            item.StepStates[QueueStepRegistry.UploadSeries] = QueueStepStatus.Pending;
            item.CurrentStep = string.Empty;
            item.StatusText = QueueStepStatus.Pending;
            item.LastError = string.Empty;
        }

        return new CopyrightProofQueuePreparationSummary(
            targets.Length,
            reusedCount,
            targets.Length - reusedCount);
    }
}
