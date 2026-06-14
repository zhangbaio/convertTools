namespace ShortDrama.Core.Models;

public sealed record VideoTranscodeFailure(
    string InputPath,
    string OutputPath,
    string Message);

public sealed record VideoTranscodeResult(
    int TotalFiles,
    int TranscodedFiles,
    int SkippedFiles,
    int FailedFiles,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<VideoTranscodeFailure> Failures);
