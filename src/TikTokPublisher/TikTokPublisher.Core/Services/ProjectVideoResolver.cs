using System.Text.RegularExpressions;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

/// <summary>解析项目目录内待上传视频（对齐 Python <c>project_payload.py</c>）。</summary>
public static class ProjectVideoResolver
{
    private const string UploadStagingDirName = "tiktok_upload_videos";

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi", ".flv", ".wmv",
    };

    public static IReadOnlyList<string> ResolveUploadVideos(string sourceProjectDir, bool allowStagedFallback = false)
    {
        var source = Path.GetFullPath(sourceProjectDir);
        if (!Directory.Exists(source))
            return Array.Empty<string>();

        var workflow = ProjectWorkspaceService.ResolveWorkflowProjectDir(source);
        if (string.IsNullOrWhiteSpace(workflow))
            workflow = source;

        var sourceVideos = ResolveSourceVideosFromRoots(source, workflow);
        var stagedVideos = ResolveStagedUploadVideos(workflow);

        var videoPaths = sourceVideos.Count > 0
            ? sourceVideos
            : allowStagedFallback ? stagedVideos : sourceVideos;

        if (videoPaths.Count == 0)
            return Array.Empty<string>();

        if (stagedVideos.Count > 0 && stagedVideos.Count == videoPaths.Count)
            return stagedVideos;

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

        return allowStagedFallback ? ResolveStagedUploadVideos(workflow) : Array.Empty<string>();
    }

    private static List<string> ResolveSourceVideosFromRoots(string sourceProjectDir, string workflowProjectDir)
    {
        var candidates = new List<string>();
        foreach (var root in new[] { sourceProjectDir, Path.Combine(sourceProjectDir, "videos"), Path.Combine(workflowProjectDir, "videos") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root))
            {
                if (IsCandidateVideoFile(path))
                    candidates.Add(Path.GetFullPath(path));
            }
        }
        return DedupeAndSort(candidates);
    }

    private static List<string> ResolveStagedUploadVideos(string workflowProjectDir)
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

    private static bool IsCandidateVideoFile(string path)
    {
        var name = Path.GetFileName(path);
        return VideoExtensions.Contains(Path.GetExtension(path))
            && !name.EndsWith(".silencefix.mp4", StringComparison.OrdinalIgnoreCase);
    }

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
