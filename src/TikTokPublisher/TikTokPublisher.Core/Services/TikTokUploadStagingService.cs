using System.Text.RegularExpressions;

namespace TikTokPublisher.Core.Services;

public static class TikTokUploadStagingService
{
    public const string StagingDirName = "tiktok_upload_videos";
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

        if (staged.Count > 0)
            log?.Invoke($"TikTok 上传副本已生成，共 {staged.Count} 个：{Path.GetFileName(staged[0])}");
        return staged;
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
