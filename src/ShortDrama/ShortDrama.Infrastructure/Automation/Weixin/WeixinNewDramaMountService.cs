using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation.Weixin.Pages;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.Automation.Weixin;

public sealed class WeixinNewDramaMountService
{
    private const string HistoryFileName = ".weixin-channel-new-drama-mount-history.jsonl";
    private const string SourceMode = "new_drama_mount";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private static readonly Regex TitleNoiseRegex = new(
        @"[\s\-_·•.,，。:：;；!！?？""'“”‘’（）()\[\]【】《》<>]+",
        RegexOptions.Compiled);

    private readonly IDramaSearchService _searchService;
    private readonly IDramaProjectBootstrapper _projectBootstrapper;
    private readonly IDramaDownloader _downloader;

    public WeixinNewDramaMountService(
        IDramaSearchService searchService,
        IDramaProjectBootstrapper projectBootstrapper,
        IDramaDownloader downloader)
    {
        _searchService = searchService;
        _projectBootstrapper = projectBootstrapper;
        _downloader = downloader;
    }

    public async Task<WeixinNewDramaMountResolution> EnsureAsync(
        string workflowProjectDir,
        WeixinAutomationConfig config,
        string? resolvedConfigPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var options = config.VideoPublish;
        if (!string.Equals(
                WeixinMaterialPublishPage.NormalizeVideoSourceMode(options.VideoSourceMode),
                SourceMode,
                StringComparison.Ordinal))
        {
            return new WeixinNewDramaMountResolution(workflowProjectDir, options, false);
        }

        var title = ResolveMountTitle(workflowProjectDir, options);
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("新剧挂载模式未填写新剧名称。");
        }

        var workspaceRoot = ResolveWorkspaceRoot(workflowProjectDir);
        var episodeSelection = ResolveEpisodeSelectionText(options);
        progress?.Report($"新剧挂载：工作区 {workspaceRoot}，目标新剧《{title}》，集数 {episodeSelection}。");

        var cachedProjectDir = ResolveValidCachedProjectDir(options, title);
        if (!string.IsNullOrWhiteSpace(cachedProjectDir))
        {
            progress?.Report($"新剧挂载：复用已下载目录 {cachedProjectDir}");
            var cachedOptions = options with
            {
                NewDramaMountProjectDir = cachedProjectDir,
                NewDramaMountResolvedTitle = FirstNonEmpty(options.NewDramaMountResolvedTitle, title),
                NewDramaMountTitle = title
            };
            PersistResolvedConfig(resolvedConfigPath, cachedOptions);
            return new WeixinNewDramaMountResolution(cachedProjectDir, cachedOptions, true);
        }

        var history = FindHistoryEntry(workspaceRoot, title);
        var historyProjectDir = ResolveHistoryProjectDir(history, title, options);
        if (!string.IsNullOrWhiteSpace(historyProjectDir))
        {
            progress?.Report($"新剧挂载：命中历史下载目录 {historyProjectDir}");
            var historyOptions = options with
            {
                NewDramaMountProjectDir = historyProjectDir,
                NewDramaMountResolvedTitle = FirstNonEmpty(GetHistoryString(history, "title"), title),
                NewDramaMountResolvedBookId = GetHistoryString(history, "book_id") ?? string.Empty,
                NewDramaMountTitle = title
            };
            PersistResolvedConfig(resolvedConfigPath, historyOptions);
            return new WeixinNewDramaMountResolution(historyProjectDir, historyOptions, true);
        }

        var drama = BuildSearchItemFromHistory(history)
                    ?? await SearchDramaAsync(title, progress, cancellationToken);

        progress?.Report($"新剧挂载：匹配到《{drama.Title}》 book_id={drama.BookId}，开始准备源项目。");
        var bootstrap = await _projectBootstrapper.BootstrapAsync(
            new DramaProjectBootstrapRequest(
                RootDir: workspaceRoot,
                Drama: drama,
                CompanyName: null,
                Episodes: episodeSelection,
                Quality: "1080P",
                Concurrent: 5,
                EpisodeNumberMode: "source"),
            cancellationToken);

        var sourceProjectDir = Path.GetFullPath(bootstrap.SourceProjectDir);
        var resolvedOptions = options with
        {
            NewDramaMountProjectDir = sourceProjectDir,
            NewDramaMountResolvedTitle = string.IsNullOrWhiteSpace(drama.Title) ? title : drama.Title.Trim(),
            NewDramaMountResolvedBookId = drama.BookId.Trim(),
            NewDramaMountTitle = title
        };

        if (!HasTargetVideos(sourceProjectDir, resolvedOptions))
        {
            progress?.Report($"新剧挂载：源目录未包含目标视频，开始下载到 {sourceProjectDir}");
            var result = await _downloader.DownloadAsync(
                new DramaDownloadRequest(
                    ProjectDir: sourceProjectDir,
                    OutputDir: sourceProjectDir,
                    DisplayName: drama.Title,
                    BookId: drama.BookId,
                    Episodes: episodeSelection,
                    Quality: "1080P",
                    Concurrent: 5,
                    EpisodeNumberMode: "source"),
                progress,
                cancellationToken);

            if (!result.Ok || !HasTargetVideos(sourceProjectDir, resolvedOptions))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.Message)
                        ? $"新剧挂载下载完成后仍未找到可发表视频：{sourceProjectDir}"
                        : $"新剧挂载下载失败：{result.Message}");
            }
        }
        else
        {
            progress?.Report("新剧挂载：源目录已有目标集视频，跳过重复下载。");
        }

        PersistResolvedConfig(resolvedConfigPath, resolvedOptions);
        AppendHistoryEntry(workspaceRoot, workflowProjectDir, sourceProjectDir, title, drama);
        progress?.Report($"新剧挂载：准备完成，素材来源 {sourceProjectDir}");
        return new WeixinNewDramaMountResolution(sourceProjectDir, resolvedOptions, true);
    }

    internal static string ResolveWorkspaceRoot(string projectDir)
    {
        var fullPath = Path.GetFullPath(projectDir);
        var directory = new DirectoryInfo(fullPath);
        if (directory.Parent is not null &&
            string.Equals(directory.Parent.Name, "workflow", StringComparison.OrdinalIgnoreCase) &&
            directory.Parent.Parent is not null)
        {
            return directory.Parent.Parent.FullName;
        }

        if (Directory.Exists(Path.Combine(fullPath, "workflow")))
        {
            return fullPath;
        }

        if (File.Exists(Path.Combine(fullPath, "shortdrama-project.json")) &&
            directory.Parent is not null &&
            Directory.Exists(Path.Combine(directory.Parent.FullName, "workflow")))
        {
            return directory.Parent.FullName;
        }

        return directory.Parent?.FullName ?? fullPath;
    }

    internal static string ResolveEpisodeSelectionText(WeixinVideoPublishOptions options)
    {
        if (string.Equals(options.EpisodeSelectionMode, "all", StringComparison.OrdinalIgnoreCase))
        {
            return "all";
        }

        if (string.Equals(options.EpisodeSelectionMode, "explicit", StringComparison.OrdinalIgnoreCase) &&
            options.EpisodeIndexes.Count > 0)
        {
            return string.Join(",", options.EpisodeIndexes.Where(index => index > 0).Distinct().OrderBy(index => index));
        }

        var start = Math.Max(1, options.StartEpisodeIndex);
        var count = Math.Max(1, options.PublishCount);
        return count == 1 ? start.ToString() : $"{start}-{start + count - 1}";
    }

    private async Task<DramaSearchItem> SearchDramaAsync(
        string title,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report($"新剧挂载：搜索新剧《{title}》...");
        var results = await _searchService.SearchAsync(title, 1, cancellationToken);
        var match = PickBestMatch(results, title);
        if (match is null)
        {
            throw new InvalidOperationException($"新剧挂载未搜索到《{title}》。");
        }

        return match;
    }

    private static DramaSearchItem? PickBestMatch(IReadOnlyList<DramaSearchItem> results, string title)
    {
        if (results.Count == 0)
        {
            return null;
        }

        var requestedKey = NormalizeTitleKey(title);
        return results
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
            .OrderByDescending(item => ScoreTitleMatch(requestedKey, NormalizeTitleKey(item.Title)))
            .ThenByDescending(item => item.FavoriteCount)
            .ThenByDescending(item => item.EpisodeTotal)
            .FirstOrDefault();
    }

    private static double ScoreTitleMatch(string requestedKey, string candidateKey)
    {
        if (string.IsNullOrWhiteSpace(requestedKey) || string.IsNullOrWhiteSpace(candidateKey))
        {
            return 0;
        }

        if (string.Equals(requestedKey, candidateKey, StringComparison.Ordinal))
        {
            return 100;
        }

        if (candidateKey.Contains(requestedKey, StringComparison.Ordinal) ||
            requestedKey.Contains(candidateKey, StringComparison.Ordinal))
        {
            return 80;
        }

        var overlap = requestedKey.Distinct().Count(candidateKey.Contains);
        return overlap * 1.0d / Math.Max(requestedKey.Length, candidateKey.Length);
    }

    private static string ResolveMountTitle(string workflowProjectDir, WeixinVideoPublishOptions options)
    {
        var metadata = ProjectAutomationMetadata.Resolve(workflowProjectDir);
        return FirstNonEmpty(
            options.NewDramaMountTitle,
            options.NewDramaMountResolvedTitle,
            metadata.NewTitle,
            metadata.Title,
            Path.GetFileName(workflowProjectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
    }

    private static string? ResolveValidCachedProjectDir(WeixinVideoPublishOptions options, string title)
    {
        if (string.IsNullOrWhiteSpace(options.NewDramaMountProjectDir) ||
            !Directory.Exists(options.NewDramaMountProjectDir))
        {
            return null;
        }

        var projectDir = Path.GetFullPath(options.NewDramaMountProjectDir);
        if (!CachedProjectMatchesTitle(projectDir, title, options))
        {
            return null;
        }

        return HasTargetVideos(projectDir, options with { NewDramaMountProjectDir = projectDir })
            ? projectDir
            : null;
    }

    private static string? ResolveHistoryProjectDir(
        JsonElement? history,
        string title,
        WeixinVideoPublishOptions options)
    {
        var path = GetHistoryString(history, "source_project_dir");
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return null;
        }

        var projectDir = Path.GetFullPath(path);
        if (!CachedProjectMatchesTitle(projectDir, title, options))
        {
            return null;
        }

        return HasTargetVideos(projectDir, options with { NewDramaMountProjectDir = projectDir })
            ? projectDir
            : null;
    }

    private static bool CachedProjectMatchesTitle(
        string projectDir,
        string title,
        WeixinVideoPublishOptions options)
    {
        var requestedKey = NormalizeTitleKey(FirstNonEmpty(title, options.NewDramaMountResolvedTitle));
        if (string.IsNullOrWhiteSpace(requestedKey))
        {
            return true;
        }

        foreach (var candidate in EnumerateCachedProjectTitleCandidates(projectDir, options))
        {
            var candidateKey = NormalizeTitleKey(candidate);
            if (string.IsNullOrWhiteSpace(candidateKey))
            {
                continue;
            }

            if (string.Equals(candidateKey, requestedKey, StringComparison.Ordinal) ||
                candidateKey.Contains(requestedKey, StringComparison.Ordinal) ||
                requestedKey.Contains(candidateKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateCachedProjectTitleCandidates(
        string projectDir,
        WeixinVideoPublishOptions options)
    {
        yield return options.NewDramaMountResolvedTitle;
        yield return Path.GetFileName(projectDir);

        var metadataPath = Path.Combine(projectDir, "shortdrama-project.json");
        if (!File.Exists(metadataPath))
        {
            yield break;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
        var root = document.RootElement;
        foreach (var key in new[] { "displayName", "title", "name", "newTitle", "new_title", "sourceName", "originalTitle" })
        {
            var text = GetString(root, key);
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return text;
            }
        }
    }

    private static bool HasTargetVideos(string projectDir, WeixinVideoPublishOptions options)
    {
        try
        {
            return WeixinMaterialPublishPage.ResolvePublishVideoItems(
                    projectDir,
                    options with { VideoSourceMode = SourceMode })
                .Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static JsonElement? FindHistoryEntry(string workspaceRoot, string title)
    {
        var historyPath = Path.Combine(workspaceRoot, HistoryFileName);
        if (!File.Exists(historyPath))
        {
            return null;
        }

        var requestedKey = NormalizeTitleKey(title);
        if (string.IsNullOrWhiteSpace(requestedKey))
        {
            return null;
        }

        try
        {
            var lines = File.ReadAllLines(historyPath, Encoding.UTF8);
            foreach (var line in lines.Reverse())
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var item = document.RootElement.Clone();
                if (!HistoryEntryMatches(item, requestedKey))
                {
                    continue;
                }

                return item;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool HistoryEntryMatches(JsonElement item, string requestedKey)
    {
        foreach (var key in new[] { "title", "requested_title", "project_key" })
        {
            var value = NormalizeTitleKey(GetString(item, key));
            if (!string.IsNullOrWhiteSpace(value) &&
                (string.Equals(value, requestedKey, StringComparison.Ordinal) ||
                 value.Contains(requestedKey, StringComparison.Ordinal) ||
                 requestedKey.Contains(value, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        if (item.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
        {
            foreach (var alias in aliases.EnumerateArray())
            {
                if (alias.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = NormalizeTitleKey(alias.GetString());
                if (!string.IsNullOrWhiteSpace(value) &&
                    (string.Equals(value, requestedKey, StringComparison.Ordinal) ||
                     value.Contains(requestedKey, StringComparison.Ordinal) ||
                     requestedKey.Contains(value, StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static DramaSearchItem? BuildSearchItemFromHistory(JsonElement? history)
    {
        var bookId = GetHistoryString(history, "book_id");
        if (string.IsNullOrWhiteSpace(bookId))
        {
            return null;
        }

        return new DramaSearchItem(
            BookId: bookId,
            Title: FirstNonEmpty(GetHistoryString(history, "title"), GetHistoryString(history, "requested_title"), bookId),
            Category: GetHistoryString(history, "category") ?? string.Empty,
            EpisodeTotal: GetHistoryInt(history, "episode_count") ?? 0,
            Intro: GetHistoryString(history, "intro") ?? string.Empty,
            PosterUrl: GetHistoryString(history, "poster_url") ?? string.Empty,
            FavoriteCount: GetHistoryInt(history, "favorite_count") ?? 0);
    }

    private static void AppendHistoryEntry(
        string workspaceRoot,
        string workflowProjectDir,
        string sourceProjectDir,
        string requestedTitle,
        DramaSearchItem drama)
    {
        try
        {
            var entry = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["version"] = 1,
                ["record_type"] = "new_drama_mount_history",
                ["title"] = drama.Title,
                ["requested_title"] = requestedTitle,
                ["title_key"] = NormalizeTitleKey(drama.Title),
                ["book_id"] = drama.BookId,
                ["source_project_dir"] = sourceProjectDir,
                ["workflow_project_dir"] = workflowProjectDir,
                ["intro"] = drama.Intro,
                ["category"] = drama.Category,
                ["poster_url"] = drama.PosterUrl,
                ["episode_count"] = drama.EpisodeTotal,
                ["favorite_count"] = drama.FavoriteCount,
                ["last_seen_at"] = DateTimeOffset.Now.ToString("O"),
                ["source"] = "csharp-material-publish"
            };

            var path = Path.Combine(workspaceRoot, HistoryFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? workspaceRoot);
            File.AppendAllText(
                path,
                JsonSerializer.Serialize(entry) + Environment.NewLine,
                Encoding.UTF8);
        }
        catch
        {
            // History is only an optimization; publishing should not fail because it cannot be written.
        }
    }

    private static void PersistResolvedConfig(string? configPath, WeixinVideoPublishOptions options)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(configPath, Encoding.UTF8)) as JsonObject ?? new JsonObject();
            var videoPublish = root["video_publish"] as JsonObject ?? new JsonObject();
            root["video_publish"] = videoPublish;
            videoPublish["publish_video_source_mode"] = SourceMode;
            videoPublish["video_source_mode"] = SourceMode;
            videoPublish["new_drama_mount_title"] = options.NewDramaMountTitle;
            videoPublish["new_drama_mount_project_dir"] = options.NewDramaMountProjectDir;
            videoPublish["new_drama_mount_resolved_title"] = options.NewDramaMountResolvedTitle;
            videoPublish["new_drama_mount_resolved_book_id"] = options.NewDramaMountResolvedBookId;
            File.WriteAllText(configPath, root.ToJsonString(JsonOptions), Encoding.UTF8);
        }
        catch
        {
            // A stale cache write should not block the upload run.
        }
    }

    private static string NormalizeTitleKey(string? value)
    {
        var text = (value ?? string.Empty).Trim().Trim('"').TrimStart('_').Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        return TitleNoiseRegex.Replace(text.ToLowerInvariant(), string.Empty);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string? GetHistoryString(JsonElement? element, string key)
    {
        return element.HasValue ? GetString(element.Value, key) : null;
    }

    private static int? GetHistoryInt(JsonElement? element, string key)
    {
        if (!element.HasValue || !element.Value.TryGetProperty(key, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            return numeric;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out numeric)
            ? numeric
            : null;
    }

    private static string? GetString(JsonElement element, string key)
    {
        return element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
    }
}

public sealed record WeixinNewDramaMountResolution(
    string SourceProjectDir,
    WeixinVideoPublishOptions Options,
    bool Resolved);
