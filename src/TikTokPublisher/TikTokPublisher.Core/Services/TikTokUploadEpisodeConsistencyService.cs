using System.Text.Json;
using System.Text.RegularExpressions;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public sealed record TikTokUploadEpisodeConsistencyResult(
    bool Ok,
    string Message,
    int ExpectedCount,
    int SourceVideoCount,
    IReadOnlyList<int> MissingEpisodes);

public static class TikTokUploadEpisodeConsistencyService
{
    private const string MetadataFileName = "shortdrama-project.json";
    private const string DramaInfoFileName = "短剧信息.txt";

    private static readonly Regex EpisodeNumberPattern =
        new(@"第\s*(\d+)\s*集", RegexOptions.Compiled);

    private static readonly string[] MetadataEpisodeCountKeys =
    [
        "effectiveEpisodeCount",
        "effective_episode_count",
        "downloadEpisodeLimit",
        "download_episode_limit",
        "episodeCount",
        "episode_count",
    ];

    private static readonly HashSet<string> DramaInfoEpisodeCountKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "集数",
        "总集数",
        "剧集数",
        "episodeCount",
        "episode_count",
    };

    public static TikTokUploadEpisodeConsistencyResult ValidateBeforeUpload(QueueProjectItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ProjectDir))
            return Pass();

        return ValidateProjectBeforeUpload(item.ProjectDir, item.EpisodeCount);
    }

    public static TikTokUploadEpisodeConsistencyResult ValidateBeforeUpload(PublishItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ProjectDir))
            return Pass();

        return ValidateProjectBeforeUpload(item.ProjectDir, item.EpisodeCount);
    }

    private static TikTokUploadEpisodeConsistencyResult ValidateProjectBeforeUpload(
        string projectDir,
        int itemEpisodeCount)
    {
        if (!Directory.Exists(projectDir))
            return Pass();

        ProjectWorkspaceContext context;
        try
        {
            context = ProjectWorkspaceService.LoadContext(projectDir);
        }
        catch
        {
            context = new ProjectWorkspaceContext(
                Path.GetFullPath(projectDir),
                Path.GetFullPath(projectDir),
                Path.GetFullPath(projectDir));
        }

        var uploadVideos = ProjectVideoResolver
            .ResolveUploadVideos(context.SourceProjectDir, allowStagedFallback: true)
            .ToList();

        var declaredCounts = ResolveDeclaredEpisodeCounts(context, itemEpisodeCount).ToList();
        var expectedCount = declaredCounts.Count > 0
            ? declaredCounts.Max()
            : uploadVideos.Count;

        if (expectedCount <= 0)
            return Pass();

        if (uploadVideos.Count == 0)
        {
            return Fail(
                $"上传前集数校验失败：短剧总集数 {expectedCount}，源目录和新剧名文件夹均未找到可上传视频文件。请先补齐视频后再执行上传。",
                expectedCount,
                uploadVideos.Count,
                Array.Empty<int>());
        }

        var indexedEpisodes = ExtractEpisodeIndexes(uploadVideos);
        var missingEpisodes = indexedEpisodes.Count > 0
            ? Enumerable.Range(1, expectedCount).Where(episode => !indexedEpisodes.Contains(episode)).ToList()
            : new List<int>();

        if (uploadVideos.Count != expectedCount || missingEpisodes.Count > 0)
        {
            var localDescription = indexedEpisodes.Count > 0
                ? $"可上传视频仅 {indexedEpisodes.Count} 个唯一视频集数（视频文件 {uploadVideos.Count} 个）"
                : $"可上传视频仅 {uploadVideos.Count} 个视频文件";
            var missingDescription = missingEpisodes.Count > 0
                ? $"，缺第 {FormatEpisodeList(missingEpisodes)} 集"
                : "";

            return Fail(
                $"上传前集数校验失败：短剧总集数 {expectedCount}，{localDescription}{missingDescription}。请先补齐视频后再执行上传。",
                expectedCount,
                uploadVideos.Count,
                missingEpisodes);
        }

        return Pass(expectedCount, uploadVideos.Count);
    }

    private static IEnumerable<int> ResolveDeclaredEpisodeCounts(
        ProjectWorkspaceContext context,
        int itemEpisodeCount)
    {
        if (itemEpisodeCount > 0)
            yield return itemEpisodeCount;

        foreach (var dir in new[] { context.SourceProjectDir, context.WorkflowProjectDir })
        {
            foreach (var count in ReadMetadataEpisodeCounts(Path.Combine(dir, MetadataFileName)))
                yield return count;
            foreach (var count in ReadDramaInfoEpisodeCounts(Path.Combine(dir, DramaInfoFileName)))
                yield return count;
        }
    }

    private static IEnumerable<int> ReadMetadataEpisodeCounts(string path)
    {
        if (!File.Exists(path))
            yield break;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch
        {
            yield break;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                yield break;

            foreach (var key in MetadataEpisodeCountKeys)
            {
                if (!document.RootElement.TryGetProperty(key, out var property))
                    continue;

                var count = property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number)
                    ? number
                    : ExtractPositiveInt(property.ToString());
                if (count > 0)
                    yield return count;
            }
        }
    }

    private static IEnumerable<int> ReadDramaInfoEpisodeCounts(string path)
    {
        if (!File.Exists(path))
            yield break;

        IEnumerable<string> lines;
        try
        {
            lines = File.ReadLines(path);
        }
        catch
        {
            yield break;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            var separator = ProjectInfoTextHelper.FindFieldSeparatorIndex(line);
            if (separator <= 0)
                continue;

            var key = line[..separator].Trim();
            if (!DramaInfoEpisodeCountKeys.Contains(key))
                continue;

            var count = ExtractPositiveInt(line[(separator + 1)..]);
            if (count > 0)
                yield return count;
        }
    }

    private static HashSet<int> ExtractEpisodeIndexes(IEnumerable<string> paths)
    {
        var indexes = new HashSet<int>();
        foreach (var path in paths)
        {
            var match = EpisodeNumberPattern.Match(Path.GetFileName(path));
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, out var episode) &&
                episode > 0)
            {
                indexes.Add(episode);
            }
        }

        return indexes;
    }

    private static int ExtractPositiveInt(string? text)
    {
        var match = Regex.Match(text ?? "", @"\d+");
        return match.Success && int.TryParse(match.Value, out var value) && value > 0 ? value : 0;
    }

    private static string FormatEpisodeList(IReadOnlyList<int> episodes)
    {
        var display = episodes.Take(20).Select(episode => episode.ToString());
        return string.Join("、", display) + (episodes.Count > 20 ? "…" : "");
    }

    private static TikTokUploadEpisodeConsistencyResult Pass(
        int expectedCount = 0,
        int sourceVideoCount = 0) =>
        new(true, "", expectedCount, sourceVideoCount, Array.Empty<int>());

    private static TikTokUploadEpisodeConsistencyResult Fail(
        string message,
        int expectedCount,
        int sourceVideoCount,
        IReadOnlyList<int> missingEpisodes) =>
        new(false, message, expectedCount, sourceVideoCount, missingEpisodes);
}
