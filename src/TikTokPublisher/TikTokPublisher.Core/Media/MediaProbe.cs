using System.Globalization;
using System.Text.Json;

namespace TikTokPublisher.Core.Media;

public sealed class MediaProbe
{
    public double DurationSeconds { get; init; }
    public string AudioCodec { get; init; } = "";
    public int AudioBitrateBps { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRateFps { get; init; }

    public static async Task<MediaProbe> ProbeAsync(string ffprobe, string path, CancellationToken ct)
    {
        var (_, stdout, stderr) = await FfmpegRunner.RunCaptureAsync(ffprobe, new[]
        {
            "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", path,
        }, ct);
        if (string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException($"ffprobe 分析失败：{stderr.Trim()}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var format = root.TryGetProperty("format", out var formatEl) ? formatEl : default;
        var duration = 0.0;
        if (format.ValueKind == JsonValueKind.Object &&
            format.TryGetProperty("duration", out var durEl) &&
            double.TryParse(durEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            duration = parsed;

        var audioCodec = "";
        var audioBitrate = 0;
        var width = 0;
        var height = 0;
        var fps = 0.0;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = stream.TryGetProperty("codec_type", out var typeEl) ? typeEl.GetString() : "";
                if (codecType == "audio" && string.IsNullOrEmpty(audioCodec))
                {
                    audioCodec = stream.TryGetProperty("codec_name", out var codecEl) ? codecEl.GetString() ?? "" : "";
                    if (stream.TryGetProperty("bit_rate", out var brEl) &&
                        int.TryParse(brEl.GetString(), out var br))
                        audioBitrate = br;
                }
                if (codecType == "video" && width == 0)
                {
                    width = stream.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : 0;
                    height = stream.TryGetProperty("height", out var hEl) ? hEl.GetInt32() : 0;
                    var rate = stream.TryGetProperty("r_frame_rate", out var rEl) ? rEl.GetString() ?? "" : "";
                    fps = ParseFrameRate(rate);
                }
            }
        }

        return new MediaProbe
        {
            DurationSeconds = duration,
            AudioCodec = audioCodec,
            AudioBitrateBps = audioBitrate,
            Width = width,
            Height = height,
            FrameRateFps = fps,
        };
    }

    private static double ParseFrameRate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
            den > 0)
            return num / den;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var direct) ? direct : 0;
    }
}
