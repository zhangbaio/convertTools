using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokVideoFrameRepairServiceTests
{
    [Theory]
    [InlineData(1080, 1872, 1080, 1920)]
    [InlineData(1080, 1964, 1080, 1920)]
    [InlineData(2000, 1080, 1920, 1080)]
    public void Off_ratio_frames_target_the_standard_size(int width, int height, int targetWidth, int targetHeight)
    {
        TikTokVideoFrameRepairService.ResolveTargetFrame(width, height).Should().Be((targetWidth, targetHeight));
    }

    [Theory]
    [InlineData(1080, 1920)]
    [InlineData(1080, 1922)]
    [InlineData(720, 1280)]
    [InlineData(1920, 1080)]
    public void Frames_within_tolerance_are_left_alone(int width, int height)
    {
        TikTokVideoFrameRepairService.ResolveTargetFrame(width, height).Should().BeNull();
    }

    [Fact]
    public void Ffmpeg_arguments_scale_and_pad_without_stretching()
    {
        var arguments = TikTokVideoFrameRepairService.BuildArguments("in.mp4", "out.mp4", (1080, 1920), copyAudio: true);

        arguments.Should().ContainInOrder("-i", "in.mp4");
        arguments.Should().Contain(
            "scale=1080:1920:force_original_aspect_ratio=decrease,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,format=yuv420p");
        arguments.Should().ContainInOrder("-c:a", "copy");
        arguments[^1].Should().Be("out.mp4");
        TikTokVideoFrameRepairService.BuildArguments("in.mp4", "out.mp4", (1080, 1920), copyAudio: false)
            .Should().ContainInOrder("-c:a", "aac");
    }

    [Fact]
    public async Task Repair_rewrites_the_episode_backs_up_the_original_and_refreshes_dedup_record()
    {
        var project = Path.Combine(Path.GetTempPath(), "frame-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        try
        {
            var video = Path.Combine(project, "10.mp4");
            await FfmpegRunner.RunAsync(FfmpegLocator.ResolveFfmpeg(),
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "color=c=blue:s=1080x1872:r=5:d=1",
                "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo",
                "-shortest", "-c:v", "libx264", "-c:a", "aac", video,
            ], CancellationToken.None);
            var originalLength = new FileInfo(video).Length;
            var fingerprintPath = Path.Combine(project, ProjectVideoResolver.SourceOriginalsDirectoryName, "dedup-fingerprint.json");
            Directory.CreateDirectory(Path.GetDirectoryName(fingerprintPath)!);
            File.WriteAllText(fingerprintPath,
                JsonSerializer.Serialize(new { Preset = "p", Outputs = new[] { new { Path = "10.mp4", Length = originalLength } } }));

            TikTokSmallVideoRepairService.NeedsRepair(project).Should().BeTrue();
            var logs = new List<string>();
            var before = await TikTokVideoFrameRepairService.RepairInPlaceAsync(video, project, logs.Add, CancellationToken.None);

            before.Should().Be("1080×1872");
            var probe = await MediaProbe.ProbeAsync(MediaBinaryResolver.ResolveFfprobe(), video, CancellationToken.None);
            (probe.Width, probe.Height).Should().Be((1080, 1920));
            probe.AudioCodec.Should().NotBeEmpty();
            File.Exists(Path.Combine(project, TikTokVideoFrameRepairService.BackupDirectoryName, "10.mp4")).Should().BeTrue();
            ProjectVideoResolver.ResolveSourceVideos(project).Should().ContainSingle();
            TikTokVideoFrameRepairService.NeedsRepair(video).Should().BeFalse();
            logs.Should().ContainSingle(line => line.Contains("1080×1872 → 1080×1920"));

            using var fingerprint = JsonDocument.Parse(File.ReadAllText(fingerprintPath));
            fingerprint.RootElement.GetProperty("Outputs")[0].GetProperty("Length").GetInt64()
                .Should().Be(new FileInfo(video).Length);
        }
        finally
        {
            try { Directory.Delete(project, recursive: true); } catch { }
        }
    }
}
