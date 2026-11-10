using System.Globalization;
using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Archive;

public sealed record AccountUploadedSeriesRow(
    string OriginalTitle,
    string NewTitle,
    int EpisodeCount,
    string Status,
    string CompletedAt,
    string ProjectDirectory)
{
    public string EpisodeText =>
        EpisodeCount > 0 ? EpisodeCount.ToString(CultureInfo.InvariantCulture) : "—";

    public bool CanOpenDirectory
    {
        get
        {
            try
            {
                return !string.IsNullOrWhiteSpace(ProjectDirectory) && Directory.Exists(ProjectDirectory);
            }
            catch
            {
                return false;
            }
        }
    }
}

/// <summary>
/// 当前账号已上传成功的剧。队列里还没归档的和归档索引各取一份，同一项目路径只保留归档记录。
/// </summary>
public static class AccountUploadedSeriesCatalog
{
    public const string SuccessStatus = "成功";

    public static IReadOnlyList<AccountUploadedSeriesRow> Load(TikTokAccountProfile account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var workspace = account.ResolveWorkspacePath();
        if (string.IsNullOrWhiteSpace(workspace))
            return Array.Empty<AccountUploadedSeriesRow>();

        IReadOnlyList<QueueProjectItem> queue;
        try
        {
            queue = WorkspaceQueueDatabase.Load(workspace).Items;
        }
        catch
        {
            queue = Array.Empty<QueueProjectItem>();
        }

        var archiveRoot = account.ResolveArchiveRootPath(workspace);
        var archived = TikTokArchivedProjectService.ListStored(
            workspace,
            string.IsNullOrWhiteSpace(archiveRoot) ? null : archiveRoot);
        return Compose(account.Id, queue, archived, TryReadEpisodeCount);
    }

    public static IReadOnlyList<AccountUploadedSeriesRow> Compose(
        string? accountProfileId,
        IEnumerable<QueueProjectItem>? queueItems,
        IEnumerable<ArchivedProjectItem>? archivedItems,
        Func<ArchivedProjectItem, int>? readEpisodeCount = null)
    {
        var accountId = (accountProfileId ?? "").Trim();
        if (accountId.Length == 0)
            return Array.Empty<AccountUploadedSeriesRow>();

        var queue = (queueItems ?? Array.Empty<QueueProjectItem>())
            .Where(item => item is not null && BelongsToAccount(item.AccountProfileId, accountId) && IsUploaded(item))
            .ToArray();
        var archived = (archivedItems ?? Array.Empty<ArchivedProjectItem>())
            .Where(item => item is not null && BelongsToAccount(item.AccountProfileId, accountId))
            .ToArray();

        var archivedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in archived)
        {
            AddPath(archivedPaths, item.SourceProjectDir);
            AddPath(archivedPaths, item.WorkflowProjectDir);
            AddPath(archivedPaths, item.ArchivedSourceDir);
            AddPath(archivedPaths, item.ArchivedWorkflowDir);
            AddPath(archivedPaths, item.MetadataPath);
        }

        var rows = new List<AccountUploadedSeriesRow>(archived.Length + queue.Length);
        foreach (var item in archived)
        {
            var episodes = readEpisodeCount?.Invoke(item) ?? 0;
            if (episodes <= 0)
                episodes = MatchingQueueEpisodeCount(queue, item);
            rows.Add(new AccountUploadedSeriesRow(
                OriginalTitle: DisplayTitle(item.OriginalTitle, item.DisplayName),
                NewTitle: DisplayTitle(item.NewTitle, item.DisplayName),
                EpisodeCount: Math.Max(0, episodes),
                Status: SuccessStatus,
                CompletedAt: FormatTime(item.ArchivedAt),
                ProjectDirectory: ChooseDirectory(
                    item.ArchivedWorkflowDir,
                    item.ArchivedSourceDir,
                    item.WorkflowProjectDir,
                    item.SourceProjectDir)));
        }

        foreach (var item in queue)
        {
            if (archivedPaths.Contains(NormalizePath(item.ProjectDir)))
                continue;
            rows.Add(new AccountUploadedSeriesRow(
                OriginalTitle: DisplayTitle(item.OriginalTitle, item.DisplayName),
                NewTitle: DisplayTitle(item.NewTitle, item.DisplayName),
                EpisodeCount: Math.Max(0, item.EpisodeCount),
                Status: SuccessStatus,
                CompletedAt: FormatTime(FirstNonEmpty(item.UploadCompletedAt, item.QueuedAt)),
                ProjectDirectory: ChooseDirectory(item.ProjectDir)));
        }

        return rows
            .OrderByDescending(row => ParseTime(row.CompletedAt))
            .ThenBy(row => row.NewTitle, StringComparer.Ordinal)
            .ToArray();
    }

    private static int MatchingQueueEpisodeCount(IReadOnlyList<QueueProjectItem> queue, ArchivedProjectItem archived)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddPath(paths, archived.SourceProjectDir);
        AddPath(paths, archived.WorkflowProjectDir);
        AddPath(paths, archived.ArchivedSourceDir);
        AddPath(paths, archived.ArchivedWorkflowDir);
        foreach (var item in queue)
        {
            if (item.EpisodeCount <= 0)
                continue;
            if (paths.Contains(NormalizePath(item.ProjectDir)))
                return item.EpisodeCount;
        }

        return 0;
    }

    internal static int TryReadEpisodeCount(ArchivedProjectItem item)
    {
        var roots = new[] { item.ArchivedWorkflowDir, item.ArchivedSourceDir, item.WorkflowProjectDir, item.SourceProjectDir }
            .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            .ToArray();
        foreach (var root in roots)
        {
            Dictionary<string, JsonElement> state;
            try
            {
                state = TikTokUploadStateStore.LoadState(root);
            }
            catch
            {
                continue;
            }

            var count = EpisodeCountFrom(state, "episode_count", "total_episodes");
            if (count > 0)
                return count;
        }

        // 上传状态文件通常不记集数。归档元数据保存了归档时队列里的集数，项目 json 保存了导入时的集数。
        var archived = EpisodeCountFromJsonFile(item.MetadataPath, "episode_count", "episodeCount", "total_episodes");
        if (archived > 0)
            return archived;

        foreach (var root in roots)
        {
            var count = EpisodeCountFromJsonFile(
                Path.Combine(root, "shortdrama-project.json"),
                "effectiveEpisodeCount", "episodeCount", "episode_count", "declaredEpisodeCount");
            if (count > 0)
                return count;
        }

        return 0;
    }

    private static int EpisodeCountFromJsonFile(string? path, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return 0;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return 0;
            var values = document.RootElement.EnumerateObject()
                .GroupBy(property => property.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Value.Clone(), StringComparer.Ordinal);
            return EpisodeCountFrom(values, keys);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return 0;
        }
    }

    private static int EpisodeCountFrom(IReadOnlyDictionary<string, JsonElement> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!values.TryGetValue(key, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0)
                return number;
            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed > 0)
                return parsed;
        }

        if (values.TryGetValue("episodes", out var episodes) && episodes.ValueKind == JsonValueKind.Array)
            return episodes.GetArrayLength();
        return 0;
    }

    private static bool IsUploaded(QueueProjectItem item)
    {
        item.NormalizeStepStates();
        return item.IsUploadCompleted || !string.IsNullOrWhiteSpace(item.UploadCompletedAt);
    }

    private static bool BelongsToAccount(string? storedId, string accountId) =>
        string.Equals((storedId ?? "").Trim(), accountId, StringComparison.OrdinalIgnoreCase);

    private static string DisplayTitle(string? preferred, string? fallback)
    {
        var text = (preferred ?? "").Trim();
        return text.Length > 0 ? text : (fallback ?? "").Trim();
    }

    private static string ChooseDirectory(params string?[] paths)
    {
        foreach (var path in paths)
        {
            var full = NormalizePath(path);
            if (full.Length == 0)
                continue;
            try
            {
                if (Directory.Exists(full))
                    return full;
            }
            catch
            {
                // Keep looking for a directory that can be opened.
            }
        }

        foreach (var path in paths)
        {
            var full = NormalizePath(path);
            if (full.Length > 0)
                return full;
        }

        return "";
    }

    private static void AddPath(HashSet<string> paths, string? path)
    {
        var full = NormalizePath(path);
        if (full.Length > 0)
            paths.Add(full);
    }

    private static string NormalizePath(string? path)
    {
        var text = (path ?? "").Trim();
        if (text.Length == 0)
            return "";
        try
        {
            return Path.GetFullPath(text);
        }
        catch
        {
            return text.Replace('\\', '/');
        }
    }

    private static string FormatTime(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
            return "";
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ||
            DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsed))
        {
            return parsed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        return text;
    }

    private static DateTime ParseTime(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTime.MinValue;

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? "").Trim();
            if (text.Length > 0)
                return text;
        }

        return "";
    }
}
