using System.Text.RegularExpressions;

namespace TikTokPublisher.Core.Services;

public static class TikTokUploadStagingService
{
    public const string StagingDirName = "tiktok_upload_videos";
    private static readonly Regex ForbiddenChars = new(@"[<>:""/\\|?*\x00-\x1f]", RegexOptions.Compiled);

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
            var existing = ResolveExisting(stagingRoot, safeTitle, videoPaths);
            if (existing.Count > 0) return existing;
            return Array.Empty<string>();
        }

        if (Directory.Exists(stagingRoot))
        {
            log?.Invoke("检测到旧的 tiktok_upload_videos，已自动重建。");
            Directory.Delete(stagingRoot, recursive: true);
        }
        Directory.CreateDirectory(stagingRoot);

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
