using System.Text.Json;
using System.Text.Json.Nodes;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

/// <summary>工作目录项目扫描（对齐 Python <c>scan_workspace_projects</c> 子集）。</summary>
public static class WorkspaceProjectScanner
{
    private const int MaxProjectCacheSize = 4096;
    private static readonly object ProjectCacheLock = new();
    private static readonly Dictionary<string, CachedProjectScan> ProjectCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm",
    };

    private static readonly HashSet<string> ReservedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "archive", "config", "material-clip-output", "workflow", TikTokUploadStagingService.StagingDirName,
    };

    private const string ProjectMetadataFile = "shortdrama-project.json";
    private const string DramaInfoFile = "短剧信息.txt";

    public sealed class WorkspaceProject
    {
        public string ProjectDir { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string OriginalTitle { get; init; } = "";
        public string NewTitle { get; init; } = "";
        public string Description { get; init; } = "";
        public string GenreCategory { get; init; } = "";
        public int EpisodeCount { get; init; }
        /// <summary>视频方向：1=竖屏，0=横屏，-1=未知。</summary>
        public int VideoVertical { get; init; } = -1;
        public string? PrimaryVideoPath { get; init; }
        public string? CoverPath { get; init; }
    }

    public static IReadOnlyList<WorkspaceProject> Scan(string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root)) return Array.Empty<WorkspaceProject>();

        var results = new List<WorkspaceProject>();
        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var project = TryBuildProject(dir, requireProjectLike: true);
            if (project is not null)
                results.Add(project);
        }
        return results;
    }

    public static bool IsValidProjectDirectory(string projectDir)
    {
        if (!Directory.Exists(projectDir)) return false;
        var name = Path.GetFileName(projectDir);
        if (ReservedDirNames.Contains(name)) return false;
        return TryBuildProject(projectDir, requireProjectLike: true) is not null;
    }

    public static WorkspaceProject BuildProject(string projectDir) =>
        TryBuildProject(projectDir, requireProjectLike: false)
        ?? BuildProjectInternal(Path.GetFullPath(projectDir));

    private static WorkspaceProject? TryBuildProject(string projectDir, bool requireProjectLike)
    {
        var normalized = Path.GetFullPath(projectDir);
        if (!Directory.Exists(normalized)) return null;
        if (ReservedDirNames.Contains(Path.GetFileName(normalized))) return null;

        var fingerprint = BuildProjectFingerprint(normalized);
        lock (ProjectCacheLock)
        {
            if (ProjectCache.TryGetValue(normalized, out var cached) &&
                string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return cached.IsProject ? cached.Project : requireProjectLike ? null : cached.Project;
            }
        }

        List<string>? preloadedVideos = null;
        var isProject = LooksLikeProjectShallow(normalized);
        if (!isProject)
        {
            preloadedVideos = FindVideoFiles(normalized);
            isProject = preloadedVideos.Count > 0 && !LooksLikeNestedProjectContainer(normalized);
        }

        if (requireProjectLike && !isProject)
        {
            CacheProject(normalized, fingerprint, project: null, isProject: false);
            return null;
        }

        var project = BuildProjectInternal(normalized, preloadedVideos);
        CacheProject(normalized, fingerprint, project, isProject: true);
        return project;
    }

    private static bool LooksLikeProjectShallow(string projectDir)
    {
        if (File.Exists(Path.Combine(projectDir, ProjectMetadataFile))) return true;
        if (File.Exists(Path.Combine(projectDir, DramaInfoFile))) return true;
        return false;
    }

    private static bool LooksLikeNestedProjectContainer(string projectDir)
    {
        if (HasDirectProjectSignal(projectDir))
            return false;

        foreach (var child in Directory.EnumerateDirectories(projectDir))
        {
            var name = Path.GetFileName(child);
            if (string.IsNullOrWhiteSpace(name) ||
                name.StartsWith(".", StringComparison.Ordinal) ||
                ReservedDirNames.Contains(name) ||
                LooksLikeEpisodeFolderName(name))
            {
                continue;
            }

            if (HasDirectProjectSignal(child) || FindVideoFiles(child).Count > 0)
                return true;
        }

        return false;
    }

    private static bool HasDirectProjectSignal(string projectDir)
    {
        if (LooksLikeProjectShallow(projectDir))
            return true;

        if (CountTopLevelVideoFiles(projectDir) > 0)
            return true;

        return CountTopLevelVideoFiles(Path.Combine(projectDir, "videos")) > 0;
    }

    private static WorkspaceProject BuildProjectInternal(string projectDir, List<string>? preloadedVideos = null)
    {
        var metadata = ReadJsonObject(Path.Combine(projectDir, ProjectMetadataFile));
        var dramaInfo = ProjectInfoTextHelper.ParseInfoFile(Path.Combine(projectDir, DramaInfoFile));
        var resolvedWorkflowDir = ResolveWorkflowProjectDir(projectDir);
        var nestedWorkflowDir = Path.Combine(projectDir, "workflow");
        var workflowDir = Directory.Exists(resolvedWorkflowDir)
            ? resolvedWorkflowDir
            : Directory.Exists(nestedWorkflowDir)
                ? nestedWorkflowDir
                : projectDir;
        var workflowInfo = ProjectInfoTextHelper.ParseInfoFile(Path.Combine(workflowDir, DramaInfoFile));

        var videos = preloadedVideos ?? FindVideoFiles(projectDir);
        if (videos.Count == 0 && workflowDir != projectDir)
            videos = FindVideoFiles(workflowDir);

        var primaryVideo = videos.FirstOrDefault();
        var stem = primaryVideo is null ? "" : Path.Combine(Path.GetDirectoryName(primaryVideo) ?? projectDir, Path.GetFileNameWithoutExtension(primaryVideo));
        var cover = ResolveCover(stem, primaryVideo);

        var originalTitle = FirstNonEmpty(
            dramaInfo.GetValueOrDefault("原剧名"),
            dramaInfo.GetValueOrDefault("剧名"),
            dramaInfo.GetValueOrDefault("标题"),
            metadata?["originalTitle"]?.GetValue<string>(),
            metadata?["title"]?.GetValue<string>(),
            Path.GetFileName(projectDir)) ?? Path.GetFileName(projectDir);

        var newTitle = FirstNonEmpty(
            workflowInfo.GetValueOrDefault("新剧名"),
            workflowInfo.GetValueOrDefault("剧名"),
            dramaInfo.GetValueOrDefault("新剧名"),
            metadata?["newTitle"]?.GetValue<string>(),
            metadata?["new_title"]?.GetValue<string>(),
            ResolveWorkflowDisplayName(resolvedWorkflowDir),
            metadata?["displayName"]?.GetValue<string>()) ?? "";

        var description = FirstNonEmpty(
            workflowInfo.GetValueOrDefault("简介"),
            workflowInfo.GetValueOrDefault("剧情简介"),
            dramaInfo.GetValueOrDefault("简介"),
            metadata?["intro"]?.GetValue<string>(),
            metadata?["description"]?.GetValue<string>()) ?? "";

        var genre = FirstNonEmpty(
            workflowInfo.GetValueOrDefault("题材类型"),
            workflowInfo.GetValueOrDefault("TikTok题材类型"),
            dramaInfo.GetValueOrDefault("题材类型"),
            metadata?["category"]?.GetValue<string>()) ?? "";

        var episodeCount = videos.Count;
        if ((metadata?["effectiveEpisodeCount"] is JsonValue effective && effective.TryGetValue<int>(out var effectiveCount) && effectiveCount > 0) ||
            (metadata?["effective_episode_count"] is JsonValue effectiveSnake && effectiveSnake.TryGetValue<int>(out effectiveCount) && effectiveCount > 0) ||
            (metadata?["downloadEpisodeLimit"] is JsonValue limit && limit.TryGetValue<int>(out effectiveCount) && effectiveCount > 0) ||
            (metadata?["download_episode_limit"] is JsonValue limitSnake && limitSnake.TryGetValue<int>(out effectiveCount) && effectiveCount > 0))
        {
            episodeCount = effectiveCount;
        }
        else if (metadata?["episodeCount"] is JsonValue ev && ev.TryGetValue<int>(out var ec) && ec > 0)
        {
            episodeCount = Math.Max(episodeCount, ec);
        }

        return new WorkspaceProject
        {
            ProjectDir = projectDir,
            DisplayName = Path.GetFileName(projectDir),
            OriginalTitle = originalTitle,
            NewTitle = newTitle,
            Description = description,
            GenreCategory = genre,
            EpisodeCount = Math.Max(1, episodeCount),
            VideoVertical = ReadVideoVertical(metadata),
            PrimaryVideoPath = primaryVideo,
            CoverPath = cover,
        };
    }

    private static int ReadVideoVertical(JsonObject? metadata)
    {
        if (metadata is null) return -1;
        foreach (var key in new[] { "videoVertical", "video_vertical" })
        {
            if (metadata[key] is not JsonValue value) continue;
            if (value.TryGetValue<int>(out var number) && number is 0 or 1) return number;
            if (value.TryGetValue<string>(out var text) && int.TryParse(text, out number) && number is 0 or 1)
                return number;
        }
        return -1;
    }

    private static void CacheProject(
        string projectDir,
        string fingerprint,
        WorkspaceProject? project,
        bool isProject)
    {
        lock (ProjectCacheLock)
        {
            if (ProjectCache.Count > MaxProjectCacheSize)
                ProjectCache.Clear();

            ProjectCache[projectDir] = new CachedProjectScan(
                fingerprint,
                isProject,
                project ?? BuildEmptyProject(projectDir));
        }
    }

    private static WorkspaceProject BuildEmptyProject(string projectDir) => new()
    {
        ProjectDir = projectDir,
        DisplayName = Path.GetFileName(projectDir),
        OriginalTitle = Path.GetFileName(projectDir),
        EpisodeCount = 1,
    };

    private static string BuildProjectFingerprint(string projectDir)
    {
        var workflowDir = ResolveWorkflowProjectDir(projectDir);
        var nestedWorkflowDir = Path.Combine(projectDir, "workflow");
        var parts = new List<string>
        {
            DirectoryStamp(projectDir),
            DirectoryStamp(Path.Combine(projectDir, "videos")),
            FileStamp(Path.Combine(projectDir, ProjectMetadataFile)),
            FileStamp(Path.Combine(projectDir, DramaInfoFile)),
            workflowDir,
            DirectoryStamp(workflowDir),
            DirectoryStamp(Path.Combine(workflowDir, "videos")),
            DirectoryStamp(Path.Combine(workflowDir, TikTokUploadStagingService.StagingDirName)),
            FileStamp(Path.Combine(workflowDir, DramaInfoFile)),
            DirectoryStamp(nestedWorkflowDir),
            FileStamp(Path.Combine(nestedWorkflowDir, DramaInfoFile)),
        };

        return string.Join('|', parts);
    }

    private static string DirectoryStamp(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return "-";
        try
        {
            var info = new DirectoryInfo(path);
            return $"{info.FullName}:{info.LastWriteTimeUtc.Ticks}:{info.CreationTimeUtc.Ticks}";
        }
        catch
        {
            return path;
        }
    }

    private static string FileStamp(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "-";
        try
        {
            var info = new FileInfo(path);
            return $"{info.FullName}:{info.LastWriteTimeUtc.Ticks}:{info.Length}";
        }
        catch
        {
            return path;
        }
    }

    private static List<string> FindVideoFiles(string dir)
    {
        var results = new List<string>();
        if (!Directory.Exists(dir)) return results;
        foreach (var path in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
        {
            if (VideoExtensions.Contains(Path.GetExtension(path)))
                results.Add(path);
        }
        return results.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int CountTopLevelVideoFiles(string dir)
    {
        if (!Directory.Exists(dir))
            return 0;

        return Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
            .Count(path => VideoExtensions.Contains(Path.GetExtension(path)));
    }

    private static bool LooksLikeEpisodeFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return System.Text.RegularExpressions.Regex.IsMatch(
            name.Trim(),
            @"^(?:第?\s*\d+\s*(?:集|话|話|章|回)?|ep(?:isode)?\.?\s*\d+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static string? ResolveCover(string stem, string? primaryVideo)
    {
        if (string.IsNullOrWhiteSpace(stem) && primaryVideo is not null)
            stem = Path.Combine(Path.GetDirectoryName(primaryVideo) ?? "", Path.GetFileNameWithoutExtension(primaryVideo));
        foreach (var candidate in new[] { stem + ".cover.jpg", stem + ".cover.png", stem + ".jpg", stem + ".png" })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static JsonObject? ReadJsonObject(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch { return null; }
    }

    private static string ResolveWorkflowProjectDir(string projectDir)
    {
        try
        {
            return ProjectWorkspaceService.ResolveWorkflowProjectDir(projectDir);
        }
        catch
        {
            return "";
        }
    }

    private static string? ResolveWorkflowDisplayName(string workflowProjectDir)
    {
        if (string.IsNullOrWhiteSpace(workflowProjectDir)) return null;

        var name = Path.GetFileName(workflowProjectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name) ||
            string.Equals(name, "workflow", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return name.TrimStart('_').Trim();
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? "").Trim();
            if (!string.IsNullOrEmpty(text)) return text;
        }
        return null;
    }

    private sealed record CachedProjectScan(
        string Fingerprint,
        bool IsProject,
        WorkspaceProject Project);
}
