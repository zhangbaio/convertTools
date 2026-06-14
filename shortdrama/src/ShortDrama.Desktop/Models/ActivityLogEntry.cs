namespace ShortDrama.Desktop.Models;

public sealed record ActivityLogEntry(
    string TimestampText,
    string Message,
    string ProjectKey,
    string ProjectLabel,
    string StepKey,
    string StepLabel,
    string DisplayText,
    bool IsFailure);
