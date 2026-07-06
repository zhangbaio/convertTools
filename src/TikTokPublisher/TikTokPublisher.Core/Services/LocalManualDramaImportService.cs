using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public sealed record LocalManualDramaImportResult(
    string SourceProjectDir,
    string WorkflowProjectDir,
    string DisplayName,
    int EpisodeCount);

public static class LocalManualDramaImportService
{
    private const string MetadataFile = "shortdrama-project.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi", ".flv", ".wmv",
    };

    public static LocalManualDramaImportResult Import(
        string workspaceRoot,
        string sourceProjectDir,
        Action<string>? log = null)
    {
        var workspace = NormalizeFullPath(workspaceRoot);
        if (string.IsNullOrWhiteSpace(workspace))
            throw new InvalidOperationException("请先选择 TikTok 上传工作目录");

        Directory.CreateDirectory(workspace);

        var source = NormalizeFullPath(sourceProjectDir);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"本地剧集目录不存在：{source}");

        var videos = ResolveLocalVideos(source);
        if (videos.Count == 0)
            throw new InvalidOperationException("本地剧集目录未找到视频文件，请选择包含 mp4/mov 等剧集视频的文件夹");

        var displayName = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = "本地导入剧集";

        var metadataPath = Path.Combine(source, MetadataFile);
        var metadata = ReadMetadata(metadataPath);
        var workflowDir = ResolveWorkflowProjectDir(workspace, source, displayName, metadata);
        Directory.CreateDirectory(workflowDir);

        var now = DateTimeOffset.Now.ToString("o");
        var projectKey = FirstNonEmpty(ReadString(metadata, "projectKey"), SanitizeFileName(displayName));
        metadata["projectKey"] = projectKey;
        metadata["sourceName"] = FirstNonEmpty(ReadString(metadata, "sourceName"), displayName);
        metadata["displayName"] = FirstNonEmpty(ReadString(metadata, "displayName"), displayName);
        metadata["bookId"] = FirstNonEmpty(ReadString(metadata, "bookId"), BuildLocalBookId(source));
        metadata["title"] = FirstNonEmpty(ReadString(metadata, "title"), displayName);
        metadata["originalTitle"] = FirstNonEmpty(ReadString(metadata, "originalTitle"), displayName);
        metadata["intro"] = FirstNonEmpty(ReadString(metadata, "intro"), ReadIntro(source));
        metadata["category"] = FirstNonEmpty(ReadString(metadata, "category"), "本地导入");
        metadata["episodeCount"] = videos.Count;
        metadata["effectiveEpisodeCount"] = videos.Count;
        metadata["episodes"] = FirstNonEmpty(ReadString(metadata, "episodes"), "all");
        metadata["quality"] = FirstNonEmpty(ReadString(metadata, "quality"), "1080P");
        metadata["concurrent"] = ReadPositiveInt(metadata, "concurrent") ?? 3;
        metadata["episodeNumberMode"] = FirstNonEmpty(ReadString(metadata, "episodeNumberMode"), "source");
        metadata["workflowDirName"] = Path.GetFileName(workflowDir);
        metadata["workflowProjectDir"] = workflowDir;
        metadata["sourceProjectDir"] = source;
        metadata["queueEntryDramaType"] = FirstNonEmpty(ReadString(metadata, "queueEntryDramaType"), "local_manual");
        metadata["localManualImport"] = true;
        metadata["updatedAt"] = now;
        if (string.IsNullOrWhiteSpace(ReadString(metadata, "createdAt")))
            metadata["createdAt"] = now;

        WriteMetadata(metadataPath, metadata);
        ProjectWorkspaceService.EnsureWorkflowInfo(source, videos.Count, log);

        return new LocalManualDramaImportResult(source, workflowDir, displayName, videos.Count);
    }

    private static IReadOnlyList<string> ResolveLocalVideos(string sourceProjectDir)
    {
        var candidates = new List<string>();
        foreach (var root in new[] { sourceProjectDir, Path.Combine(sourceProjectDir, "videos") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly))
            {
                if (VideoExtensions.Contains(Path.GetExtension(path)))
                    candidates.Add(Path.GetFullPath(path));
            }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => BuildNaturalSortKey(Path.GetFileName(path)), NaturalSortKeyComparer.Instance)
            .ToArray();
    }

    private static string ResolveWorkflowProjectDir(
        string workspaceRoot,
        string sourceProjectDir,
        string displayName,
        JsonObject metadata)
    {
        var configuredPath = ReadString(metadata, "workflowProjectDir");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var full = NormalizeFullPath(configuredPath);
            if (IsWithinDirectory(full, workspaceRoot))
                return full;
        }

        var configuredName = ReadString(metadata, "workflowDirName");
        var baseName = !string.IsNullOrWhiteSpace(configuredName)
            ? SanitizeFileName(configuredName)
            : "_" + SanitizeFileName(displayName);
        if (!baseName.StartsWith('_'))
            baseName = "_" + baseName;

        var workflowRoot = Path.Combine(workspaceRoot, "workflow");
        Directory.CreateDirectory(workflowRoot);

        for (var index = 0; index < 200; index++)
        {
            var name = index == 0 ? baseName : $"{baseName}-{index + 1}";
            var candidate = Path.Combine(workflowRoot, name);
            if (CanReuseWorkflowDir(candidate, sourceProjectDir))
                return candidate;
        }

        return Path.Combine(workflowRoot, $"{baseName}-{Guid.NewGuid():N}");
    }

    private static bool CanReuseWorkflowDir(string workflowDir, string sourceProjectDir)
    {
        if (!Directory.Exists(workflowDir))
            return true;

        var metadata = ReadMetadata(Path.Combine(workflowDir, MetadataFile));
        var boundSource = ReadString(metadata, "sourceProjectDir");
        if (!string.IsNullOrWhiteSpace(boundSource) &&
            string.Equals(NormalizeFullPath(boundSource), sourceProjectDir, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !Directory.EnumerateFileSystemEntries(workflowDir).Any();
    }

    private static string ReadIntro(string sourceProjectDir)
    {
        foreach (var name in new[] { "简介.txt", "详细简介.txt" })
        {
            var path = Path.Combine(sourceProjectDir, name);
            if (!File.Exists(path)) continue;

            var text = File.ReadAllText(path, Encoding.UTF8).Trim();
            if (text.Length > 0)
                return text.Length <= 4000 ? text : text[..4000];
        }

        return "";
    }

    private static JsonObject ReadMetadata(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new JsonObject();

            return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static void WriteMetadata(string path, JsonObject metadata)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, metadata.ToJsonString(JsonOptions), Encoding.UTF8);
    }

    private static string ReadString(JsonObject metadata, string key)
    {
        if (!metadata.TryGetPropertyValue(key, out var node) || node is null)
            return "";

        try
        {
            return node.GetValue<string>()?.Trim() ?? "";
        }
        catch
        {
            return node.ToJsonString().Trim('"').Trim();
        }
    }

    private static int? ReadPositiveInt(JsonObject metadata, string key)
    {
        if (!metadata.TryGetPropertyValue(key, out var node) || node is null)
            return null;

        try
        {
            return node.GetValue<int>() is var value && value > 0 ? value : null;
        }
        catch
        {
            return int.TryParse(ReadString(metadata, key), out var value) && value > 0 ? value : null;
        }
    }

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

    private static string BuildLocalBookId(string sourceProjectDir)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sourceProjectDir.ToLowerInvariant()));
        return "local:" + Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string((value ?? "").Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray())
            .Trim()
            .Trim('.');
        sanitized = Regex.Replace(sanitized, @"\s+", " ");
        return string.IsNullOrWhiteSpace(sanitized) ? "drama" : sanitized;
    }

    private static string NormalizeFullPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables((path ?? "").Trim());
        return string.IsNullOrWhiteSpace(expanded) ? "" : Path.GetFullPath(expanded);
    }

    private static bool IsWithinDirectory(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            return false;

        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static NaturalSortKey BuildNaturalSortKey(string value)
    {
        var parts = new List<IComparable>();
        foreach (var token in Regex.Split(value, @"(\d+)"))
        {
            if (string.IsNullOrEmpty(token)) continue;
            parts.Add(int.TryParse(token, out var n) ? n : token.ToLowerInvariant());
        }

        return new NaturalSortKey(parts.ToArray());
    }

    private sealed record NaturalSortKey(IComparable[] Parts);

    private sealed class NaturalSortKeyComparer : IComparer<NaturalSortKey>
    {
        public static readonly NaturalSortKeyComparer Instance = new();

        public int Compare(NaturalSortKey? x, NaturalSortKey? y)
        {
            var left = x?.Parts ?? Array.Empty<IComparable>();
            var right = y?.Parts ?? Array.Empty<IComparable>();
            var count = Math.Max(left.Length, right.Length);
            for (var i = 0; i < count; i++)
            {
                if (i >= left.Length) return -1;
                if (i >= right.Length) return 1;
                var cmp = Comparer<IComparable>.Default.Compare(left[i], right[i]);
                if (cmp != 0) return cmp;
            }

            return 0;
        }
    }
}
