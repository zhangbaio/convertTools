using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.VideoDedup;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public sealed class VideoDedupSourceBitrateTests
{
    [Fact]
    public void ACompletedPresetCopyAndSavedAccountKeepTheSourceSelection()
    {
        var preset = JsonSerializer.Deserialize<VideoDedupPreset>("""{"VideoBitrateMbps":2.2}""")!;
        Assert.False(preset.UseSourceVideoBitrate);
        var manualFingerprint = preset.Fingerprint();
        preset.UseSourceVideoBitrate = true;
        var restored = JsonSerializer.Deserialize<VideoDedupPreset>(JsonSerializer.Serialize(preset))!;
        Assert.True(restored.Clone().UseSourceVideoBitrate);
        Assert.Equal(2.2, restored.VideoBitrateMbps);
        Assert.NotEqual(manualFingerprint, restored.Fingerprint());
        restored.UseSourceVideoBitrate = false;
        Assert.Equal(manualFingerprint, restored.Fingerprint());
    }

    [Fact]
    public void ProbeReadsOnlyTheVideoStreamRateForTheVideoTarget()
    {
        var probe = VideoDedupService.ReadProbeJson("""
            {"format":{"duration":"10","bit_rate":"3100000"},"streams":[
                {"codec_type":"video","width":1080,"height":1920,"bit_rate":"2400000"},
                {"codec_type":"audio","bit_rate":"96000"}
            ]}
            """);
        var missing = VideoDedupService.ReadProbeJson("""
            {"format":{"duration":"10","bit_rate":"3100000"},"streams":[
                {"codec_type":"video","width":1080,"height":1920,"bit_rate":"N/A"},
                {"codec_type":"audio","bit_rate":"96000"}
            ]}
            """);
        Assert.Equal(2_400_000, probe.VideoBitrateBps);
        Assert.Equal(96, probe.AudioBitrateKbps);
        Assert.Equal(0, missing.VideoBitrateBps);
    }

    [Fact]
    public void JoinedEpisodesWeightByUsedDurationAndUseManualRateForMissingStreams()
    {
        var preset = new VideoDedupPreset { UseSourceVideoBitrate = true, VideoBitrateMbps = 2.2 };
        var first = new VideoDedupClip("first.mp4", new VideoDedupProbe(10, 1080, 1920, false, VideoBitrateBps: 4_000_000));
        var second = new VideoDedupClip("second.mp4", new VideoDedupProbe(10, 1080, 1920, false));
        var slices = new[] { new VideoDedupSlice(0, 0, 3), new VideoDedupSlice(1, 0, 7) };
        var (target, fallback) = VideoDedupPlanner.ResolveVideoBitrate(preset, [first, second], slices);
        Assert.Equal(2.74, target, 6);
        Assert.True(fallback);
        var args = VideoDedupPlanner.BuildArguments(preset, [first, second], slices, "out.mp4", "libx264", 1);
        Assert.Equal("2740k", ValueAfter(args, "-b:v"));
    }

    [Theory]
    [InlineData("nvenc")]
    [InlineData("amf")]
    [InlineData("qsv")]
    public void HardwareAndCpuUseTheSameSourceTargetAndCrfIgnoresIt(string backend)
    {
        var preset = new VideoDedupPreset { UseSourceVideoBitrate = true, BitrateMode = VideoDedupPreset.BitrateCbr };
        var format = new VideoDedupSourceFormat(
            "h264", "Main", "yuv420p", "1:1", "tv", "bt709", "bt709", "bt709", "progressive", "30/1", false, 1, 0);
        var clip = new VideoDedupClip("episode.mp4", new VideoDedupProbe(20, 1080, 1920, false,
            AverageFramesPerSecond: 30, Format: format, VideoBitrateBps: 275_000));
        var slice = new VideoDedupSlice(0, 0, 20);
        var frame = VideoDedupPlanner.GetHardwareFrame(preset, [clip], [slice])!.Value;
        var device = new VideoDedupHardwareDevice("GPU", "id", "model", backend, 0);
        var gpu = VideoDedupPlanner.BuildHardwareArguments(preset, clip, slice, frame, device, device.Encoder(false), "gpu.mp4");
        var cpu = VideoDedupPlanner.BuildArguments(preset, [clip], [slice], "cpu.mp4", "libx264", 1);
        Assert.Equal("275k", ValueAfter(gpu, "-b:v"));
        Assert.Equal("275k", ValueAfter(cpu, "-b:v"));
        Assert.Equal("275k", ValueAfter(gpu, "-maxrate"));

        preset.BitrateMode = VideoDedupPreset.BitrateCrf;
        var crf = VideoDedupPlanner.BuildArguments(preset, [clip], [slice], "crf.mp4", "libx264", 1);
        Assert.DoesNotContain("-b:v", crf);
        Assert.Equal("23", ValueAfter(crf, "-crf"));
    }

    private static string ValueAfter(IReadOnlyList<string> args, string option) =>
        args.Zip(args.Skip(1)).Single(pair => pair.First == option).Second;
}
