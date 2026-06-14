namespace ShortDrama.Core.Models;

public sealed record VideoMaterialConvertRequest(
    string ProjectDir,
    string InputDir,
    string OutputDir,
    string? ConfigFile = null,
    bool Overwrite = false);

public sealed record VideoMaterialConvertFailure(
    string InputPath,
    string OutputPath,
    string Message);

public sealed record VideoMaterialConvertResult(
    int TotalFiles,
    int ConvertedFiles,
    int SkippedFiles,
    int FailedFiles,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<VideoMaterialConvertFailure> Failures);

public sealed record VideoMaterialConvertProgress(
    int Index,
    int Total,
    string InputPath,
    string OutputPath,
    string Kind,
    string? Message = null,
    string? Elapsed = null);
