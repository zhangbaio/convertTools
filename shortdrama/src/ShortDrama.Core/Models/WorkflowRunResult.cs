namespace ShortDrama.Core.Models;

public sealed record WorkflowRunResult(
    bool Ok,
    IReadOnlyList<WorkflowStepResult> Steps);

public sealed record WorkflowStepResult(
    string Type,
    bool Ok,
    string? ErrorCode = null,
    string? Message = null,
    IReadOnlyDictionary<string, string>? Outputs = null);
