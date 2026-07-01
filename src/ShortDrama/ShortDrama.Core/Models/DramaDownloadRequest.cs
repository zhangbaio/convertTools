namespace ShortDrama.Core.Models;

public sealed record DramaDownloadRequest(
    string ProjectDir,
    string OutputDir,
    string DisplayName,
    string? BookId,
    string Episodes,
    string Quality,
    int Concurrent);

public sealed record DramaDownloadResult(
    bool Ok,
    string OutputDir,
    int VideoCount,
    string? Message = null);
