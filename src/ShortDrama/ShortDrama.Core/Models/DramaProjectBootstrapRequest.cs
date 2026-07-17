namespace ShortDrama.Core.Models;

public sealed record DramaProjectBootstrapRequest(
    string RootDir,
    DramaSearchItem Drama,
    string? CompanyName,
    string? Episodes = null,
    string Quality = "1080P",
    int Concurrent = 5,
    string EpisodeNumberMode = "source",
    string QueueEntryDramaType = "");

public sealed record DramaProjectBootstrapResult(
    string ProjectKey,
    string DisplayName,
    string SourceProjectDir,
    bool Created);
