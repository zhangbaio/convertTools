using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlatformPublisher.Kuaishou.Publishing;

public enum KuaishouVideoRepairRoute
{
    UploadLocal,
    RestoreAndDownload,
    Skip,
}

public enum KuaishouVideoRepairOutcome
{
    Skipped,
    Uploaded,
    Failed,
}

public sealed record KuaishouSeriesFailure(string SeriesId, string Title, IReadOnlyList<int> FailedEpisodes);

public sealed record KuaishouVideoRepairNote(string Title, KuaishouVideoRepairOutcome Outcome, string Message);

public readonly record struct KuaishouVideoRepairFiles(
    IReadOnlyList<string> VideoPaths,
    string? SkipReason,
    IReadOnlyList<int>? UploadEpisodes = null)
{
    public IDisposable? Lease { get; init; }

    public bool ShouldSkip => !string.IsNullOrWhiteSpace(SkipReason);

    public static KuaishouVideoRepairFiles Skip(string reason) => new([], reason);

    public static KuaishouVideoRepairFiles Ready(IReadOnlyList<string> paths, IReadOnlyList<int>? uploadEpisodes = null) =>
        new(paths, null, uploadEpisodes);
}

public static partial class KuaishouFailedEpisodeRepair
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm",
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "material-videos", "material-clip-output", "素材剪辑输出", "videos-智能重剪前", "archive",
        KuaishouSettlementEpisodeVideoService.DedupDirectoryName, KuaishouSettlementEpisodeVideoService.DedupOutputDirectoryName,
    };

    private static readonly Regex UploadReasonPattern = new(
        @"第\s*([0-9]+(?:\s*[,，、]\s*[0-9]+)*)\s*集",
        RegexOptions.Compiled);

    public static IReadOnlyList<string> ParseTitles(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var titles = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var title = line.Trim().Trim('《', '》', '"', '“', '”');
            if (title.Length == 0 || !seen.Add(title))
                continue;
            titles.Add(title);
        }

        return titles;
    }

    public static KuaishouVideoRepairRoute Decide(bool listed, bool hasVideos, bool archived)
    {
        if (listed && hasVideos)
            return KuaishouVideoRepairRoute.UploadLocal;
        if (archived)
            return KuaishouVideoRepairRoute.RestoreAndDownload;
        return KuaishouVideoRepairRoute.Skip;
    }

    public static KuaishouSeriesFailure? ParseSeriesLookup(string? json, string expectedTitle)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
            return ParseSeriesLookup(document.RootElement, expectedTitle);
    }

    private static KuaishouSeriesFailure? ParseSeriesLookup(JsonElement root, string expectedTitle)
    {
        if (root.ValueKind != JsonValueKind.Object || !ReadBool(root, "found"))
            return null;
        var seriesId = ReadString(root, "seriesId");
        var title = ReadString(root, "title");
        if (seriesId.Length == 0 ||
            !string.Equals(title, expectedTitle.Trim(), StringComparison.Ordinal))
            return null;

        var episodes = ReadEpisodes(root);
        var failed = episodes.Count > 0
            ? episodes.Where(item => item.VideoStatus == 2 && item.OrderNo >= 0)
                .Select(item => item.OrderNo + 1)
                .Distinct()
                .Order()
                .ToArray()
            : ParseUploadReason(ReadString(root, "uploadReason"));
        return new KuaishouSeriesFailure(seriesId, title, failed);
    }

    public static IReadOnlyList<int> ParseUploadReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return [];
        var numbers = new List<int>();
        foreach (Match match in UploadReasonPattern.Matches(reason))
        {
            foreach (var part in match.Groups[1].Value.Split([',', '，', '、', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part, out var number) && number > 0)
                    numbers.Add(number);
            }
        }

        return numbers.Distinct().Order().ToArray();
    }

    public static IReadOnlyList<int> MissingEpisodes(IEnumerable<string> paths, IReadOnlyList<int> episodes) =>
        episodes.Where(episode => KuaishouPersonalEpisodeUploadService.MatchEpisodeFile(paths, episode) is null)
            .Distinct()
            .Order()
            .ToArray();

    public static IReadOnlyList<int> EpisodesToRedownload(
        IEnumerable<string> paths,
        IReadOnlyList<int> episodes,
        Func<string, bool> isSupported)
    {
        var files = paths as IReadOnlyList<string> ?? paths.ToArray();
        return episodes.Where(episode =>
            {
                var path = KuaishouPersonalEpisodeUploadService.MatchEpisodeFile(files, episode);
                return path is null || !isSupported(path);
            })
            .Distinct()
            .Order()
            .ToArray();
    }

    public static string FormatEpisodes(IReadOnlyList<int> episodes) =>
        string.Join("、", episodes.Select(episode => $"第{episode}集"));

    public static bool ContainsPlayableVideo(params string?[] directories)
    {
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;
            if (FindVideo(directory))
                return true;
        }

        return false;
    }

    private static bool FindVideo(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.'))
                    continue;
                if (Directory.Exists(entry))
                {
                    if (!ExcludedDirectories.Contains(name))
                        pending.Push(entry);
                    continue;
                }

                if (!VideoExtensions.Contains(Path.GetExtension(entry)))
                    continue;
                try
                {
                    if (new FileInfo(entry).Length > 0)
                        return true;
                }
                catch (IOException)
                {
                }
            }
        }

        return false;
    }

    private static List<(int OrderNo, int VideoStatus)> ReadEpisodes(JsonElement root)
    {
        var episodes = new List<(int, int)>();
        if (!root.TryGetProperty("episodes", out var items) || items.ValueKind != JsonValueKind.Array)
            return episodes;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("orderNo", out var orderNode) || !orderNode.TryGetInt32(out var orderNo))
                continue;
            var status = item.TryGetProperty("videoStatus", out var statusNode) && statusNode.TryGetInt32(out var parsed)
                ? parsed
                : 0;
            episodes.Add((orderNo, status));
        }

        return episodes;
    }

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True ||
         (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed));

    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : value.ToString().Trim();
    }
}
