using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IWorkService
{
    Task<WorkRunResult> RunAsync(
        string rootDir,
        string? backupRootDir,
        bool force,
        IProgress<WorkRunEvent>? progress,
        CancellationToken cancellationToken);

    Task<ProjectWorkResult> RunProjectAsync(
        string sourceProjectDir,
        string? backupRootDir,
        bool force,
        IProgress<WorkRunEvent>? progress,
        CancellationToken cancellationToken);

    Task<ProjectWorkResult> RunProjectStepAsync(
        string sourceProjectDir,
        string? backupRootDir,
        string stepType,
        bool force,
        IProgress<WorkRunEvent>? progress,
        CancellationToken cancellationToken,
        string? configOverridePath = null);

    Task<ProjectTitleUpdateResult> UpdateProjectTitleAsync(
        string sourceProjectDir,
        string? backupRootDir,
        string newTitle,
        CancellationToken cancellationToken);

    Task<int> RefreshWeixinConfigsAsync(
        string sourceProjectDir,
        string? backupRootDir,
        CancellationToken cancellationToken);

    Task<string> EnsureWeixinUploadConfigAsync(
        string sourceProjectDir,
        string? backupRootDir,
        CancellationToken cancellationToken);
}
