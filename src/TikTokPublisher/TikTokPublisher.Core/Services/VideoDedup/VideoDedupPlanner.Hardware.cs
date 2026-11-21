using System.Globalization;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services.VideoDedup;

internal static partial class VideoDedupPlanner
{
    internal readonly record struct HardwareFrame(UploadFrame Size, string AspectRatio);

    public static HardwareFrame? GetHardwareFrame(
        VideoDedupPreset preset, IReadOnlyList<VideoDedupClip> clips, IReadOnlyList<VideoDedupSlice> slices)
    {
        if (slices.Count != 1 || preset.EncodingPreference == VideoDedupPreset.EncodingSoftwareOnly ||
            preset.CropLeftPercent + preset.CropRightPercent + preset.CropTopPercent + preset.CropBottomPercent > 0 ||
            Math.Abs(preset.SpeedFactor - 1) > 0.0001 || preset.FrameDropPercent > 0 ||
            VideoDedupEffectFilters.NeedsGraph(preset) || VideoDedupEffectFilters.LinearFilters(preset).Length != 0 ||
            (preset.Effects is { } effects && VideoDedupEffectFilters.CompositionFilters(effects, 0).Any()))
            return null;

        var slice = slices[0];
        var probe = clips[slice.ClipIndex].Probe;
        if (Math.Abs(slice.StartSeconds) > 0.001 || Math.Abs(slice.EndSeconds - probe.DurationSeconds) > 0.001 ||
            probe.Format is not { IsSupported: true } format ||
            probe.Width is <= 0 or > 8192 || probe.Height is <= 0 or > 8192 ||
            (probe.Width & 1) != 0 || (probe.Height & 1) != 0 ||
            !VideoDedupSourceFormat.TryReadRatio(format.NominalFrameRate, '/', out var fpsNumerator, out var fpsDenominator) ||
            Math.Abs((double)fpsNumerator / fpsDenominator - probe.AverageFramesPerSecond) > 0.001 ||
            !VideoDedupSourceFormat.TryReadRatio(format.SampleAspectRatio, ':', out var sarNumerator, out var sarDenominator))
            return null;

        var frame = ResolveUploadFrame(probe.Width, probe.Height, preset);
        if (frame.Width is <= 0 or > 8192 || frame.Height is <= 0 or > 8192)
            return null;
        var numerator = (long)sarNumerator * probe.Width * frame.Height;
        var denominator = (long)sarDenominator * probe.Height * frame.Width;
        var divisor = GreatestCommonDivisor(numerator, denominator);
        if (divisor <= 0 || numerator / divisor > 100000 || denominator / divisor > 100000)
            return null;
        return new(frame, FormattableString.Invariant($"{numerator / divisor}:{denominator / divisor}"));
    }

    public static IReadOnlyList<string> BuildHardwareArguments(
        VideoDedupPreset preset, VideoDedupClip clip, VideoDedupSlice slice, HardwareFrame frame,
        VideoDedupHardwareDevice device, string encoder, string? outputPath, int sampleFrames = 48)
    {
        var args = new List<string>
        {
            "-y", "-hide_banner", "-nostdin", "-loglevel", "error", "-filter_threads", "1",
        };
        device.AppendDevice(args);
        device.AppendDecode(args);
        args.AddRange(["-i", clip.Path, "-map", "0:v:0"]);
        var filters = new List<string>();
        if (frame.Size.Scaled)
            filters.Add(device.Scale(frame.Size.Width, frame.Size.Height));
        filters.Add("setsar=sar=" + frame.AspectRatio.Replace(':', '/') + ":max=100000");
        filters.Add("setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709");
        AppendFrameRateFilters(filters, preset);
        args.AddRange(["-vf", string.Join(',', filters)]);

        var sample = outputPath is null;
        var hasAudio = !sample && clip.Probe.HasAudio;
        if (hasAudio)
        {
            args.AddRange(["-map", "0:a:0"]);
            AppendSingleAudio(args, clip, slice, preset, 0);
        }
        else
            args.Add("-an");

        args.AddRange(["-threads:v", "1", "-color_range", "tv", "-colorspace", "bt709",
            "-color_primaries", "bt709", "-color_trc", "bt709"]);
        AppendEncoder(args, preset, encoder, hasAudio, sourceAudioBitrateKbps: clip.Probe.AudioBitrateKbps,
            hardwareFrames: true);
        if (sample)
            args.AddRange(["-frames:v", sampleFrames.ToString(CultureInfo.InvariantCulture), "-f", "null", "-"]);
        else
            args.AddRange(["-movflags", "+faststart", outputPath!]);
        return args;
    }

    private static long GreatestCommonDivisor(long a, long b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }
}
