namespace ShortDrama.Core.Models;

public sealed record ProjectArchiveResult(
    bool Ok,
    string ProjectKey,
    string ArchiveProjectDir,
    string? ArchivedSourceDir,
    string? ArchivedWorkflowDir,
    string? ArchivedBackupDir,
    int DeletedVideoFileCount,
    int PreservedVideoFileCount,
    string Message);
