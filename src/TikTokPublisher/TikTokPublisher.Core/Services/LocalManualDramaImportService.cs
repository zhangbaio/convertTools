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

public sealed record LocalManualDramaImportPreview(
    string ProjectDir,
    string DisplayName,
    int EpisodeCount,
    string? PosterPath,
    string? IntroPath,
    bool MetadataExists);

public static class LocalManualDramaImportService
{
    private const string MetadataFile = "shortdrama-project.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".heic", ".heif",
    };

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "workflow", "archive", "config", "material-clip-output", TikTokUploadStagingService.StagingDirName,
    };

    public static bool IsLocalManualImportProject(string projectDir)
    {
        try
        {
            var source = NormalizeFullPath(projectDir);
            if (string.IsNullOrWhiteSpace(source))
                return false;

            var metadata = ReadMetadata(Path.Combine(source, MetadataFile));
            if (metadata.TryGetPropertyValue("localManualImport", out var marker) &&
                marker is JsonValue markerValue &&
                markerValue.TryGetValue<bool>(out var enabled) &&
                enabled)
            {
                return true;
            }

            return string.Equals(
                ReadString(metadata, "queueEntryDramaType"),
                "local_manual",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<LocalManualDramaImportPreview> ListCandidates(string workspaceRoot)
    {
        var workspace = NormalizeFullPath(workspaceRoot);
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
            return Array.Empty<LocalManualDramaImportPreview>();

        var results = new List<LocalManualDramaImportPreview>();
        foreach (var child in EnumerateCandidateProjectDirs(workspace))
        {
            var videos = ResolveLocalVideos(child);
            if (videos.Count == 0)
                continue;

            var metadataPath = Path.Combine(child, MetadataFile);
            var metadata = ReadMetadata(metadataPath);
            var externalInfo = ExternalDramaInfoReader.Read(child, metadata);
            results.Add(new LocalManualDramaImportPreview(
                ProjectDir: Path.GetFullPath(child),
                DisplayName: ResolveDisplayName(child, metadata, externalInfo.Title),
                EpisodeCount: videos.Count,
                PosterPath: FindLocalPoster(child),
                IntroPath: externalInfo.IntroPath,
                MetadataExists: File.Exists(metadataPath)));
        }

        return results;
    }

    private static IReadOnlyList<string> EnumerateCandidateProjectDirs(string workspace)
    {
        var results = new List<string>();
        foreach (var child in Directory.EnumerateDirectories(workspace)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            CollectCandidateProjectDirs(child, results, depth: 0);
        }

        return results
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool CollectCandidateProjectDirs(string directory, List<string> results, int depth)
    {
        if (ShouldSkipNestedDirectory(directory))
            return false;

        var childResults = new List<string>();
        foreach (var child in Directory.EnumerateDirectories(directory)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            CollectCandidateProjectDirs(child, childResults, depth + 1);
        }

        var videoCount = ResolveLocalVideos(directory).Count;
        if (videoCount == 0)
        {
            results.AddRange(childResults);
            return childResults.Count > 0;
        }

        var directVideoCount = CountTopLevelVideos(directory) + CountTopLevelVideos(Path.Combine(directory, "videos"));
        var hasMetadata = File.Exists(Path.Combine(directory, MetadataFile));
        var hasInfoSignal = hasMetadata || FindLocalIntroPath(directory) is not null || FindLocalPoster(directory) is not null;
        var looksLikeEpisodeLeaf = depth > 0 &&
                                   childResults.Count == 0 &&
                                   directVideoCount <= 1 &&
                                   !hasInfoSignal &&
                                   LooksLikeEpisodeFolderName(Path.GetFileName(directory));

        if (looksLikeEpisodeLeaf)
            return false;

        if (directVideoCount > 0 || hasInfoSignal || childResults.Count == 0)
        {
            results.Add(Path.GetFullPath(directory));
            return true;
        }

        results.AddRange(childResults);
        return true;
    }

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

        var metadataPath = Path.Combine(source, MetadataFile);
        var metadata = ReadMetadata(metadataPath);
        var externalInfo = ExternalDramaInfoReader.Read(source, metadata);
        var displayName = ResolveDisplayName(source, metadata, externalInfo.Title);
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
        metadata["intro"] = externalInfo.Intro;
        metadata["category"] = FirstNonEmpty(ReadString(metadata, "category"), externalInfo.Category, "本地导入");
        var declaredEpisodeCount = Math.Max(videos.Count, externalInfo.DeclaredEpisodeCount);
        metadata["episodeCount"] = declaredEpisodeCount;
        metadata["declaredEpisodeCount"] = declaredEpisodeCount;
        metadata["effectiveEpisodeCount"] = videos.Count;
        metadata["episodes"] = FirstNonEmpty(ReadString(metadata, "episodes"), "all");
        metadata["quality"] = FirstNonEmpty(ReadString(metadata, "quality"), "local");
        metadata["concurrent"] = ReadPositiveInt(metadata, "concurrent") ?? 3;
        metadata["episodeNumberMode"] = FirstNonEmpty(ReadString(metadata, "episodeNumberMode"), "source");
        metadata["workflowDirName"] = Path.GetFileName(workflowDir);
        metadata["workflowProjectDir"] = workflowDir;
        metadata["sourceProjectDir"] = source;
        metadata["queueEntryDramaType"] = FirstNonEmpty(ReadString(metadata, "queueEntryDramaType"), "local_manual");
        metadata["importMode"] = "local";
        metadata["localImported"] = true;
        metadata["localManualImport"] = true;
        metadata["downloadDisabled"] = true;
        metadata["updatedAt"] = now;
        if (string.IsNullOrWhiteSpace(ReadString(metadata, "createdAt")))
            metadata["createdAt"] = now;

        WriteMetadata(metadataPath, metadata);
        EnsureStandardPosterAlias(source);
        ProjectWorkspaceService.EnsureWorkflowInfo(source, videos.Count, log);

        return new LocalManualDramaImportResult(source, workflowDir, displayName, videos.Count);
    }

    private static IReadOnlyList<string> ResolveLocalVideos(string sourceProjectDir)
    {
        var candidates = new List<string>();
        foreach (var root in new[] { sourceProjectDir, Path.Combine(sourceProjectDir, "videos") })
        {
            if (!Directory.Exists(root)) continue;
            candidates.AddRange(EnumerateVideoFiles(root));
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => BuildNaturalSortKey(Path.GetFileName(path)), NaturalSortKeyComparer.Instance)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateVideoFiles(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly))
        {
            if (IsCandidateVideoFile(path))
                yield return Path.GetFullPath(path);
        }

        foreach (var child in Directory.EnumerateDirectories(root))
        {
            if (ShouldSkipNestedDirectory(child))
                continue;

            foreach (var path in EnumerateVideoFiles(child))
                yield return path;
        }
    }

    private static bool IsCandidateVideoFile(string path)
    {
        var name = Path.GetFileName(path);
        return ProjectVideoResolver.IsCompleteVideoFile(path) &&
               !name.StartsWith(".", StringComparison.Ordinal) &&
               !name.EndsWith(".silencefix.mp4", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSkipNestedDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ||
               name.StartsWith(".", StringComparison.Ordinal) ||
               IgnoredDirectoryNames.Contains(name);
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

    private static string? FindLocalIntroPath(string sourceProjectDir)
        => ExternalDramaInfoReader.FindIntroPath(sourceProjectDir);

    private static string? FindLocalPoster(string sourceProjectDir)
    {
        var preferredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFileName(sourceProjectDir),
            "海报图片",
            "海报",
            "封面",
            "poster",
            "cover",
        };

        var imageFiles = Directory.EnumerateFiles(sourceProjectDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)) && !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var path in imageFiles)
        {
            if (preferredNames.Contains(Path.GetFileNameWithoutExtension(path).Trim()))
                return path;
        }

        return imageFiles.FirstOrDefault();
    }

    private static int CountTopLevelVideos(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        try
        {
            return Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                .Count(IsCandidateVideoFile);
        }
        catch
        {
            return 0;
        }
    }

    private static bool LooksLikeEpisodeFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var text = name.Trim();
        return Regex.IsMatch(
            text,
            @"^(?:第?\s*\d+\s*(?:集|话|話|章|回)?|ep(?:isode)?\.?\s*\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static void EnsureStandardPosterAlias(string sourceProjectDir)
    {
        if (ImageExtensions.Any(ext =>
                File.Exists(Path.Combine(sourceProjectDir, $"{QueueMaterialPrepareService.SourcePosterStem}{ext}")) ||
                File.Exists(Path.Combine(sourceProjectDir, $"{QueueMaterialPrepareService.LegacySourcePosterStem}{ext}"))))
            return;

        var posterPath = FindLocalPoster(sourceProjectDir);
        if (string.IsNullOrWhiteSpace(posterPath))
            return;

        var aliasPath = Path.Combine(
            sourceProjectDir,
            $"{QueueMaterialPrepareService.SourcePosterStem}{Path.GetExtension(posterPath).ToLowerInvariant()}");
        if (File.Exists(aliasPath))
            return;

        try
        {
            File.Copy(posterPath, aliasPath, overwrite: false);
        }
        catch
        {
            // Poster alias is a convenience for later steps; import can still continue without it.
        }
    }

    private static string ResolveDisplayName(
        string sourceProjectDir,
        JsonObject metadata,
        string? externalTitle = null)
    {
        var fallback = Path.GetFileName(sourceProjectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return FirstNonEmpty(
            ReadString(metadata, "displayName"),
            ReadString(metadata, "sourceName"),
            ReadString(metadata, "title"),
            ReadString(metadata, "originalTitle"),
            externalTitle,
            fallback,
            "本地导入剧集");
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
