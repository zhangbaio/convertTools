namespace ShortDrama.Core.Models;

public sealed record WorkflowInteractionRequest(
    string RequestId,
    string ProjectKey,
    string DisplayName,
    string StepType,
    string Scope,
    string Stage,
    string Message,
    IReadOnlyList<string> Options);
