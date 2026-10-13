using System.Text.Json;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public sealed record TikTokSeriesIdentity(string OriginalTitle, string EpisodeCount, string QueuedAt)
{
    public static TikTokSeriesIdentity Empty { get; } = new("", "", "");

    public bool HasOriginalTitle => !string.IsNullOrWhiteSpace(OriginalTitle);
}

/// <summary>
/// 用新剧名匹配原剧名、集数和加入时间。顺序是上传记录 Excel、归档目录、本地队列。
/// 本地队列是同步到管理系统的那份记录。
/// </summary>
public sealed class TikTokSeriesIdentityIndex
{
    public static TikTokSeriesIdentityIndex Empty { get; } = new(new Dictionary<string, TikTokSeriesIdentity>(StringComparer.Ordinal));

    private readonly Dictionary<string, TikTokSeriesIdentity> _byNewTitle;

    private TikTokSeriesIdentityIndex(Dictionary<string, TikTokSeriesIdentity> byNewTitle)
    {
        _byNewTitle = byNewTitle;
    }

    public TikTokSeriesIdentity Find(string? newTitle)
    {
        var key = TikTokOriginalTitleIndex.Normalize(newTitle);
        return key.Length == 0 ? TikTokSeriesIdentity.Empty : _byNewTitle.GetValueOrDefault(key) ?? TikTokSeriesIdentity.Empty;
    }

    public static TikTokSeriesIdentityIndex Load(TikTokAccountProfile account)
    {
        var merged = new Dictionary<string, TikTokSeriesIdentity>(StringComparer.Ordinal);
        Add(merged, ReadExcel(account), replaceExisting: false);
        var workspace = SafeWorkspace(account);
        Add(merged, ReadArchive(account, workspace), replaceExisting: false);
        Add(merged, ReadQueue(workspace), replaceExisting: false);
        return new TikTokSeriesIdentityIndex(merged);
    }

    public static TikTokSeriesIdentityIndex Merge(
        IEnumerable<(string NewTitle, TikTokSeriesIdentity Identity)> excel,
        IEnumerable<(string NewTitle, TikTokSeriesIdentity Identity)> archive,
        IEnumerable<(string NewTitle, TikTokSeriesIdentity Identity)> queue)
    {
        var merged = new Dictionary<string, TikTokSeriesIdentity>(StringComparer.Ordinal);
        Add(merged, excel, replaceExisting: false);
        Add(merged, archive, replaceExisting: false);
        Add(merged, queue, replaceExisting: false);
        return new TikTokSeriesIdentityIndex(merged);
    }

    private static void Add(
        Dictionary<string, TikTokSeriesIdentity> target,
        IEnumerable<(string NewTitle, TikTokSeriesIdentity Identity)> source,
        bool replaceExisting)
    {
        foreach (var (title, identity) in source)
        {
            if (!identity.HasOriginalTitle &&
                string.IsNullOrWhiteSpace(identity.EpisodeCount) &&
                string.IsNullOrWhiteSpace(identity.QueuedAt))
            {
                continue;
            }

            var key = TikTokOriginalTitleIndex.Normalize(title);
            if (key.Length == 0) continue;
            if (!replaceExisting && target.TryGetValue(key, out var existing))
            {
                target[key] = MergeIdentity(existing, identity);
                continue;
            }

            target[key] = identity;
        }
    }

    private static IEnumerable<(string NewTitle, TikTokSeriesIdentity Identity)> ReadExcel(TikTokAccountProfile account)
    {
        TikTokOriginalTitleIndex index;
        try
        {
            index = TikTokOriginalTitleIndex.Load(TikTokExcelExportService.ResolveReportPath(account));
        }
        catch (InvalidOperationException)
        {
            yield break;
        }

        foreach (var record in index.Records)
            yield return (record.NewTitle, new TikTokSeriesIdentity(record.OriginalTitle, record.EpisodeCount, record.QueuedAt));
    }

    private static IEnumerable<(string NewTitle, TikTokSeriesIdentity Identity)> ReadArchive(TikTokAccountProfile account, string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) yield break;
        IReadOnlyList<ArchivedProjectItem> items;
        try
        {
            items = TikTokArchivedProjectService.List(workspace, account.ResolveArchiveRootPath(workspace));
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var item in items)
        {
            var title = FirstNonEmpty(item.NewTitle, item.DisplayName);
            if (string.IsNullOrWhiteSpace(title)) continue;
            yield return (title, new TikTokSeriesIdentity(item.OriginalTitle, ReadArchiveEpisodeCount(item), item.QueuedAt));
        }
    }

    private static IEnumerable<(string NewTitle, TikTokSeriesIdentity Identity)> ReadQueue(string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) yield break;
        IReadOnlyList<QueueProjectItem> items;
        try
        {
            items = WorkspaceQueueDatabase.Load(workspace).Items;
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.NewTitle)) continue;
            var episodes = item.EpisodeCount > 0 ? item.EpisodeCount.ToString() : "";
            yield return (item.NewTitle, new TikTokSeriesIdentity(item.OriginalTitle, episodes, item.QueuedAt));
        }
    }

    private static TikTokSeriesIdentity MergeIdentity(TikTokSeriesIdentity current, TikTokSeriesIdentity incoming) =>
        new(
            FirstNonEmpty(current.OriginalTitle, incoming.OriginalTitle),
            PreferEpisodeCount(current.EpisodeCount, incoming.EpisodeCount),
            FirstNonEmpty(current.QueuedAt, incoming.QueuedAt));

    private static string PreferEpisodeCount(string current, string incoming)
    {
        var kept = ParsePositive(current);
        var candidate = ParsePositive(incoming);
        if (kept <= 0) return candidate > 0 ? candidate.ToString() : "";
        if (candidate <= 0) return kept.ToString();
        // 归档后视频会被删掉，只剩 1 个文件时记下来的集数不能覆盖原来的总集数。
        if (kept == 1 && candidate > 1) return candidate.ToString();
        return kept.ToString();
    }

    private static int ParsePositive(string? value)
    {
        var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var count) && count > 0 ? count : 0;
    }

    private static string ReadArchiveEpisodeCount(ArchivedProjectItem item)
    {
        var counts = new List<int>();
        foreach (var dir in new[] { item.ArchivedWorkflowDir, item.ArchivedSourceDir, item.ArchiveProjectDir })
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            AddInfoEpisodeCounts(Path.Combine(dir, "短剧信息.txt"), counts);
            AddJsonEpisodeCounts(Path.Combine(dir, "shortdrama-project.json"), counts,
                "originalEpisodeCount", "original_episode_count", "episodeCount", "episode_count",
                "effectiveEpisodeCount", "effective_episode_count", "total_episodes");
            AddJsonEpisodeCounts(Path.Combine(dir, ".weixin-channel-download-state.json"), counts, "video_count");
            AddJsonEpisodeCounts(Path.Combine(dir, "tiktok-upload-state.json"), counts,
                "originalEpisodeCount", "original_episode_count", "episode_count", "total_episodes");
        }

        if (!string.IsNullOrWhiteSpace(item.MetadataPath))
        {
            AddJsonEpisodeCounts(item.MetadataPath, counts,
                "episodeCount", "episode_count", "originalEpisodeCount", "original_episode_count");
        }

        return counts.Count == 0 ? "" : counts.Max().ToString();
    }

    private static void AddInfoEpisodeCounts(string path, List<int> counts)
    {
        Dictionary<string, string> info;
        try
        {
            info = ProjectInfoTextHelper.ParseInfoFile(path);
        }
        catch (IOException)
        {
            return;
        }

        foreach (var key in new[] { "集数", "总集数", "剧集数" })
        {
            if (info.TryGetValue(key, out var value))
            {
                var count = ParsePositive(value);
                if (count > 0) counts.Add(count);
            }
        }
    }

    private static void AddJsonEpisodeCounts(string path, List<int> counts, params string[] keys)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var key in keys)
            {
                if (!document.RootElement.TryGetProperty(key, out var property)) continue;
                var count = property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number)
                    ? number
                    : ParsePositive(property.ToString());
                if (count > 0) counts.Add(count);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 归档资料缺失时跳过，不改用磁盘上的视频数量。
        }
    }

    private static string SafeWorkspace(TikTokAccountProfile account)
    {
        try
        {
            return account.ResolveWorkspacePath() ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return "";
    }
}
