using System.Globalization;
using System.Text.Json;
using TikTokPublisher.Core.Media;

namespace TikTokPublisher.Core.Services.ProjectImages.FableCut;

internal readonly record struct FableCutMediaInfo(double DurationSeconds, int Width, int Height);

internal static class FableCutMediaProbe
{
    public static async Task<FableCutMediaInfo> ProbeAsync(string videoPath, CancellationToken ct)
    {
        var ffprobe = MediaBinaryResolver.ResolveFfprobe();
        var result = await FfmpegRunner.RunCaptureAsync(ffprobe,
        [
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=width,height:format=duration",
            "-of", "json",
            videoPath,
        ], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe 读取视频失败：{Path.GetFileName(videoPath)}｜{result.Stderr.Trim()}");

        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        var stream = root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array
            ? streams.EnumerateArray().FirstOrDefault()
            : default;
        var width = stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("width", out var widthElement)
            ? widthElement.GetInt32()
            : 0;
        var height = stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("height", out var heightElement)
            ? heightElement.GetInt32()
            : 0;
        var durationText = root.TryGetProperty("format", out var format) &&
                           format.TryGetProperty("duration", out var durationElement)
            ? durationElement.GetString()
            : null;
        if (width <= 0 || height <= 0 ||
            !double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) || duration <= 0)
        {
            throw new InvalidOperationException($"ffprobe 未返回有效的视频尺寸或时长：{Path.GetFileName(videoPath)}");
        }

        return new FableCutMediaInfo(duration, width, height);
    }
}
