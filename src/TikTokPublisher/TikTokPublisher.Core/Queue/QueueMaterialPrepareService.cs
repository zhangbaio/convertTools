using System.Diagnostics;
using System.Globalization;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

/// <summary>对齐 Python <c>material_prepare_service.prepare_project_material_inputs</c>（含无封面时视频抽帧）。</summary>
public static class QueueMaterialPrepareService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif",
    };

    public static async Task<string?> PrepareMaterialInputsAsync(
        string projectDir,
        Action<string>? log,
        CancellationToken ct)
    {
        var context = ProjectWorkspaceService.LoadContext(projectDir);
        var workflowDir = ProjectWorkspaceService.PrepareWorkflowProject(context.SourceProjectDir, log);
        var episodeCount = ProjectWorkspaceService.ResolveSourceEpisodeCount(projectDir);
        ProjectWorkspaceService.EnsureWorkflowInfo(projectDir, episodeCount, log);

        var posterPath = await EnsureSourcePosterAsync(context.SourceProjectDir, log, ct);
        if (posterPath is not null)
        {
            ProjectWorkspaceService.PrepareWorkflowProject(context.SourceProjectDir, log);
            log?.Invoke($"素材封面已就绪：{Path.GetFileName(posterPath)}");
        }

        return ProjectWorkspaceService.FindPosterInputFile(context.SourceProjectDir, workflowDir);
    }

    private static async Task<string?> EnsureSourcePosterAsync(
        string sourceProjectDir,
        Action<string>? log,
        CancellationToken ct)
    {
        var existing = FindPosterAlias(sourceProjectDir);
        if (existing is not null)
            return existing;

        var image = FindFirstImage(sourceProjectDir, Path.GetFileName(sourceProjectDir));
        if (image is not null)
        {
            var target = Path.Combine(sourceProjectDir, $"海报图片{Path.GetExtension(image)}");
            if (!File.Exists(target))
            {
                File.Copy(image, target, overwrite: false);
                log?.Invoke($"已生成项目海报别名：{Path.GetFileName(target)}");
            }

            return target;
        }

        var videoPath = ResolveFirstSourceVideo(sourceProjectDir);
        if (videoPath is null)
            return null;

        var framePoster = Path.Combine(sourceProjectDir, "海报图片.jpg");
        await ExtractVideoFrameAsync(videoPath, framePoster, sampleSeconds: 1.0, log, ct);
        log?.Invoke($"已从视频抽帧生成封面：{Path.GetFileName(framePoster)}");
        return framePoster;
    }

    private static string? FindPosterAlias(string projectDir)
    {
        foreach (var ext in ImageExtensions)
        {
            var path = Path.Combine(projectDir, $"海报图片{ext}");
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static string? FindFirstImage(string projectDir, string preferredStem)
    {
        if (!Directory.Exists(projectDir)) return null;

        var candidates = Directory.EnumerateFiles(projectDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .Where(path =>
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                return !stem.StartsWith("工程图_", StringComparison.Ordinal) &&
                       !stem.StartsWith("成本报表", StringComparison.Ordinal);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates.FirstOrDefault(path => Path.GetFileNameWithoutExtension(path) == preferredStem)
               ?? candidates.FirstOrDefault();
    }

    private static string? ResolveFirstSourceVideo(string sourceProjectDir)
    {
        var videos = ProjectVideoResolver.ResolveUploadVideos(sourceProjectDir);
        return videos.FirstOrDefault();
    }

    private static async Task ExtractVideoFrameAsync(
        string videoPath,
        string outputPath,
        double sampleSeconds,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ffmpeg = FfmpegLocator.ResolveFfmpeg();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (File.Exists(outputPath)) File.Delete(outputPath);

        var args = new[]
        {
            "-hide_banner",
            "-loglevel", "error",
            "-ss", sampleSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", videoPath,
            "-frames:v", "1",
            "-y",
            outputPath,
        };

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                WorkingDirectory = Path.GetDirectoryName(videoPath) ?? Environment.CurrentDirectory,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Clear();
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
            throw new InvalidOperationException($"视频抽帧失败：{Path.GetFileName(videoPath)}（{stderr.Trim()}）");

        log?.Invoke($"抽帧封面：{Path.GetFileName(videoPath)} @ {sampleSeconds:0.###}s");
    }
}
