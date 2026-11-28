using System.Diagnostics;

namespace PlatformPublisher.Desktop.ViewModels;

public static class ArchivedProjectSearch
{
    public static bool Matches(string? query, params string?[] fields)
    {
        var text = (query ?? "").Trim();
        if (text.Length == 0)
            return true;
        foreach (var field in fields)
        {
            if (!string.IsNullOrEmpty(field) &&
                field.Contains(text, StringComparison.CurrentCultureIgnoreCase))
                return true;
        }
        return false;
    }
}

public sealed record ArchivedProjectRowViewModel(
    string ProjectKey,
    string DisplayName,
    string ArchiveProjectDirectory,
    string ArchivedSourceDirectory,
    string ArchivedWorkflowDirectory,
    DateTimeOffset? ArchivedAt,
    int DeletedVideoFileCount,
    int PreservedVideoFileCount,
    string OriginalTitle = "",
    string NewTitle = "",
    string AccountName = "")
{
    public string ArchivedAtText => ArchivedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
    public string VideoSummary => $"删除 {DeletedVideoFileCount} / 保留 {PreservedVideoFileCount}";

    public string OpenSourceDirectory() => OpenDirectory(ArchivedSourceDirectory, "原剧名");

    public string OpenWorkflowDirectory() => OpenDirectory(ArchivedWorkflowDirectory, "新剧名");

    private static string OpenDirectory(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return string.IsNullOrWhiteSpace(path) ? $"未找到{label}目录" : $"未找到{label}目录：{path}";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            return $"已打开{label}目录：{path}";
        }
        catch (Exception ex)
        {
            return $"打开{label}目录失败：{ex.Message}";
        }
    }
}
