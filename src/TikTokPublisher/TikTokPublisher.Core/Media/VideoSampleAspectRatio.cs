using System.Globalization;
using System.Text.RegularExpressions;

namespace TikTokPublisher.Core.Media;

internal static class VideoSampleAspectRatio
{
    private static readonly (int Width, int Height)[] StandardRatios =
    [
        (0, 0), (1, 1), (12, 11), (10, 11), (16, 11), (40, 33), (24, 11), (20, 11),
        (32, 11), (80, 33), (18, 11), (15, 11), (64, 33), (160, 99), (4, 3), (3, 2), (2, 1),
    ];

    private static readonly Regex TraceField = new(
        @"\b(aspect_ratio_idc|sar_width|sar_height)\s+[01]+\s+=\s+(\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<string?> CheckAsync(string path, MediaProbe probe, CancellationToken ct)
    {
        if (probe.VideoCodec is not ("h264" or "hevc") || !TryReadRatio(probe.SampleAspectRatio, out _, out _))
            return null;

        // Read the codec headers directly because ffprobe's frame SAR can also prefer the container.
        var (code, _, trace) = await FfmpegRunner.RunCaptureAsync(
            MediaBinaryResolver.ResolveFfmpeg(),
            ["-nostdin", "-hide_banner", "-loglevel", "info", "-i", path, "-map", "0:v:0",
                "-c:v", "copy", "-bsf:v", "trace_headers", "-frames:v", "1", "-f", "null", "-"], ct)
            .ConfigureAwait(false);
        if (code != 0)
        {
            var error = trace.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault();
            throw new InvalidOperationException($"读取视频码流比例失败（FFmpeg {code}）：{error}");
        }

        return FindConflict(probe.SampleAspectRatio, trace);
    }

    internal static string? FindConflict(string containerRatio, string trace)
    {
        if (!TryReadRatio(containerRatio, out var width, out var height))
            return null;

        var fields = TraceField.Matches(trace);
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            if (field.Groups[1].Value != "aspect_ratio_idc")
                continue;

            var idc = ReadValue(field);
            var (codecWidth, codecHeight) = idc switch
            {
                255 when index + 2 < fields.Count &&
                    fields[index + 1].Groups[1].Value == "sar_width" &&
                    fields[index + 2].Groups[1].Value == "sar_height" =>
                    (ReadValue(fields[index + 1]), ReadValue(fields[index + 2])),
                > 0 when idc < StandardRatios.Length => StandardRatios[idc],
                _ => (0, 0),
            };
            if (codecWidth > 0 && codecHeight > 0 && (long)width * codecHeight != (long)height * codecWidth)
                return $"像素宽高比冲突（容器 {containerRatio}，码流 {codecWidth}:{codecHeight}），请重新去重或修复比例后再上传";
        }

        return null;
    }

    private static int ReadValue(Match field) =>
        int.Parse(field.Groups[2].Value, CultureInfo.InvariantCulture);

    private static bool TryReadRatio(string ratio, out int width, out int height)
    {
        width = height = 0;
        var parts = ratio.Split(':', 2);
        return parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height) &&
            width > 0 && height > 0;
    }
}
