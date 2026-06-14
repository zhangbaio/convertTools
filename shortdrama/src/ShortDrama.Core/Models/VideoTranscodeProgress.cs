namespace ShortDrama.Core.Models;

public sealed record VideoTranscodeProgress(
    int Index,
    int Total,
    string InputPath,
    string OutputPath,
    string Kind,
    string? Elapsed = null,
    string? Message = null);
