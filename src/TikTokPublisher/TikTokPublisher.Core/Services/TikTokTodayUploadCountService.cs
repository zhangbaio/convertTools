using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class TikTokTodayUploadCountService
{
    public static int CountTodayUploads(
        IEnumerable<QueueProjectItem> queueItems,
        string? accountProfileId,
        string? workspaceRoot = null,
        DateTimeOffset? now = null,
        bool includeExecutionHistory = true)
    {
        var today = (now ?? DateTimeOffset.Now).ToLocalTime().Date;
        var accountId = (accountProfileId ?? "").Trim();
        var workspace = (workspaceRoot ?? "").Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in queueItems)
            AddQueueItemIfToday(seen, item, accountId, today);

        if (!string.IsNullOrWhiteSpace(workspace) && Directory.Exists(workspace))
        {
            foreach (var item in TikTokArchivedProjectService.List(workspace))
                AddArchiveItemIfToday(seen, item, accountId, today);
        }

        if (includeExecutionHistory)
        {
            foreach (var snapshot in TikTokExecutionHistoryService.LoadProjectSnapshots())
            {
                if (!WorkspaceMatches(workspace, snapshot.Workspace))
                    continue;
                AddQueueItemIfToday(seen, snapshot.Item, accountId, today);
            }
        }

        return seen.Count;
    }

    private static void AddQueueItemIfToday(
        HashSet<string> seen,
        QueueProjectItem item,
        string accountId,
        DateTime today)
    {
        if (!AccountMatches(accountId, item.AccountProfileId))
            return;
        if (!IsToday(item.UploadCompletedAt, today))
            return;

        seen.Add(BuildQueueItemKey(item));
    }

    private static void AddArchiveItemIfToday(
        HashSet<string> seen,
        ArchivedProjectItem item,
        string accountId,
        DateTime today)
    {
        if (!AccountMatches(accountId, item.AccountProfileId))
            return;
        if (!IsToday(item.UploadCompletedAt, today))
            return;

        seen.Add(BuildArchiveItemKey(item));
    }

    private static bool AccountMatches(string selectedAccountId, string itemAccountId) =>
        selectedAccountId.Length == 0 ||
        string.Equals((itemAccountId ?? "").Trim(), selectedAccountId, StringComparison.Ordinal);

    private static bool IsToday(string value, DateTime today) =>
        !string.IsNullOrWhiteSpace(value) &&
        DateTimeOffset.TryParse(value.Trim(), out var timestamp) &&
        timestamp.ToLocalTime().Date == today;

    private static bool WorkspaceMatches(string selectedWorkspace, string eventWorkspace)
    {
        if (string.IsNullOrWhiteSpace(selectedWorkspace))
            return true;
        if (string.IsNullOrWhiteSpace(eventWorkspace))
            return false;

        return string.Equals(PathKey(selectedWorkspace), PathKey(eventWorkspace), StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildQueueItemKey(QueueProjectItem item)
    {
        var account = NormalizeToken(FirstNonEmpty(item.AccountProfileId, item.AccountProfileName));
        var project = NormalizeToken(FirstNonEmpty(
            ProjectPathToken(item.ProjectDir),
            item.OriginalTitle,
            item.NewTitle,
            item.DisplayName));
        var original = NormalizeToken(item.OriginalTitle);
        var title = NormalizeToken(FirstNonEmpty(item.NewTitle, item.DisplayName));
        return $"{account}|{project}|{original}|{title}";
    }

    private static string BuildArchiveItemKey(ArchivedProjectItem item)
    {
        var account = NormalizeToken(FirstNonEmpty(item.AccountProfileId, item.AccountProfileName));
        var project = NormalizeToken(FirstNonEmpty(
            item.ProjectKey,
            ProjectPathToken(item.ArchivedSourceDir),
            ProjectPathToken(item.ArchivedWorkflowDir),
            item.OriginalTitle,
            item.NewTitle,
            item.DisplayName));
        var original = NormalizeToken(item.OriginalTitle);
        var title = NormalizeToken(FirstNonEmpty(item.NewTitle, item.DisplayName));
        return $"{account}|{project}|{original}|{title}";
    }

    private static string ProjectPathToken(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        try
        {
            var full = Path.GetFullPath(path.Trim());
            var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return name.TrimStart('_');
        }
        catch
        {
            return Path.GetFileName(path.Trim()).TrimStart('_');
        }
    }

    private static string PathKey(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
    }

    private static string NormalizeToken(string? value) =>
        (value ?? "").Trim().Replace('\\', '/').ToLowerInvariant();

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return "";
    }
}
