using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Core.Tests;

public sealed class VideoDedupHardwareTests
{
    private static readonly VideoDedupSourceFormat SourceFormat = new(
        "hevc", "Main", "yuv420p", "1:1", "tv", "bt709", "bt709", "bt709",
        "progressive", "30/1", false, 1, 1);

    [Theory]
    [InlineData("amf", "amf", "amf=dedup_gpu@dedup_dx", "vpp_amf")]
    [InlineData("nvenc", "cuda", "cuda=dedup_gpu:2", "scale_cuda")]
    [InlineData("qsv", "qsv", "qsv=dedup_gpu:hw,child_device=2,child_device_type=d3d11va", "scale_qsv")]
    public void Builds_a_bound_hardware_pipeline_with_the_expected_display_aspect_ratio(
        string backend, string decode, string deviceArgument, string filter)
    {
        var preset = new VideoDedupPreset { PortraitWidth = 1080, PortraitHeight = 1920 };
        var clip = Clip(SourceFormat);
        var slice = new VideoDedupSlice(0, 0, 20);
        var device = new VideoDedupHardwareDevice("GPU", "luid", "model", backend, 2);
        var frame = VideoDedupPlanner.GetHardwareFrame(preset, [clip], [slice]);
        frame.Should().NotBeNull();
        frame!.Value.AspectRatio.Should().Be("1:1");

        var args = VideoDedupPlanner.BuildHardwareArguments(preset, clip, slice, frame.Value,
            device, device.Encoder(hevc: false), "result.mp4").ToList();
        args.Should().Contain(deviceArgument).And.NotContain("-pix_fmt");
        args[args.IndexOf("-hwaccel") + 1].Should().Be(decode);
        args[args.IndexOf("-hwaccel_device") + 1].Should().Be("dedup_gpu");
        args[args.IndexOf("-c:v") + 1].Should().Be("h264_" + backend);
        args[args.IndexOf("-vf") + 1].Should().StartWith(filter)
            .And.Contain("setsar=sar=1/1").And.Contain("setparams=range=tv");
        args.Should().Contain("0:a:0").And.Contain("aac");
        args[^1].Should().Be("result.mp4");
    }

    [Fact]
    public void Uses_compatible_filters_for_edits_higher_bit_depth_and_uncertain_timestamps()
    {
        var simple = new VideoDedupPreset();
        var clip = Clip(SourceFormat);
        var whole = new VideoDedupSlice(0, 0, 20);
        var altered = new VideoDedupPreset { Effects = new VideoDedupEffects { FrameMixEnabled = true } };
        var forcedCpu = new VideoDedupPreset { EncodingPreference = VideoDedupPreset.EncodingSoftwareOnly };
        VideoDedupPlanner.GetHardwareFrame(simple, [clip], [whole, whole]).Should().BeNull();
        VideoDedupPlanner.GetHardwareFrame(simple, [clip], [whole with { StartSeconds = 2 }]).Should().BeNull();
        VideoDedupPlanner.GetHardwareFrame(altered, [clip], [whole]).Should().BeNull();
        VideoDedupPlanner.GetHardwareFrame(forcedCpu, [clip], [whole]).Should().BeNull();
        VideoDedupPlanner.GetHardwareFrame(simple, [Clip(SourceFormat with { PixelFormat = "yuv420p10le" })], [whole]).Should().BeNull();
        VideoDedupPlanner.GetHardwareFrame(simple, [Clip(SourceFormat with { HasDisplayTransform = true })], [whole]).Should().BeNull();
        VideoDedupPlanner.GetHardwareFrame(simple, [Clip(SourceFormat with { NominalFrameRate = "60/1" })], [whole]).Should().BeNull();
        VideoDedupPlanner.GetHardwareFrame(simple, [Clip(SourceFormat with { AudioStreams = 2 })], [whole]).Should().BeNull();
    }

    [Fact]
    public void Compatible_chain_uploads_the_filtered_frames_to_the_selected_device()
    {
        var device = new VideoDedupHardwareDevice("Radeon", "luid", "model", "amf", 2);
        var args = VideoDedupPlanner.BuildArguments(new VideoDedupPreset(), [Clip(SourceFormat)],
            [new VideoDedupSlice(0, 0, 20)], "result.mp4", "h264_amf", 0, device).ToList();
        args.Should().NotContain("-hwaccel").And.NotContain("-pix_fmt").And.Contain("d3d11va=dedup_dx:2");
        args[args.IndexOf("-vf") + 1].Should().EndWith("format=nv12,hwupload");
    }

    [Fact]
    public void Calibration_requires_a_material_throughput_gain_and_resource_headroom()
    {
        VideoDedupHardwareTranscoder.Recommend([new(1, 41.82), new(2, 42.31)]).Should().Be(1);
        VideoDedupHardwareTranscoder.Recommend([new(1, 40), new(2, 33), new(3, 25, 90, false)]).Should().Be(2);
        VideoDedupHardwareTranscoder.Recommend([new(1, 40), new(2, 34), new(3, 30)]).Should().Be(3);
    }

    [Theory]
    [InlineData("libx264")]
    [InlineData("libx265")]
    public void Calibration_creates_its_own_standard_video(string encoder)
    {
        var args = VideoDedupCalibrationSample.BuildArguments(encoder, "sample.mp4");
        args.Should().Contain("-n").And.Contain("lavfi").And.Contain("yuv420p").And.Contain(encoder);
        args.Should().Contain(argument => argument.StartsWith("testsrc2="));
        args[^1].Should().Be("sample.mp4");
    }

    [Fact]
    public void Calibration_only_covers_jobs_within_the_tested_workload()
    {
        var preset = VideoDedupCalibrationSample.CreatePreset();
        var clip = Clip(SourceFormat);
        var frame = VideoDedupPlanner.GetHardwareFrame(preset, [clip], [new(0, 0, 20)])!.Value;

        VideoDedupCalibrationSample.Covers(clip.Probe, frame, preset).Should().BeTrue();
        VideoDedupCalibrationSample.Covers(
            clip.Probe with { Width = 1922, Height = 1080 }, frame, preset).Should().BeTrue();
        VideoDedupCalibrationSample.Covers(
            clip.Probe with { AverageFramesPerSecond = 60 }, frame, preset).Should().BeFalse();
        VideoDedupCalibrationSample.Covers(clip.Probe, frame with { Size = new(2160, 3840, true) }, preset).Should().BeFalse();
        preset.VideoCodec = VideoDedupPreset.CodecHevc;
        VideoDedupCalibrationSample.Covers(clip.Probe, frame, preset).Should().BeFalse();
    }

    [Fact]
    public async Task Render_failure_tries_same_card_with_software_filters_and_validates_output()
    {
        var preset = new VideoDedupPreset();
        var clip = Clip(SourceFormat);
        var slice = new VideoDedupSlice(0, 0, 20);
        var device = new VideoDedupHardwareDevice("Radeon", Guid.NewGuid().ToString(), "model", "amf", 1);
        var transcoder = new VideoDedupHardwareTranscoder(preset, [device], "ffmpeg.exe");
        var frame = VideoDedupPlanner.GetHardwareFrame(preset, [clip], [slice])!.Value;
        var invocations = new List<IReadOnlyList<string>>();
        var validations = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var done = await transcoder.TryRenderAsync(clip, slice, frame, "out.mp4", false, 2,
            (args, _, _) =>
            {
                invocations.Add(args);
                if (args[^1] == "out.mp4" && args.Contains("-hwaccel"))
                    throw new InvalidOperationException("Error reinitializing filters");
                return Task.FromResult(0.5);
            },
            _ => { validations++; return Task.FromResult(true); }, _ => { }, timeout.Token);

        done.Should().BeTrue();
        validations.Should().Be(1);
        invocations.Should().HaveCount(3);
        invocations[0].Should().Contain("-frames:v");
        invocations[1].Should().Contain("-hwaccel");
        invocations[2].Should().NotContain("-hwaccel").And.Contain("d3d11va=dedup_dx:1").And.Contain("h264_amf");
    }

    private static VideoDedupClip Clip(VideoDedupSourceFormat format) =>
        new("source.mp4", new VideoDedupProbe(20, 1080, 1922, true, 96, 30, format));
}
