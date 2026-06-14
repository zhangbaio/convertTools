namespace ShortDrama.Core.Models;

public sealed record WorkflowDefinition(
    string ProjectKey,
    string DisplayName,
    string ProjectDir,
    string? ConfigDir,
    IReadOnlyList<WorkflowStep> Steps);
