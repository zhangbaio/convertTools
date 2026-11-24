namespace ShortDrama.Core.Models;

public enum ExistingVideoPolicy
{
    ReuseValid,
    ReplaceInvalid,
    ReplaceAll,
}

public sealed record DramaDownloadRequest(
    string ProjectDir,
    string OutputDir,
    string DisplayName,
    string? BookId,
    string Episodes,
    string Quality,
    int Concurrent,
    string EpisodeNumberMode = "source",
    ExistingVideoPolicy ExistingVideoPolicy = ExistingVideoPolicy.ReuseValid,
    string? MetadataDirectory = null,
    int ExpectedEpisodeTotal = 0);

public sealed record DramaDownloadResult(
    bool Ok,
    string OutputDir,
    int VideoCount,
    string? Message = null);
