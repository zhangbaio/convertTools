using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed record KuaishouLocalDramaCandidate(
    string ProjectDirectory,
    string DisplayName,
    int EpisodeCount,
    bool HasPoster,
    bool MetadataExists);

public sealed record KuaishouLocalDramaBootstrapResult(string ProjectDirectory, string DisplayName);

public static class KuaishouLocalDramaImportService
{
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp"];
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "workflow", "archive", "config",
    };

    public static IReadOnlyList<KuaishouLocalDramaCandidate> ListCandidates(string workspaceDirectory)
    {
        if (!Directory.Exists(workspaceDirectory))
            return [];
        var candidates = new List<KuaishouLocalDramaCandidate>();
        foreach (var child in Directory.EnumerateDirectories(workspaceDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || IgnoredDirectories.Contains(name))
                continue;
            var episodeCount = CountVideos(child);
            if (episodeCount <= 0)
                continue;
            var metadata = ReadObject(Path.Combine(child, KuaishouWorkspaceProjects.ProjectFileName));
            candidates.Add(new KuaishouLocalDramaCandidate(
                Path.GetFullPath(child),
                ResolveTitle(child, metadata, ReadInfo(Path.Combine(child, "短剧信息.txt"))),
                episodeCount,
                FindPoster(child) is not null,
                metadata is not null));
        }

        return candidates;
    }

    public static KuaishouLocalDramaBootstrapResult Bootstrap(string projectDirectory)
    {
        if (!Directory.Exists(projectDirectory))
            throw new DirectoryNotFoundException($"本地短剧目录不存在: {projectDirectory}");
        var projectDir = Path.GetFullPath(projectDirectory);
        var metadataPath = Path.Combine(projectDir, KuaishouWorkspaceProjects.ProjectFileName);
        var metadata = ReadObject(metadataPath) ?? new JsonObject();
        var info = ReadInfo(Path.Combine(projectDir, "短剧信息.txt"));
        var external = ReadObject(Path.Combine(projectDir, "metadata.json"));
        var title = ResolveTitle(projectDir, metadata, info, external);
        var intro = First(
            info.GetValueOrDefault("简介"),
            Text(metadata, "intro"),
            Text(external, "intro"),
            Text(external, "description"));
        var episodeCount = Math.Max(CountVideos(projectDir), Math.Max(Number(metadata, "episodeCount"), Number(external, "episode_count")));
        var workflowName = First(Text(metadata, "workflowDirName"), "_" + SafeDirectoryName(title));
        var workflowDir = First(Text(metadata, "workflowProjectDir"));
        if (string.IsNullOrWhiteSpace(workflowDir) || !Directory.Exists(workflowDir))
            workflowDir = Path.Combine(Directory.GetParent(projectDir)!.FullName, "workflow", workflowName);
        Directory.CreateDirectory(workflowDir);

        var document = new JsonObject
        {
            ["projectKey"] = Path.GetFileName(projectDir),
            ["sourceName"] = Path.GetFileName(projectDir),
            ["displayName"] = title,
            ["bookId"] = First(Text(metadata, "bookId"), Text(external, "drama_id")),
            ["title"] = title,
            ["originalTitle"] = First(Text(metadata, "originalTitle"), title),
            ["intro"] = intro,
            ["category"] = First(Text(metadata, "category"), JoinCategories(external)),
            ["episodeCount"] = episodeCount,
            ["posterUrl"] = First(Text(metadata, "posterUrl"), Text(external, "cover_url")),
            ["configDir"] = Text(metadata, "configDir"),
            ["episodes"] = First(Text(metadata, "episodes"), "all"),
            ["quality"] = First(Text(metadata, "quality"), "local"),
            ["concurrent"] = Math.Max(1, Number(metadata, "concurrent") == 0 ? 1 : Number(metadata, "concurrent")),
            ["episodeNumberMode"] = First(Text(metadata, "episodeNumberMode"), "source"),
            ["workflowDirName"] = Path.GetFileName(workflowDir),
            ["workflowProjectDir"] = workflowDir,
            ["sourceProjectDir"] = projectDir,
            ["createdAt"] = First(Text(metadata, "createdAt"), DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")),
            ["importMode"] = "local",
            ["localImported"] = true,
            ["downloadDisabled"] = true,
        };
        var json = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(metadataPath, json);
        File.WriteAllText(Path.Combine(workflowDir, KuaishouWorkspaceProjects.ProjectFileName), json);
        EnsureInfo(projectDir, title, episodeCount, intro);
        EnsureInfo(workflowDir, title, episodeCount, intro);
        EnsurePosterAlias(projectDir);
        return new KuaishouLocalDramaBootstrapResult(projectDir, title);
    }

    private static void EnsureInfo(string directory, string title, int episodeCount, string intro)
    {
        var path = Path.Combine(directory, "短剧信息.txt");
        var existing = ReadInfo(path);
        var placeholder = Path.GetFileName(directory);
        if (!existing.TryGetValue("原剧名", out var original) || string.IsNullOrWhiteSpace(original) || original == placeholder)
            existing["原剧名"] = title;
        if (!existing.TryGetValue("新剧名", out var renamed) || string.IsNullOrWhiteSpace(renamed) || renamed == placeholder)
            existing["新剧名"] = existing["原剧名"];
        if (!existing.ContainsKey("集数") || string.IsNullOrWhiteSpace(existing["集数"]))
            existing["集数"] = Math.Max(0, episodeCount).ToString();
        if (!string.IsNullOrWhiteSpace(intro) &&
            (!existing.ContainsKey("简介") || string.IsNullOrWhiteSpace(existing["简介"])))
            existing["简介"] = intro;
        var lines = new[] { "原剧名", "新剧名", "集数", "简介" }
            .Where(existing.ContainsKey)
            .Select(key => $"{key}：{existing[key]}");
        foreach (var pair in existing.Where(pair => pair.Key is not "原剧名" and not "新剧名" and not "集数" and not "简介"))
            lines = lines.Append($"{pair.Key}：{pair.Value}");
        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static void EnsurePosterAlias(string directory)
    {
        if (ImageExtensions.Any(extension => File.Exists(Path.Combine(directory, "海报图片" + extension))))
            return;
        var poster = FindPoster(directory);
        if (poster is null)
            return;
        var alias = Path.Combine(directory, "海报图片" + Path.GetExtension(poster).ToLowerInvariant());
        if (!File.Exists(alias))
            File.Copy(poster, alias, overwrite: false);
    }

    private static string? FindPoster(string directory)
    {
        if (!Directory.Exists(directory))
            return null;
        var images = Directory.EnumerateFiles(directory)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFileName(directory), "海报图片", "海报", "封面", "poster", "cover",
        };
        return images.FirstOrDefault(path => preferred.Contains(Path.GetFileNameWithoutExtension(path)))
               ?? (images.Length == 1 ? images[0] : images.FirstOrDefault());
    }

    private static int CountVideos(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;
        var count = 0;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current))
            {
                if (VideoExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) &&
                    !Path.GetFileName(file).StartsWith('.'))
                    count++;
            }

            foreach (var child in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(child);
                if (!name.StartsWith('.') && !IgnoredDirectories.Contains(name) &&
                    !name.Equals("material-videos", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("素材剪辑输出", StringComparison.OrdinalIgnoreCase))
                    pending.Push(child);
            }
        }

        return count;
    }

    private static string ResolveTitle(string projectDirectory, JsonObject? metadata, Dictionary<string, string> info, JsonObject? external = null)
    {
        var folder = Path.GetFileName(projectDirectory);
        var stripped = folder.StartsWith("红果_", StringComparison.Ordinal) && folder.Length > "红果_".Length
            ? folder["红果_".Length..]
            : folder;
        return First(
            Text(metadata, "displayName"),
            Text(metadata, "title"),
            Text(metadata, "originalTitle"),
            info.GetValueOrDefault("原剧名"),
            Text(external, "title"),
            stripped);
    }

    private static Dictionary<string, string> ReadInfo(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return result;
        foreach (var line in File.ReadLines(path))
        {
            var index = line.IndexOfAny([':', '：']);
            if (index > 0)
                result[line[..index].Trim()] = line[(index + 1)..].Trim();
        }

        return result;
    }

    private static JsonObject? ReadObject(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonObject? node, string name)
    {
        if (node?[name] is not JsonValue value)
            return string.Empty;
        return value.TryGetValue<string>(out var text) ? text.Trim() : string.Empty;
    }

    private static int Number(JsonObject? node, string name)
    {
        if (node?[name] is not JsonValue value)
            return 0;
        if (value.TryGetValue<int>(out var number))
            return number;
        return value.TryGetValue<string>(out var text) && int.TryParse(text, out number) ? number : 0;
    }

    private static string JoinCategories(JsonObject? external)
    {
        if (external?["categories"] is not JsonArray categories)
            return string.Empty;
        return string.Join(",", categories.Select(item => item?.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    private static string SafeDirectoryName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new string(value.Where(character => !invalid.Contains(character)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(text) ? "短剧" : text;
    }

    private static string First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
