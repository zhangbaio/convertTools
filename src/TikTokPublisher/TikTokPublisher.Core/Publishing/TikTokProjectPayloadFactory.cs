using System.Text.RegularExpressions;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Publishing;

/// <summary>对齐 Python <c>project_payload.build_tiktok_project_payload</c> 的字段解析。</summary>
public static class TikTokProjectPayloadFactory
{
    private static readonly string[] EpisodeCountKeys = ["集数", "总集数", "剧集数", "episodeCount"];
    private static readonly string[] TargetAudienceKeys = ["TikTok目标观众", "TikTok 目标观众", "目标观众", "目标受众"];
    private static readonly string[] GenreKeys = ["TikTok题材类型", "TikTok 题材类型", "题材类型", "题材"];

    public static TikTokProjectPayload BuildFromPublishItem(PublishItem item)
    {
        var sourceDir = "";
        var workflowDir = "";
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(item.ProjectDir))
        {
            var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
            sourceDir = context.SourceProjectDir;
            workflowDir = context.WorkflowProjectDir;
            merged = ProjectInfoTextHelper.MergeProjectInfo(
                Path.Combine(sourceDir, "短剧信息.txt"),
                Path.Combine(workflowDir, "短剧信息.txt"));
        }

        var title = FirstNonEmpty(
            merged.GetValueOrDefault("新剧名"),
            merged.GetValueOrDefault("剧名"),
            merged.GetValueOrDefault("标题"),
            item.Title,
            item.DramaName,
            string.IsNullOrWhiteSpace(workflowDir) ? "" : Path.GetFileName(workflowDir).TrimStart('_')) ?? "";

        var originalTitle = FirstNonEmpty(
            merged.GetValueOrDefault("原剧名"),
            merged.GetValueOrDefault("剧名"),
            item.OriginalTitle,
            title) ?? title;

        var description = FirstNonEmpty(
            merged.GetValueOrDefault("简介"),
            merged.GetValueOrDefault("描述"),
            merged.GetValueOrDefault("剧情简介"),
            item.Description) ?? "";

        var uploadPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.ProjectDir))
        {
            uploadPaths.AddRange(ProjectVideoResolver.ResolveUploadVideos(item.ProjectDir, allowStagedFallback: true));
        }
        if (uploadPaths.Count == 0 && !string.IsNullOrWhiteSpace(item.VideoPath) && File.Exists(item.VideoPath))
            uploadPaths.Add(item.VideoPath);

        var stagedUploadVideoCount = !string.IsNullOrWhiteSpace(item.ProjectDir)
            ? ProjectVideoResolver.ResolveStagedUploadVideos(item.ProjectDir).Count
            : 0;
        var episodeCount = ResolveEpisodeCount(
            merged,
            item,
            uploadPaths.Count,
            stagedUploadVideoCount);
        var targetAudience = ResolveTargetAudience(merged);
        var genres = ResolveGenres(merged, item.GenreCategory);

        return new TikTokProjectPayload
        {
            SourceProjectDir = sourceDir,
            WorkflowProjectDir = workflowDir,
            Title = title,
            OriginalTitle = originalTitle,
            Description = description,
            EpisodeCount = episodeCount,
            TargetAudience = targetAudience,
            Genres = genres,
        };
    }

    private static int ResolveEpisodeCount(
        IReadOnlyDictionary<string, string> merged,
        PublishItem item,
        int uploadVideoCount,
        int stagedUploadVideoCount)
    {
        if (!string.IsNullOrWhiteSpace(item.ProjectDir) &&
            ProjectWorkspaceService.TryResolveDownloadedEpisodeCount(item.ProjectDir, out var downloadedCount))
        {
            return downloadedCount;
        }

        // The staging directory is the finalized upload set. Once it exists, stale
        // source/workflow metadata must not inflate the TikTok total episode field.
        if (stagedUploadVideoCount > 0)
            return stagedUploadVideoCount;

        var counts = new List<int>();
        foreach (var key in EpisodeCountKeys)
        {
            if (merged.TryGetValue(key, out var raw))
            {
                var value = ExtractInt(raw);
                if (value > 0) counts.Add(value);
            }
        }

        if (item.EpisodeCount > 0) counts.Add(item.EpisodeCount);

        if (!string.IsNullOrWhiteSpace(item.ProjectDir))
        {
            var resolved = ProjectWorkspaceService.ResolveSourceEpisodeCount(item.ProjectDir);
            if (resolved > 0) counts.Add(resolved);
        }

        if (uploadVideoCount > 0) counts.Add(uploadVideoCount);
        return counts.Count > 0 ? counts.Max() : 1;
    }

    private static string ResolveTargetAudience(IReadOnlyDictionary<string, string> merged)
    {
        var text = FirstProjectInfoValue(merged, TargetAudienceKeys).ToLowerInvariant();
        if (text is "female" or "woman" or "women" or "girl" or "girls" or "f" or "女频" or "女" or "女性")
            return "female";
        if (text is "male" or "man" or "men" or "boy" or "boys" or "m" or "男频" or "男" or "男性")
            return "male";
        return "";
    }

    private static List<string> ResolveGenres(IReadOnlyDictionary<string, string> merged, string? fallbackCategory)
    {
        var rawValue = FirstProjectInfoValue(merged, GenreKeys);
        if (string.IsNullOrWhiteSpace(rawValue))
            rawValue = fallbackCategory ?? "";

        if (string.IsNullOrWhiteSpace(rawValue))
            return [];

        return ParseGenreTokens(rawValue);
    }

    public static List<string> ParseGenreTokens(string rawValue)
    {
        var text = rawValue ?? "";
        var matches = new List<(int Index, string Canonical)>();
        var occupied = new bool[text.Length];
        foreach (var alias in GenreAliasesByLength())
        {
            var index = 0;
            while (index < text.Length)
            {
                var found = text.IndexOf(alias.Alias, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                    break;

                var end = found + alias.Alias.Length;
                var overlaps = false;
                for (var i = found; i < end; i++)
                {
                    if (!occupied[i])
                        continue;
                    overlaps = true;
                    break;
                }

                if (!overlaps)
                {
                    for (var i = found; i < end; i++)
                        occupied[i] = true;
                    matches.Add((found, alias.Canonical));
                }

                index = Math.Max(found + 1, end);
            }
        }

        var selected = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matches.OrderBy(item => item.Index))
        {
            if (seen.Add(match.Canonical))
                selected.Add(match.Canonical);
        }

        return selected;
    }

    private static IEnumerable<(string Alias, string Canonical)> GenreAliasesByLength()
    {
        return TikTokPublishConstants.GenreCatalog
            .SelectMany(option => new[]
            {
                (Alias: option.Canonical, option.Canonical),
                (Alias: option.EnglishLabel, option.Canonical),
            })
            .GroupBy(item => item.Alias, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.Alias.Length)
            .ThenBy(item => item.Alias, StringComparer.Ordinal);
    }

    private static string FirstProjectInfoValue(IReadOnlyDictionary<string, string> merged, string[] keys)
    {
        foreach (var key in keys)
        {
            if (merged.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static int ExtractInt(string? value)
    {
        var match = Regex.Match(value ?? "", @"\d+");
        return match.Success && int.TryParse(match.Value, out var number) ? number : 0;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return null;
    }
}
