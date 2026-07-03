using System.Text.Json;
using System.Text.Json.Nodes;

namespace TikTokPublisher.Core.Services;

/// <summary>工作目录项目扫描（对齐 Python <c>scan_workspace_projects</c> 子集）。</summary>
public static class WorkspaceProjectScanner
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm",
    };

    private static readonly HashSet<string> ReservedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "archive", "config", "material-clip-output", "workflow",
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
            if (!IsValidProjectDirectory(dir)) continue;
            results.Add(BuildProject(dir));
        }
        return results;
    }

    public static bool IsValidProjectDirectory(string projectDir)
    {
        if (!Directory.Exists(projectDir)) return false;
        var name = Path.GetFileName(projectDir);
        if (ReservedDirNames.Contains(name)) return false;
        return LooksLikeProject(projectDir);
    }

    public static WorkspaceProject BuildProject(string projectDir) => BuildProjectInternal(projectDir);

    private static bool LooksLikeProject(string projectDir)
    {
        if (File.Exists(Path.Combine(projectDir, ProjectMetadataFile))) return true;
        if (File.Exists(Path.Combine(projectDir, DramaInfoFile))) return true;
        return FindVideoFiles(projectDir).Count > 0;
    }

    private static WorkspaceProject BuildProjectInternal(string projectDir)
    {
        var metadata = ReadJsonObject(Path.Combine(projectDir, ProjectMetadataFile));
        var dramaInfo = ParseInfoFile(Path.Combine(projectDir, DramaInfoFile));
        var workflowDir = Directory.Exists(Path.Combine(projectDir, "workflow"))
            ? Path.Combine(projectDir, "workflow")
            : projectDir;
        var workflowInfo = ParseInfoFile(Path.Combine(workflowDir, DramaInfoFile));

        var videos = FindVideoFiles(projectDir);
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
        if (metadata?["episodeCount"] is JsonValue ev && ev.TryGetValue<int>(out var ec) && ec > 0)
            episodeCount = Math.Max(episodeCount, ec);

        return new WorkspaceProject
        {
            ProjectDir = projectDir,
            DisplayName = Path.GetFileName(projectDir),
            OriginalTitle = originalTitle,
            NewTitle = newTitle,
            Description = description,
            GenreCategory = genre,
            EpisodeCount = Math.Max(1, episodeCount),
            PrimaryVideoPath = primaryVideo,
            CoverPath = cover,
        };
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

    private static Dictionary<string, string> ParseInfoFile(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || !line.Contains(':') && !line.Contains('：')) continue;
            var sep = line.Contains('：') ? '：' : ':';
            var parts = line.Split(sep, 2);
            if (parts.Length != 2) continue;
            var key = parts[0].Trim();
            var value = parts[1].Trim();
            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                result[key] = value;
        }
        return result;
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
}
