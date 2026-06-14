namespace ShortDrama.Desktop.Models;

public sealed record DependencyStatusItem(
    string Name,
    bool IsAvailable,
    string? Path,
    string Source,
    string Hint,
    string TestStatus,
    string TestMessage);
