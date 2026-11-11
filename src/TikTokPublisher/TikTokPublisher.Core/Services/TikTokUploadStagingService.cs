using System.Text.Json;
using System.Text.RegularExpressions;

namespace TikTokPublisher.Core.Services;

public static class TikTokUploadStagingService
{
    public const string StagingDirName = "tiktok_upload_videos";
    private const string ManifestFileName = ".upload-source-manifest.json";
    private static readonly Regex ForbiddenChars = new(@"[<>:""/\\|?*\x00-\x1f]", RegexOptions.Compiled);
    private static readonly Regex EpisodePattern = new(@"第\s*0*(\d+)\s*集", RegexOptions.Compiled);
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi", ".flv", ".wmv",
    };

    public sealed record StagingResult(IReadOnlyList<string> SourcePaths, IReadOnlyList<string> UploadPaths);

    public static StagingResult BuildPayload(
        string sourceProjectDir,
        string? title,
        string? originalTitle,
        bool rebuildStaging,
        bool repairSmallVideos,
        Action<string>? log,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(sourceProjectDir);
        var workflow = TikTokUploadStateStore.ResolveWorkflowProjectDir(source);
        var sourceVideos = ProjectVideoResolver.ResolveSourceVideos(
            source,
            allowStagedFallback: !rebuildStaging).ToList();
        if (sourceVideos.Count == 0)
            return new StagingResult(Array.Empty<string>(), Array.Empty<string>());

        var safeTitle = SanitizeTitle(title, originalTitle, source);
        var uploadPaths = PrepareUploadFiles(
            workflow,
            safeTitle,
            sourceVideos,
            rebuildStaging,
            repairSmallVideos,
            log,
            ct);

        return new StagingResult(sourceVideos, uploadPaths);
    }

    public static IReadOnlyList<string> PrepareUploadFiles(
        string workflowProjectDir,
        string safeTitle,
        IReadOnlyList<string> videoPaths,
        bool rebuildStaging,
        bool repairSmallVideos,
        Action<string>? log,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var workflow = Path.GetFullPath(workflowProjectDir);
        var stagingRoot = Path.Combine(workflow, StagingDirName);

        if (!rebuildStaging)
        {
            NormalizeExistingNames(stagingRoot, safeTitle, log);
            if (ReferencesExternalSources(stagingRoot, videoPaths) &&
                !ManifestMatches(stagingRoot, videoPaths))
            {
                if (Directory.Exists(stagingRoot))
                    log?.Invoke("上传副本与当前成片不一致，将按当前成片重建。");
                return Array.Empty<string>();
            }

            var existing = ResolveExisting(stagingRoot, safeTitle, videoPaths);
            if (existing.Count > 0) return existing;
            return Array.Empty<string>();
        }

        if (Directory.Exists(stagingRoot))
        {
            log?.Invoke("检测到旧的 tiktok_upload_videos，已自动重建。");
            ResilientFileSystem.DeleteDirectory(stagingRoot);
        }
        ResilientFileSystem.EnsureDirectory(stagingRoot);

        var staged = new List<string>();
        for (var index = 0; index < videoPaths.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var sourcePath = Path.GetFullPath(videoPaths[index]);
            var suffix = Path.GetExtension(sourcePath);
            if (string.IsNullOrEmpty(suffix)) suffix = ".mp4";
            var targetPath = Path.Combine(stagingRoot, $"{safeTitle}-第{index + 1}集{suffix.ToLowerInvariant()}");

            if (repairSmallVideos &&
                TikTokSmallVideoPaddingService.NeedsPadding(sourcePath) &&
                TikTokSmallVideoPaddingService.SupportsPadding(sourcePath))
            {
                TikTokSmallVideoPaddingService.CopyForPadding(sourcePath, targetPath);
                TikTokSmallVideoPaddingService.PadWithoutReencodeAsync(targetPath, log, ct)
                    .GetAwaiter().GetResult();
            }
            else
            {
                LinkOrCopy(sourcePath, targetPath);
            }
            staged.Add(targetPath);
        }

        WriteManifest(stagingRoot, videoPaths);
        if (staged.Count > 0)
            log?.Invoke($"TikTok 上传副本已生成，共 {staged.Count} 个：{Path.GetFileName(staged[0])}");
        return staged;
    }

    /// <summary>
    /// 成片还在时，上传副本必须对得上每一集的文件名、大小和修改时间。
    /// 成片已经删掉时，现有副本继续可用。
    /// </summary>
    public static bool CopiesMatchSources(string sourceProjectDir)
    {
        var sources = ProjectVideoResolver.ResolveSourceVideos(sourceProjectDir, allowStagedFallback: false);
        if (sources.Count == 0)
            return true;

        var workflow = TikTokUploadStateStore.ResolveWorkflowProjectDir(sourceProjectDir);
        var stagingRoot = Path.Combine(Path.GetFullPath(workflow), StagingDirName);
        if (!ManifestMatches(stagingRoot, sources))
            return false;

        if (!Directory.Exists(stagingRoot))
            return false;
        var stagedCount = Directory.EnumerateFiles(stagingRoot, "*.*", SearchOption.TopDirectoryOnly)
            .Count(path => VideoExtensions.Contains(Path.GetExtension(path)));
        return stagedCount == sources.Count;
    }

    /// <summary>去重写出新成片后清掉旧副本。目录不存在时返回 false。</summary>
    public static bool DiscardCopies(string sourceProjectDir)
    {
        try
        {
            var workflow = TikTokUploadStateStore.ResolveWorkflowProjectDir(sourceProjectDir);
            var stagingRoot = Path.Combine(Path.GetFullPath(workflow), StagingDirName);
            if (!Directory.Exists(stagingRoot))
                return false;
            return ResilientFileSystem.TryDeleteDirectory(stagingRoot);
        }
        catch
        {
            return false;
        }
    }

    private static void NormalizeExistingNames(string stagingRoot, string safeTitle, Action<string>? log)
    {
        if (!Directory.Exists(stagingRoot)) return;

        var videos = Directory.EnumerateFiles(stagingRoot, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => VideoExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => ResolveEpisode(path))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (videos.Count == 0) return;

        var plan = new List<(string Source, string Temp, string Target)>();
        for (var index = 0; index < videos.Count; index++)
        {
            var source = videos[index];
            var episode = ResolveEpisode(source);
            if (episode == int.MaxValue) episode = index + 1;
            var extension = Path.GetExtension(source);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".mp4";
            var target = Path.Combine(stagingRoot, $"{safeTitle}-第{episode}集{extension.ToLowerInvariant()}");
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.Exists(target) && !videos.Contains(target, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"无法同步上传视频名称，目标文件已存在：{target}");
            var temp = Path.Combine(stagingRoot, $".title-sync-{Guid.NewGuid():N}{extension}");
            plan.Add((source, temp, target));
        }

        foreach (var step in plan) File.Move(step.Source, step.Temp);
        foreach (var step in plan) File.Move(step.Temp, step.Target);
        if (plan.Count > 0)
            log?.Invoke($"已按当前新剧名同步上传视频文件名，共 {plan.Count} 个。");
    }

    private static int ResolveEpisode(string path)
    {
        var match = EpisodePattern.Match(Path.GetFileNameWithoutExtension(path));
        return match.Success && int.TryParse(match.Groups[1].Value, out var episode) && episode > 0
            ? episode
            : int.MaxValue;
    }

    private static List<string> ResolveExisting(string stagingRoot, string safeTitle, IReadOnlyList<string> videoPaths)
    {
        if (!Directory.Exists(stagingRoot)) return new List<string>();
        var resolved = new List<string>();
        for (var index = 0; index < videoPaths.Count; index++)
        {
            var suffix = Path.GetExtension(videoPaths[index]);
            if (string.IsNullOrEmpty(suffix)) suffix = ".mp4";
            var target = Path.Combine(stagingRoot, $"{safeTitle}-第{index + 1}集{suffix.ToLowerInvariant()}");
            if (!File.Exists(target)) return new List<string>();
            resolved.Add(Path.GetFullPath(target));
        }
        return resolved;
    }

    private static bool ReferencesExternalSources(string stagingRoot, IReadOnlyList<string> videoPaths)
    {
        var root = Path.GetFullPath(stagingRoot) + Path.DirectorySeparatorChar;
        return videoPaths.Any(path =>
        {
            var full = Path.GetFullPath(path);
            return !full.StartsWith(root, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        });
    }

    private static bool ManifestMatches(string stagingRoot, IReadOnlyList<string> videoPaths)
    {
        var manifestPath = Path.Combine(stagingRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
            return false;

        UploadSourceManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UploadSourceManifest>(File.ReadAllText(manifestPath));
        }
        catch
        {
            return false;
        }

        if (manifest?.Sources is null || manifest.Sources.Count != videoPaths.Count)
            return false;

        for (var index = 0; index < videoPaths.Count; index++)
        {
            var entry = manifest.Sources[index];
            FileInfo info;
            try
            {
                info = new FileInfo(videoPaths[index]);
                if (!info.Exists)
                    return false;
            }
            catch
            {
                return false;
            }

            if (entry is null ||
                !string.Equals(entry.Name, info.Name, StringComparison.OrdinalIgnoreCase) ||
                entry.Length != info.Length ||
                entry.LastWriteUtcTicks != info.LastWriteTimeUtc.Ticks)
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteManifest(string stagingRoot, IReadOnlyList<string> videoPaths)
    {
        var manifest = new UploadSourceManifest
        {
            Sources = videoPaths.Select(path =>
            {
                var info = new FileInfo(path);
                return new UploadSourceEntry
                {
                    Name = info.Name,
                    Length = info.Exists ? info.Length : 0,
                    LastWriteUtcTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0,
                };
            }).ToList(),
        };
        var path = Path.Combine(stagingRoot, ManifestFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
    }

    private sealed class UploadSourceManifest
    {
        public List<UploadSourceEntry> Sources { get; set; } = [];
    }

    private sealed class UploadSourceEntry
    {
        public string Name { get; set; } = "";
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
    }

    private static void LinkOrCopy(string sourcePath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        if (File.Exists(targetPath)) File.Delete(targetPath);
        try { File.Copy(sourcePath, targetPath, overwrite: false); }
        catch { File.Copy(sourcePath, targetPath, overwrite: true); }
    }

    private static string SanitizeTitle(string? title, string? originalTitle, string fallback)
    {
        var text = (title ?? originalTitle ?? Path.GetFileName(fallback)).Trim();
        text = ForbiddenChars.Replace(text, " ");
        text = Regex.Replace(text, @"\s+", " ").Trim().Trim('.');
        if (string.IsNullOrEmpty(text)) return "短剧视频";
        return text.Length > 80 ? text[..80] : text;
    }
}
