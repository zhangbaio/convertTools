namespace ShortDrama.Desktop.Models;

public sealed partial record class ArchivedProjectItem(
    string ProjectKey,
    string DisplayName,
    string ArchiveProjectDir,
    string? ArchivedSourceDir,
    string? ArchivedWorkflowDir,
    string? ArchivedBackupDir,
    int DeletedVideoFileCount,
    DateTimeOffset? ArchivedAt)
{
    public bool IsChecked { get; set; }

    public string ArchivedAtText => ArchivedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
}
