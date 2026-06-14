namespace ShortDrama.Core.Models;

public sealed record DramaProjectBootstrapRequest(
    string RootDir,
    DramaSearchItem Drama,
    string? CompanyName,
    string? Episodes = null);

public sealed record DramaProjectBootstrapResult(
    string ProjectKey,
    string DisplayName,
    string SourceProjectDir,
    bool Created);
