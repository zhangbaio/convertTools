namespace ShortDrama.Core.Models;

public sealed record ArchivedProjectDeleteResult(
    bool Ok,
    string ArchiveProjectDir,
    string Message);
