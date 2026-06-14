namespace ShortDrama.Desktop.Models;

public sealed record MaterialValidationIssueItem(
    string Code,
    string Severity,
    string Message,
    string? RelatedPath,
    bool CanAutoFix);
