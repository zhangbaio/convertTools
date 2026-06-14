namespace ShortDrama.Core.Models;

public sealed record WorkRunEvent(
    string ProjectKey,
    string DisplayName,
    string Kind,
    string? StepType = null,
    string? Message = null,
    bool? Ok = null);
