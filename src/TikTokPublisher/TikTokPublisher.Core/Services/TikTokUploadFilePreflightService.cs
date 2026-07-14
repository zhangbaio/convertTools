using System.Text.RegularExpressions;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public sealed record TikTokUploadFilePreflightResult(
    bool Ok,
    string Message,
    IReadOnlyList<string> VideoPaths);

public static class TikTokUploadFilePreflightService
{
    private static readonly Regex EpisodeNumberPattern =
        new(@"第\s*(\d+)\s*集", RegexOptions.Compiled);

    public static Task<TikTokUploadFilePreflightResult> ValidateAsync(
        QueueProjectItem item,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var paths = ResolveUploadPaths(item);
        return ValidatePathsAsync(paths, log, ct);
    }

    public static Task<TikTokUploadFilePreflightResult> ValidateAsync(
        PublishItem item,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var paths = ResolveUploadPaths(item);
        return ValidatePathsAsync(paths, log, ct);
    }

    private static async Task<TikTokUploadFilePreflightResult> ValidatePathsAsync(
        IReadOnlyList<string> videoPaths,
        Action<string>? log,
        CancellationToken ct)
    {
        if (videoPaths.Count == 0)
            return Fail("上传前素材校验失败：未找到可上传视频文件。", videoPaths);

        var ffprobe = "";
        var issues = new List<string>();
        for (var index = 0; index < videoPaths.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var path = videoPaths[index];
            var episode = ResolveEpisodeIndex(path, index);
            var name = Path.GetFileName(path);

            if (!File.Exists(path))
            {
                issues.Add($"第{episode}集 | {name} | 文件不存在");
                continue;
            }

            long fileSize;
            try
            {
                fileSize = new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                issues.Add($"第{episode}集 | {name} | 读取文件大小失败（{ex.Message}）");
                continue;
            }

            MediaProbe probe;
            try
            {
                if (string.IsNullOrWhiteSpace(ffprobe))
                    ffprobe = MediaBinaryResolver.ResolveFfprobe();
                probe = await MediaProbe.ProbeAsync(ffprobe, path, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                issues.Add($"第{episode}集 | {name} | 读取视频时长失败（{ex.Message}）");
                continue;
            }

            foreach (var message in TikTokMaterialValidationService.ValidateVideoLimits(fileSize, probe.DurationSeconds))
                issues.Add($"第{episode}集 | {name} | {message}");
        }

        if (issues.Count == 0)
        {
            log?.Invoke($"上传前素材校验通过：共 {videoPaths.Count} 个视频。");
            return new TikTokUploadFilePreflightResult(true, "", videoPaths);
        }

        foreach (var issue in issues.Take(10))
            log?.Invoke($"上传前素材校验失败：{issue}");

        var preview = string.Join("；", issues.Take(5));
        var suffix = issues.Count > 5 ? $"；等 {issues.Count} 个问题" : "";
        return Fail($"上传前素材校验失败：{preview}{suffix}。请先执行「小文件修复/素材校验」后再上传。", videoPaths);
    }

    private static TikTokUploadFilePreflightResult Fail(string message, IReadOnlyList<string> paths) =>
        new(false, message, paths);

    private static IReadOnlyList<string> ResolveUploadPaths(QueueProjectItem item)
    {
        var paths = !string.IsNullOrWhiteSpace(item.ProjectDir)
            ? ProjectVideoResolver.ResolveUploadVideos(item.ProjectDir, allowStagedFallback: true).ToList()
            : new List<string>();
        if (paths.Count == 0 && !string.IsNullOrWhiteSpace(item.PrimaryVideoPath))
            paths.Add(item.PrimaryVideoPath);
        return paths;
    }

    private static IReadOnlyList<string> ResolveUploadPaths(PublishItem item)
    {
        var paths = !string.IsNullOrWhiteSpace(item.ProjectDir)
            ? ProjectVideoResolver.ResolveUploadVideos(item.ProjectDir, allowStagedFallback: true).ToList()
            : new List<string>();
        if (paths.Count == 0 && !string.IsNullOrWhiteSpace(item.VideoPath))
            paths.Add(item.VideoPath);
        return paths;
    }

    private static int ResolveEpisodeIndex(string path, int fallbackIndex)
    {
        var match = EpisodeNumberPattern.Match(Path.GetFileName(path));
        return match.Success && int.TryParse(match.Groups[1].Value, out var episode) && episode > 0
            ? episode
            : fallbackIndex + 1;
    }
}
