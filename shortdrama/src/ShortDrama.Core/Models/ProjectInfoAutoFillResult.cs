namespace ShortDrama.Core.Models;

public sealed record ProjectInfoAutoFillResult(
    string WorkflowInfoPath,
    IReadOnlyList<string> UpdatedFields)
{
    public bool Changed => UpdatedFields.Count > 0;
}
