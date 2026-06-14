namespace ShortDrama.Core.Models;

public sealed record WeixinAutomationEvent(
    string Kind,
    string Message,
    string? Phase = null,
    string? Step = null,
    double? Percent = null,
    string? ArtifactPath = null,
    string? Detail = null);
