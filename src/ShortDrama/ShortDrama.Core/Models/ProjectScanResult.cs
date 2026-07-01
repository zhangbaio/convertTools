namespace ShortDrama.Core.Models;

public sealed record ProjectScanResult(
    string RootDir,
    string? BackupRootDir,
    int TotalProjects,
    int PendingProjects,
    IReadOnlyList<ScannedProject> Projects);

public sealed record ScannedProject(
    string ProjectKey,
    string SourceName,
    string DisplayName,
    string SourceProjectDir,
    string? WorkflowProjectDir,
    string? BackupProjectDir,
    DateTimeOffset? CreatedAt,
    string Status,
    int VideoCount,
    int CompletedSteps,
    int TotalSteps,
    string? ResumeFrom,
    string? FailedStep,
    bool HasFailure);
