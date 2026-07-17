namespace ShortDrama.Core.Models;

public sealed record WorkRunResult(
    string RootDir,
    string? BackupRootDir,
    int TotalProjects,
    int SucceededProjects,
    int FailedProjects,
    int SkippedProjects,
    IReadOnlyList<ProjectWorkResult> Projects);

public sealed record ProjectWorkResult(
    string ProjectKey,
    string DisplayName,
    string WorkflowProjectDir,
    bool Ok,
    bool Skipped,
    string? Message,
    IReadOnlyList<WorkflowStepResult> Steps);
