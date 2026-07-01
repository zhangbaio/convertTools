namespace ShortDrama.Core.Models;

public sealed record MaterialValidationIssue(
    string Code,
    string Severity,
    string Message,
    string? RelatedPath = null,
    bool CanAutoFix = false);

public sealed record MaterialValidationResult(
    IReadOnlyList<MaterialValidationIssue> Issues)
{
    public bool HasErrors => Issues.Any(item => string.Equals(item.Severity, "错误", StringComparison.Ordinal));
    public bool HasWarnings => Issues.Any(item => string.Equals(item.Severity, "警告", StringComparison.Ordinal));
}
