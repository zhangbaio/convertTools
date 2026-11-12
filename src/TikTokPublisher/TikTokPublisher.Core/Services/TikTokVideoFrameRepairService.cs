using System.Collections.Concurrent;
using System.Globalization;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 画面比例不是 9:16（横屏 16:9）的成片：等比缩放进 1080×1920（横屏 1920×1080）并补黑边。
/// 直接改项目里的成片，上传副本每次都从成片重建，只改副本会被下次重建还原。
/// </summary>
public static class TikTokVideoFrameRepairService
{
    public const string BackupDirectoryName = ".frame-repair-originals";

    private static readonly ConcurrentDictionary<string, (long Length, long WriteTicks, int Width, int Height)> FrameCache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static Func<string, CancellationToken, Task<(int Width, int Height)>>? ProbeForTests { get; set; }
    internal static Func<IReadOnlyList<string>, CancellationToken, Task>? RunFfmpegForTests { get; set; }

    public static (int Width, int Height)? ResolveTargetFrame(int width, int height)
    {
        if (!TikTokMaterialValidationService.IsAspectOutOfRange(width, height))
            return null;
        return height >= width ? (1080, 1920) : (1920, 1080);
    }

    public static bool NeedsRepair(string path, CancellationToken ct = default)
    {
        try
        {
            var (width, height) = ReadFrame(path, ct);
            return ResolveTargetFrame(width, height) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public static IReadOnlyList<string> FindMisframed(IEnumerable<string> paths, CancellationToken ct = default) =>
        paths.Where(path => NeedsRepair(path, ct)).ToList();

    /// <summary>修好一个成片并返回原尺寸描述；原片先移到同目录的备份文件夹（已有备份时不覆盖）。</summary>
    public static async Task<string> RepairInPlaceAsync(
        string videoPath,
        string projectDir,
        Action<string>? log,
        CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(videoPath);
        var (width, height) = ReadFrame(fullPath, ct);
        var target = ResolveTargetFrame(width, height);
        var before = $"{width.ToString(CultureInfo.InvariantCulture)}×{height.ToString(CultureInfo.InvariantCulture)}";
        if (target is null)
            return before;

        var directory = Path.GetDirectoryName(fullPath)!;
        var backupDirectory = Path.Combine(directory, BackupDirectoryName);
        Directory.CreateDirectory(backupDirectory);
        var temporaryPath = Path.Combine(backupDirectory, $"{Path.GetFileNameWithoutExtension(fullPath)}.{Guid.NewGuid():N}.tmp.mp4");
        try
        {
            try
            {
                await RunFfmpegAsync(BuildArguments(fullPath, temporaryPath, target.Value, copyAudio: true), ct);
            }
            catch (InvalidOperationException)
            {
                TryDelete(temporaryPath);
                await RunFfmpegAsync(BuildArguments(fullPath, temporaryPath, target.Value, copyAudio: false), ct);
            }

            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                throw new InvalidOperationException($"FFmpeg 未生成有效文件：{Path.GetFileName(fullPath)}");

            var backupPath = Path.Combine(backupDirectory, Path.GetFileName(fullPath));
            if (File.Exists(backupPath))
                File.Delete(fullPath);
            else
                File.Move(fullPath, backupPath);
            File.Move(temporaryPath, fullPath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }

        FrameCache.TryRemove(fullPath, out _);
        VideoDedupService.RefreshOutputLength(projectDir, fullPath);
        log?.Invoke(
            $"比例修复：{Path.GetFileName(fullPath)} {before} → " +
            $"{target.Value.Width.ToString(CultureInfo.InvariantCulture)}×{target.Value.Height.ToString(CultureInfo.InvariantCulture)}（等比缩放并补黑边）。");
        return before;
    }

    internal static IReadOnlyList<string> BuildArguments(
        string input,
        string output,
        (int Width, int Height) target,
        bool copyAudio)
    {
        var w = target.Width.ToString(CultureInfo.InvariantCulture);
        var h = target.Height.ToString(CultureInfo.InvariantCulture);
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", input,
            "-map", "0:v:0", "-map", "0:a?",
            "-vf", $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,format=yuv420p",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
        };
        arguments.AddRange(copyAudio ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "128k"]);
        arguments.AddRange(["-movflags", "+faststart", output]);
        return arguments;
    }

    private static (int Width, int Height) ReadFrame(string path, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("视频不存在", fullPath);

        if (FrameCache.TryGetValue(fullPath, out var cached) &&
            cached.Length == info.Length &&
            cached.WriteTicks == info.LastWriteTimeUtc.Ticks)
        {
            return (cached.Width, cached.Height);
        }

        (int Width, int Height) frame;
        if (ProbeForTests is not null)
        {
            frame = ProbeForTests(fullPath, ct).GetAwaiter().GetResult();
        }
        else
        {
            var probe = MediaProbe.ProbeAsync(MediaBinaryResolver.ResolveFfprobe(), fullPath, ct).GetAwaiter().GetResult();
            frame = (probe.Width, probe.Height);
        }

        FrameCache[fullPath] = (info.Length, info.LastWriteTimeUtc.Ticks, frame.Width, frame.Height);
        return frame;
    }

    private static Task RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
        RunFfmpegForTests is not null
            ? RunFfmpegForTests(arguments, ct)
            : FfmpegRunner.RunAsync(FfmpegLocator.ResolveFfmpeg(), arguments, ct);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 临时文件删不掉不影响结果。
        }
    }
}
