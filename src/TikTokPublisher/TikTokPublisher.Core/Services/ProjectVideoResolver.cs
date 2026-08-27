using System.Text.RegularExpressions;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

/// <summary>解析项目目录内待上传视频（对齐 Python <c>project_payload.py</c>）。</summary>
public static class ProjectVideoResolver
{
    private const string UploadStagingDirName = "tiktok_upload_videos";
    public const string MaterialVideoDirectoryName = "material-videos";
    public const string PublishedMaterialDirectoryName = "tiktok-published";

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi", ".flv", ".wmv",
    };

    private static readonly HashSet<string> IgnoredSourceDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "workflow", "archive", UploadStagingDirName,
    };

    private static readonly string[] IncompleteDownloadSuffixes =
    [
        ".aria2", ".part", ".partial", ".download", ".crdownload",
    ];

    public static IReadOnlyList<string> ResolveUploadVideos(string sourceProjectDir, bool allowStagedFallback = false)
    {
        var source = Path.GetFullPath(sourceProjectDir);
        if (!Directory.Exists(source))
            return Array.Empty<string>();

        var workflow = ProjectWorkspaceService.ResolveWorkflowProjectDir(source);
        if (string.IsNullOrWhiteSpace(workflow))
            workflow = source;

        var sourceVideos = ResolveSourceVideosFromRoots(source, workflow);
        var stagedVideos = ResolveStagedUploadVideosFromWorkflow(workflow);

        // Once the upload staging directory contains videos, it is the canonical
        // upload set. Source/material copies must not override or inflate it.
        var videoPaths = allowStagedFallback && stagedVideos.Count > 0
            ? stagedVideos
            : sourceVideos;

        if (videoPaths.Count == 0)
            return Array.Empty<string>();

        return videoPaths;
    }

    public static IReadOnlyList<string> ResolveSourceVideos(string sourceProjectDir, bool allowStagedFallback = false)
    {
        var source = Path.GetFullPath(sourceProjectDir);
        if (!Directory.Exists(source))
            return Array.Empty<string>();

        var workflow = ProjectWorkspaceService.ResolveWorkflowProjectDir(source);
        if (string.IsNullOrWhiteSpace(workflow))
            workflow = source;

        var sourceVideos = ResolveSourceVideosFromRoots(source, workflow);
        if (sourceVideos.Count > 0)
            return sourceVideos;

        return allowStagedFallback ? ResolveStagedUploadVideosFromWorkflow(workflow) : Array.Empty<string>();
    }

    public static IReadOnlyList<string> ResolveStagedUploadVideos(string sourceProjectDir)
    {
        var source = Path.GetFullPath(sourceProjectDir);
        if (!Directory.Exists(source))
            return Array.Empty<string>();

        var workflow = ProjectWorkspaceService.ResolveWorkflowProjectDir(source);
        if (string.IsNullOrWhiteSpace(workflow))
            workflow = source;

        return ResolveStagedUploadVideosFromWorkflow(workflow);
    }

    /// <summary>
    /// Resolves videos that may be used to generate local materials. Unlike
    /// <see cref="ResolveUploadVideos"/>, this includes the isolated cache restored
    /// from an already-uploaded TikTok series. The cache is deliberately never part
    /// of the upload resolver, so a later forced upload cannot publish platform
    /// downloads as if they were original source files.
    /// </summary>
    public static IReadOnlyList<string> ResolveMaterialVideos(
        string sourceProjectDir,
        bool allowStagedFallback = true)
    {
        var source = Path.GetFullPath(sourceProjectDir);
        if (!Directory.Exists(source))
            return Array.Empty<string>();

        var workflow = ProjectWorkspaceService.ResolveWorkflowProjectDir(source);
        if (string.IsNullOrWhiteSpace(workflow))
            workflow = source;

        var local = ResolveSourceVideosFromRoots(source, workflow);
        if (local.Count == 0 && allowStagedFallback)
            local = ResolveStagedUploadVideosFromWorkflow(workflow);

        var published = ResolvePublishedMaterialVideosFromWorkflow(workflow);
        if (published.Count == 0)
            return local;
        if (local.Count == 0)
            return published;

        return DedupeAndSort(
            local.Concat(published).ToList(),
            path => NaturalKey(Path.GetFileName(path)));
    }

    /// <summary>Material videos suitable for ASR, scripts, outlines, and character discovery.</summary>
    public static IReadOnlyList<string> ResolveNarrativeVideos(
        string sourceProjectDir,
        bool allowStagedFallback = true) =>
        ResolveMaterialVideos(sourceProjectDir, allowStagedFallback)
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                "证明材料抽帧兜底.mp4",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public static string ResolvePublishedMaterialVideoDirectory(string sourceProjectDir)
    {
        var source = Path.GetFullPath(sourceProjectDir);
        var workflow = ProjectWorkspaceService.ResolveWorkflowProjectDir(source);
        if (string.IsNullOrWhiteSpace(workflow))
            workflow = source;
        return Path.Combine(
            workflow,
            MaterialVideoDirectoryName,
            PublishedMaterialDirectoryName);
    }

    private static List<string> ResolveSourceVideosFromRoots(string sourceProjectDir, string workflowProjectDir)
    {
        var candidates = new List<string>();
        foreach (var root in new[] { sourceProjectDir, Path.Combine(sourceProjectDir, "videos"), Path.Combine(workflowProjectDir, "videos") })
        {
            if (!Directory.Exists(root)) continue;
            candidates.AddRange(EnumerateSourceVideos(root));
        }
        return DedupeAndSort(candidates);
    }

    private static IEnumerable<string> EnumerateSourceVideos(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly))
        {
            if (IsCandidateVideoFile(path))
                yield return Path.GetFullPath(path);
        }

        foreach (var child in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(child);
            if (string.IsNullOrWhiteSpace(name) ||
                name.StartsWith(".", StringComparison.Ordinal) ||
                IgnoredSourceDirectoryNames.Contains(name))
            {
                continue;
            }

            foreach (var path in EnumerateSourceVideos(child))
                yield return path;
        }
    }

    private static List<string> ResolveStagedUploadVideosFromWorkflow(string workflowProjectDir)
    {
        var stagingRoot = Path.Combine(workflowProjectDir, UploadStagingDirName);
        if (!Directory.Exists(stagingRoot)) return new List<string>();

        var candidates = Directory.EnumerateFiles(stagingRoot, "*.*", SearchOption.AllDirectories)
            .Where(IsCandidateVideoFile)
            .Select(Path.GetFullPath)
            .ToList();

        return DedupeAndSort(candidates, path =>
        {
            var relative = Path.GetRelativePath(stagingRoot, path);
            return NaturalKey(relative);
        });
    }

    private static List<string> ResolvePublishedMaterialVideosFromWorkflow(string workflowProjectDir)
    {
        var cacheRoot = Path.Combine(
            workflowProjectDir,
            MaterialVideoDirectoryName,
            PublishedMaterialDirectoryName);
        if (!Directory.Exists(cacheRoot)) return new List<string>();

        var candidates = Directory.EnumerateFiles(cacheRoot, "*.*", SearchOption.AllDirectories)
            .Where(IsCandidateVideoFile)
            .Select(Path.GetFullPath)
            .ToList();
        return DedupeAndSort(candidates, path =>
            NaturalKey(Path.GetRelativePath(cacheRoot, path)));
    }

    internal static bool IsCompleteVideoFile(string path)
    {
        var name = Path.GetFileName(path);
        return VideoExtensions.Contains(Path.GetExtension(path))
            && !name.EndsWith(".silencefix.mp4", StringComparison.OrdinalIgnoreCase)
            && !IncompleteDownloadSuffixes.Any(suffix => File.Exists(path + suffix));
    }

    private static bool IsCandidateVideoFile(string path) => IsCompleteVideoFile(path);

    private static List<string> DedupeAndSort(List<string> paths, Func<string, IComparable[]>? keyFn = null)
    {
        keyFn ??= NaturalKey;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var path in paths.OrderBy(p => keyFn(p), Comparer<IComparable[]>.Create(CompareNaturalKeys)))
        {
            if (!seen.Add(path)) continue;
            ordered.Add(path);
        }
        return ordered;
    }

    private static IComparable[] NaturalKey(string value)
    {
        var parts = new List<IComparable>();
        foreach (var token in Regex.Split(value, @"(\d+)"))
        {
            if (string.IsNullOrEmpty(token)) continue;
            parts.Add(int.TryParse(token, out var n) ? n : token.ToLowerInvariant());
        }
        return parts.ToArray();
    }

    private static int CompareNaturalKeys(IComparable[]? left, IComparable[]? right)
    {
        left ??= Array.Empty<IComparable>();
        right ??= Array.Empty<IComparable>();
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
