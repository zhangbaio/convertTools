using System.Collections.Concurrent;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public sealed record TikTokUploadedDrama(string AccountProfileId, string SeriesId, string Title, string CompletedAt);

public static class TikTokTodayUploadCountService
{
    public static string NormalizeReportedSeriesId(string? bookId)
    {
        var text = (bookId ?? "").Trim();
        var colon = text.LastIndexOf(':');
        if (colon >= 0 && colon < text.Length - 1)
            text = text[(colon + 1)..].Trim();
        return text;
    }

    public static IReadOnlyList<TikTokUploadedDrama> ListRecentUploads(
        IEnumerable<QueueProjectItem> queueItems,
        string? workspaceRoot = null,
        string? archiveRootDir = null,
        DateTimeOffset? now = null) =>
        ListRecentUploads(
            queueItems,
            workspaceRoot,
            archiveRootDir,
            TikTokExecutionHistoryService.ReadUploadCompletionStamps(),
            now);

    internal static IReadOnlyList<TikTokUploadedDrama> ListRecentUploads(
        IEnumerable<QueueProjectItem> queueItems,
        string? workspaceRoot,
        string? archiveRootDir,
        IReadOnlyList<UploadCompletionStamp> completionStamps,
        DateTimeOffset? now = null)
    {
        var since = (now ?? DateTimeOffset.Now).ToLocalTime().Date.AddDays(-1);
        var workspace = (workspaceRoot ?? "").Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uploads = new List<TikTokUploadedDrama>();

        void Add(string accountId, string projectDir, string originalTitle, string newTitle, string displayName, string completedAt)
        {
            if (!IsOnOrAfter(completedAt, since))
                return;
            var account = (accountId ?? "").Trim();
            var title = FirstNonEmpty(originalTitle, newTitle, displayName);
            var seriesId = NormalizeReportedSeriesId(ReadProjectBookId(projectDir));
            var key = $"{account}|{NormalizeToken(FirstNonEmpty(ProjectPathToken(projectDir), originalTitle, newTitle, displayName))}|{NormalizeToken(originalTitle)}|{NormalizeToken(FirstNonEmpty(newTitle, displayName))}";
            if (!seen.Add(key))
                return;
            uploads.Add(new TikTokUploadedDrama(account, seriesId, title, completedAt.Trim()));
        }

        foreach (var item in queueItems)
        {
            if (string.IsNullOrWhiteSpace(item.UploadCompletedAt))
                continue;
            Add(item.AccountProfileId, item.ProjectDir, item.OriginalTitle, item.NewTitle, item.DisplayName, item.UploadCompletedAt);
        }

        if (!string.IsNullOrWhiteSpace(workspace) && Directory.Exists(workspace))
        {
            foreach (var item in TikTokArchivedProjectService.ListStored(workspace, archiveRootDir))
            {
                var completedAt = FirstNonEmpty(item.UploadCompletedAt, item.ArchivedAt);
                Add(item.AccountProfileId, FirstNonEmpty(item.SourceProjectDir, item.ArchivedSourceDir, item.ArchiveProjectDir), item.OriginalTitle, item.NewTitle, item.DisplayName, completedAt);
            }
        }

        foreach (var stamp in completionStamps)
        {
            if (!WorkspaceMatches(workspace, stamp.Workspace))
                continue;
            Add(stamp.AccountProfileId, stamp.ProjectDir, stamp.OriginalTitle, stamp.NewTitle, stamp.DisplayName, stamp.UploadCompletedAt);
        }

        return uploads.Count > 300 ? uploads.Skip(uploads.Count - 300).ToArray() : uploads;
    }

    private static bool IsOnOrAfter(string value, DateTime since)
    {
        if (string.IsNullOrWhiteSpace(value) || !DateTimeOffset.TryParse(value.Trim(), out var timestamp))
            return false;
        return timestamp.ToLocalTime().DateTime >= since;
    }

    private static readonly ConcurrentDictionary<string, (DateTime WriteTimeUtc, string SeriesId)> ProjectBookIdCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static string ReadProjectBookId(string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
            return "";
        var path = Path.Combine(projectDir, "shortdrama-project.json");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return "";
            var writeTime = info.LastWriteTimeUtc;
            if (ProjectBookIdCache.TryGetValue(path, out var cached) && cached.WriteTimeUtc == writeTime)
                return cached.SeriesId;

            var seriesId = ReadBookIdFromProjectFile(path);
            ProjectBookIdCache[path] = (writeTime, seriesId);
            return seriesId;
        }
        catch
        {
            return "";
        }
    }

    private static string ReadBookIdFromProjectFile(string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            return "";
        foreach (var name in new[] { "bookId", "book_id" })
        {
            if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                return value.GetString() ?? "";
        }

        return "";
    }

    public static int CountTodayUploads(
        IEnumerable<QueueProjectItem> queueItems,
        string? accountProfileId,
        string? workspaceRoot = null,
        DateTimeOffset? now = null,
        bool includeExecutionHistory = true,
        string? archiveRootDir = null)
    {
        var today = (now ?? DateTimeOffset.Now).ToLocalTime().Date;
        var accountId = (accountProfileId ?? "").Trim();
        var workspace = (workspaceRoot ?? "").Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in queueItems)
            AddQueueItemIfToday(seen, item, accountId, today);

        if (!string.IsNullOrWhiteSpace(workspace) && Directory.Exists(workspace))
        {
            foreach (var item in TikTokArchivedProjectService.ListStored(workspace, archiveRootDir))
                AddArchiveItemIfToday(seen, item, accountId, today);
        }

        if (includeExecutionHistory)
        {
            foreach (var stamp in TikTokExecutionHistoryService.ReadUploadCompletionStamps(accountProfileId: accountId))
            {
                if (!WorkspaceMatches(workspace, stamp.Workspace))
                    continue;
                AddQueueItemIfToday(seen, new QueueProjectItem
                {
                    AccountProfileId = stamp.AccountProfileId,
                    AccountProfileName = stamp.AccountProfileName,
                    ProjectDir = stamp.ProjectDir,
                    OriginalTitle = stamp.OriginalTitle,
                    NewTitle = stamp.NewTitle,
                    DisplayName = stamp.DisplayName,
                    UploadCompletedAt = stamp.UploadCompletedAt,
                }, accountId, today);
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
