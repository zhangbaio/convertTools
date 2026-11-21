using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services.VideoDedup;

internal static class VideoDedupCalibrationSample
{
    private const int Width = 1080;
    private const int InputHeight = 1922;
    private const int OutputHeight = 1920;
    private const int Fps = 30;
    private const int Seconds = 18;

    public static VideoDedupPreset CreatePreset() => new()
    {
        PortraitWidth = Width,
        PortraitHeight = OutputHeight,
        Fps = Fps,
    };

    public static IReadOnlyList<string> BuildArguments(string encoder, string destination)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-n",
            "-f", "lavfi", "-i", FormattableString.Invariant($"testsrc2=size={Width}x{InputHeight}:rate={Fps}:duration={Seconds}"),
            "-an", "-threads:v", "4", "-c:v", encoder, "-preset", "ultrafast", "-crf", "21",
            "-pix_fmt", "yuv420p", "-color_range", "tv", "-colorspace", "bt709",
            "-color_primaries", "bt709", "-color_trc", "bt709",
        };
        if (encoder == "libx265")
            args.AddRange(["-x265-params", "pools=4:frame-threads=2:log-level=error", "-tag:v", "hvc1"]);
        args.Add(destination);
        return args;
    }

    public static async Task CreateAsync(string ffmpeg, string encoder, string destination, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await FfmpegRunner.RunAsync(ffmpeg, BuildArguments(encoder, destination), timeout.Token, belowNormal: true)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("准备视频转码测试片段超时。");
        }
    }

    public static bool Covers(VideoDedupProbe probe, VideoDedupPlanner.HardwareFrame frame, VideoDedupPreset preset) =>
        (preset.VideoCodec is VideoDedupPreset.CodecDefault or VideoDedupPreset.CodecH264) &&
        preset.Fps <= Fps && probe.AverageFramesPerSecond <= Fps &&
        FitsSample(probe.Width, probe.Height) && FitsSample(frame.Size.Width, frame.Size.Height);

    private static bool FitsSample(int width, int height) =>
        Math.Min(width, height) <= Width && Math.Max(width, height) <= InputHeight;
}
