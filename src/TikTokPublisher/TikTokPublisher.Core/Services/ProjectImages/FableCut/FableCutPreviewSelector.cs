using System.Globalization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TikTokPublisher.Core.Media;

namespace TikTokPublisher.Core.Services.ProjectImages.FableCut;

internal static class FableCutPreviewSelector
{
    public static async Task<double> SelectAsync(
        string videoPath,
        double durationSeconds,
        double preferredRatio,
        CancellationToken ct)
    {
        var fallback = Math.Clamp(preferredRatio, 0.04, 0.96);
        if (durationSeconds <= 1)
            return fallback;

        var preferredTime = durationSeconds * fallback;
        var min = Math.Max(0.5, preferredTime - durationSeconds * 0.42);
        var max = Math.Min(durationSeconds - 0.5, preferredTime + durationSeconds * 0.42);
        if (max <= min)
            return fallback;

        var candidates = Enumerable.Range(0, 13)
            .Select(index => min + (max - min) * index / 12d)
            .Append(preferredTime)
            .DistinctBy(value => Math.Round(value, 3))
            .ToArray();
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"fablecut-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var ffmpeg = MediaBinaryResolver.ResolveFfmpeg();
            var bestTime = preferredTime;
            var bestScore = double.NegativeInfinity;
            for (var index = 0; index < candidates.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var framePath = Path.Combine(tempDirectory, $"frame-{index:D2}.png");
                try
                {
                    await FfmpegRunner.RunAsync(ffmpeg,
                    [
                        "-y", "-hide_banner", "-loglevel", "error",
                        "-ss", candidates[index].ToString("0.###", CultureInfo.InvariantCulture),
                        "-i", videoPath,
                        "-frames:v", "1",
                        "-vf", "scale=640:-2",
                        framePath,
                    ], ct).ConfigureAwait(false);
                    using var image = Image.Load<Rgba32>(framePath);
                    var score = TikTokAiGenerationScreenshotService.ScoreFaceVisibility(image);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestTime = candidates[index];
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* fixed-ratio fallback */ }
            }
            return double.IsFinite(bestScore)
                ? Math.Clamp(bestTime / durationSeconds, 0.01, 0.99)
                : fallback;
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); }
            catch { }
        }
    }
}
