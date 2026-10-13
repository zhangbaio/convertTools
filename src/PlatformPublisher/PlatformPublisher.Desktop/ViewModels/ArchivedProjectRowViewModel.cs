namespace PlatformPublisher.Desktop.ViewModels;

public sealed record ArchivedProjectRowViewModel(
    string ProjectKey,
    string DisplayName,
    string ArchiveProjectDirectory,
    string ArchivedSourceDirectory,
    string ArchivedWorkflowDirectory,
    DateTimeOffset? ArchivedAt,
    int DeletedVideoFileCount,
    int PreservedVideoFileCount)
{
    public string ArchivedAtText => ArchivedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
    public string VideoSummary => $"删除 {DeletedVideoFileCount} / 保留 {PreservedVideoFileCount}";
}
