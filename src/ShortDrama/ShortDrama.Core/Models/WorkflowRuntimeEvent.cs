namespace ShortDrama.Core.Models;

public sealed record WorkflowRuntimeEvent(
    string StepType,
    string Kind,
    string? Message = null);
