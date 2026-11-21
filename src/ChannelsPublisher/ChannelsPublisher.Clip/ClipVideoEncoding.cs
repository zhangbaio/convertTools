using System.Globalization;

namespace ChannelsPublisher.Clip;

internal static class ClipVideoEncoding
{
    public static string Software(ClipEngineOptions options) => IsHevc(options) ? "libx265" : "libx264";

    public static async Task<string> ResolveAsync(ClipEngineOptions options, Action<string>? log, CancellationToken ct)
    {
        if (!options.HardwareEncode)
            return Software(options);

        var codec = IsHevc(options) ? "hevc" : "h264";
        foreach (var implementation in new[] { "nvenc", "qsv", "amf" })
        {
            var encoder = $"{codec}_{implementation}";
            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=s=128x128:r=25", "-frames:v", "2",
            };
            arguments.AddRange(Arguments(options, encoder));
            arguments.AddRange(["-f", "null", "-"]);
            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                if ((await Ffmpeg.RunCaptureAsync(options.FfmpegPath, arguments, probeTimeout.Token)).ExitCode == 0)
                {
                    log?.Invoke($"渲染编码器：{encoder}");
                    return encoder;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log?.Invoke($"硬件编码器 {encoder} 检测超时。");
            }
        }
        log?.Invoke($"硬件编码不可用，使用 {Software(options)}。");
        return Software(options);
    }

    public static List<string> Arguments(ClipEngineOptions options, string encoder)
    {
        var fast = options.RenderSpeed == "fast";
        var quality = options.RenderSpeed == "quality";
        var fixedRate = options.EncodeMode == "bitrate";
        var preset = fast ? "veryfast" : quality ? "medium" : "faster";
        var crf = quality ? "20" : "22";
        var result = new List<string> { "-c:v", encoder, "-pix_fmt", "yuv420p" };
        switch (encoder)
        {
            case "libx264":
            case "libx265":
                result.AddRange(["-preset", preset, "-threads", "4"]);
                if (!fixedRate)
                    result.AddRange(["-crf", crf]);
                if (encoder == "libx265")
                    result.AddRange(["-x265-params", fixedRate ? "pools=4:strict-cbr=1" : "pools=4"]);
                if (encoder == "libx264" && fixedRate)
                    result.AddRange(["-x264-params", "nal-hrd=cbr"]);
                break;
            case var nvenc when nvenc.EndsWith("_nvenc", StringComparison.Ordinal):
                result.AddRange(["-preset", fast ? "p2" : quality ? "p6" : "p4", "-rc", fixedRate ? "cbr" : "vbr"]);
                if (!fixedRate)
                    result.AddRange(["-cq", crf, "-b:v", "0"]);
                break;
            case var qsv when qsv.EndsWith("_qsv", StringComparison.Ordinal):
                result.AddRange(["-preset", preset]);
                if (!fixedRate)
                    result.AddRange(["-global_quality", crf]);
                break;
            default:
                result.AddRange(["-quality", fast ? "speed" : quality ? "quality" : "balanced", "-rc", fixedRate ? "cbr" : "cqp"]);
                if (!fixedRate)
                    result.AddRange(["-qp_i", crf, "-qp_p", crf]);
                break;
        }
        if (fixedRate)
        {
            var bitrate = Math.Round(options.VideoBitrateMbps * 1000).ToString(CultureInfo.InvariantCulture) + "k";
            var buffer = Math.Round(options.VideoBitrateMbps * 2000).ToString(CultureInfo.InvariantCulture) + "k";
            result.AddRange(["-b:v", bitrate, "-minrate", bitrate, "-maxrate", bitrate, "-bufsize", buffer]);
        }
        if (IsHevc(options))
            result.AddRange(["-tag:v", "hvc1"]);
        return result;
    }

    private static bool IsHevc(ClipEngineOptions options) => options.VideoCodec == "h265";
}
