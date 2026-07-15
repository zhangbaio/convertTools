using System.Globalization;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

/// <summary>
/// Builds the reference image used by the <c>video_frame</c> poster mode.
/// The resulting frame is still sent to the configured image model; this is
/// not a text-only image generation path.
/// </summary>
internal static class VideoFramePosterSourceService
{
    private static readonly Regex ChineseEpisodePattern = new(
        @"第\s*0*(?<episode>\d+)\s*[集话]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LeadingEpisodePattern = new(
        @"^(?:ep(?:isode)?[\s._-]*)?0*(?<episode>\d+)(?:\D|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static async Task<string> ExtractBestFrameAsync(
        string sourceProjectDir,
        string workflowProjectDir,
        ClientSettings settings,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var videos = ProjectVideoResolver.ResolveSourceVideos(sourceProjectDir, allowStagedFallback: true);
        if (videos.Count == 0)
            throw new InvalidOperationException($"未找到可用于抽帧生成封面的视频：{sourceProjectDir}");

        var desiredEpisode = Math.Clamp(settings.FrameExtractEpisodeIndex, 1, 999);
        var videoPath = SelectVideoForEpisode(videos, desiredEpisode);
        var ffmpeg = MediaBinaryResolver.ResolveFfmpeg();
        var ffprobe = MediaBinaryResolver.ResolveFfprobe();
        var duration = await FfmpegRunner.ProbeDurationSecondsAsync(
            ffprobe,
            videoPath,
            cancellationToken).ConfigureAwait(false);

        var preferredTime = double.IsFinite(settings.FrameExtractTime) && settings.FrameExtractTime > 0
            ? settings.FrameExtractTime
            : ClientSettingsDefaults.FrameExtractTime;
        if (preferredTime > duration)
            preferredTime = Math.Max(0.1, duration * 0.1);

        var candidateTimes = BuildCandidateTimes(
            preferredTime,
            duration,
            settings.FrameExtractNeighborOffsetsSeconds,
            settings.FrameExtractFallbackPercents);

        log($"正在从视频抽帧：{Path.GetFileName(videoPath)} @ {preferredTime:F1}秒");
        log($"抽帧预检候选时间点：{string.Join("、", candidateTimes.Select(value => $"{value:F1}秒"))}");

        var tempRoot = Path.Combine(Path.GetTempPath(), $"poster-frame-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        string? bestFramePath = null;
        var bestScore = double.NegativeInfinity;
        var selectedTime = preferredTime;

        try
        {
            for (var index = 0; index < candidateTimes.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidateTime = candidateTimes[index];
                var candidatePath = Path.Combine(tempRoot, $"candidate-{index:D2}.png");
                try
                {
                    await ExtractFrameAsync(
                        ffmpeg,
                        videoPath,
                        candidateTime,
                        candidatePath,
                        cancellationToken).ConfigureAwait(false);

                    var score = await ScoreFrameAsync(
                        candidatePath,
                        candidateTime,
                        preferredTime,
                        cancellationToken).ConfigureAwait(false);
                    log($"抽帧预检：{Path.GetFileName(videoPath)} @ {candidateTime:F1}秒，画面评分 {score:F3}");
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestFramePath = candidatePath;
                        selectedTime = candidateTime;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log($"抽帧预检：{candidateTime:F1}秒画面不可用，已跳过：{ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(bestFramePath) || !File.Exists(bestFramePath))
                throw new InvalidOperationException($"无法从视频提取有效画面：{Path.GetFileName(videoPath)}");

            Directory.CreateDirectory(workflowProjectDir);
            var outputPath = Path.Combine(workflowProjectDir, "抽帧图片.png");
            File.Copy(bestFramePath, outputPath, overwrite: true);
            using var selected = await Image.LoadAsync<Rgba32>(outputPath, cancellationToken).ConfigureAwait(false);
            log($"已保存抽帧图片：{Path.GetFileName(outputPath)} ({selected.Width}x{selected.Height})");
            log($"抽帧预检：已选中 {selectedTime:F1} 秒画面（评分 {bestScore:F3}）");
            return outputPath;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    internal static string SelectVideoForEpisode(IReadOnlyList<string> videos, int desiredEpisode)
    {
        if (videos.Count == 0)
            throw new ArgumentException("视频列表不能为空。", nameof(videos));

        foreach (var video in videos)
        {
            if (TryExtractEpisodeIndex(Path.GetFileNameWithoutExtension(video), out var episode)
                && episode == desiredEpisode)
            {
                return video;
            }
        }

        return videos[0];
    }

    internal static IReadOnlyList<double> BuildCandidateTimes(
        double preferredTime,
        double duration,
        string? neighborOffsets,
        string? fallbackPercents)
    {
        var safeDuration = Math.Max(0.1, double.IsFinite(duration) ? duration : 0.1);
        var safePreferred = ClampExtractTime(preferredTime, safeDuration);
        var seeds = new List<double> { safePreferred };
        foreach (var offset in ParseNumberList(neighborOffsets, [2.0, 4.0]))
        {
            var absoluteOffset = Math.Abs(offset);
            seeds.Add(safePreferred - absoluteOffset);
            seeds.Add(safePreferred + absoluteOffset);
        }

        foreach (var percent in ParseNumberList(fallbackPercents, [10.0, 25.0, 50.0, 75.0]))
            seeds.Add(safeDuration * (percent / 100.0));

        var candidates = new List<double>();
        var seenTenths = new HashSet<int>();
        foreach (var seed in seeds)
        {
            var clamped = ClampExtractTime(seed, safeDuration);
            var dedupeKey = (int)Math.Round(clamped * 10.0, MidpointRounding.AwayFromZero);
            if (!seenTenths.Add(dedupeKey))
                continue;
            candidates.Add(clamped);
        }

        return candidates;
    }

    private static async Task ExtractFrameAsync(
        string ffmpeg,
        string videoPath,
        double timeSeconds,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await FfmpegRunner.RunAsync(ffmpeg,
        [
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-ss", timeSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", videoPath,
            "-frames:v", "1",
            "-an",
            "-sn",
            outputPath,
        ], cancellationToken).ConfigureAwait(false);

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new InvalidOperationException("ffmpeg 未生成有效抽帧图片。");
    }

    private static async Task<double> ScoreFrameAsync(
        string framePath,
        double candidateTime,
        double preferredTime,
        CancellationToken cancellationToken)
    {
        using var image = await Image.LoadAsync<Rgba32>(framePath, cancellationToken).ConfigureAwait(false);
        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(320, 320),
        }));

        var count = image.Width * image.Height;
        if (count <= 0)
            return double.NegativeInfinity;

        var sum = 0.0;
        var sumSquares = 0.0;
        var edgeSum = 0.0;
        var edgeCount = 0;
        var previousRow = new double[image.Width];
        for (var y = 0; y < image.Height; y++)
        {
            var previousPixel = 0.0;
            for (var x = 0; x < image.Width; x++)
            {
                var pixel = image[x, y];
                var luminance = ((0.2126 * pixel.R) + (0.7152 * pixel.G) + (0.0722 * pixel.B)) / 255.0;
                sum += luminance;
                sumSquares += luminance * luminance;
                if (x > 0)
                {
                    edgeSum += Math.Abs(luminance - previousPixel);
                    edgeCount++;
                }
                if (y > 0)
                {
                    edgeSum += Math.Abs(luminance - previousRow[x]);
                    edgeCount++;
                }
                previousPixel = luminance;
                previousRow[x] = luminance;
            }
        }

        var mean = sum / count;
        var variance = Math.Max(0.0, (sumSquares / count) - (mean * mean));
        var contrast = Math.Sqrt(variance);
        var sharpness = edgeCount > 0 ? edgeSum / edgeCount : 0.0;
        var exposure = 1.0 - Math.Min(1.0, Math.Abs(mean - 0.5) / 0.5);
        var distancePenalty = Math.Abs(candidateTime - preferredTime) * 0.0005;
        return (sharpness * 2.0) + contrast + (exposure * 0.25) - distancePenalty;
    }

    private static IReadOnlyList<double> ParseNumberList(string? rawValue, IReadOnlyList<double> fallback)
    {
        var normalized = (rawValue ?? string.Empty).Trim().Replace('，', ',');
        if (string.IsNullOrWhiteSpace(normalized))
            return fallback;

        var values = normalized
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : double.NaN)
            .Where(double.IsFinite)
            .ToArray();
        return values.Length > 0 ? values : fallback;
    }

    private static double ClampExtractTime(double candidateTime, double duration)
    {
        var upperBound = Math.Max(0.1, duration - 0.1);
        var safeCandidate = double.IsFinite(candidateTime) ? candidateTime : 0.1;
        return Math.Max(0.1, Math.Min(safeCandidate, upperBound));
    }

    private static bool TryExtractEpisodeIndex(string fileName, out int episode)
    {
        foreach (var pattern in new[] { ChineseEpisodePattern, LeadingEpisodePattern })
        {
            var match = pattern.Match(fileName);
            if (match.Success
                && int.TryParse(match.Groups["episode"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out episode)
                && episode > 0)
            {
                return true;
            }
        }

        episode = 0;
        return false;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of temporary frame candidates.
        }
    }
}
