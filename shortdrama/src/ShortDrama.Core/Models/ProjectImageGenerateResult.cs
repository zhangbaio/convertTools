namespace ShortDrama.Core.Models;

public sealed record ProjectImageGenerateResult(
    int Count,
    IReadOnlyList<string> Outputs);
