using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Media;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Media;

public sealed class FfmpegVideoMaterialConverterTests
{
    [Fact]
    public async Task ConvertAsync_Should_Skip_Existing_Output_When_Not_Overwrite()
    {
        var inputDir = Directory.CreateTempSubdirectory();
        var outputDir = Directory.CreateTempSubdirectory();
        var projectDir = Directory.CreateTempSubdirectory();

        var inputPath = Path.Combine(inputDir.FullName, "episode01.mp4");
        var outputPath = Path.Combine(outputDir.FullName, "episode01.mp4");
        await File.WriteAllBytesAsync(inputPath, [1, 2, 3]);
        await File.WriteAllBytesAsync(outputPath, [4, 5, 6]);

        var runner = new RecordingProcessRunner();
        var converter = new FfmpegVideoMaterialConverter(
            runner,
            NullLogger<FfmpegVideoMaterialConverter>.Instance);

        var result = await converter.ConvertAsync(
            new VideoMaterialConvertRequest(projectDir.FullName, inputDir.FullName, outputDir.FullName, Overwrite: false),
            progress: null,
            CancellationToken.None);

        result.TotalFiles.Should().Be(1);
        result.ConvertedFiles.Should().Be(0);
        result.SkippedFiles.Should().Be(1);
        result.FailedFiles.Should().Be(0);
        runner.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task ConvertAsync_Should_Use_Strict_Frame_Select_Filter()
    {
        var inputDir = Directory.CreateTempSubdirectory();
        var outputDir = Directory.CreateTempSubdirectory();
        var projectDir = Directory.CreateTempSubdirectory();
        var configPath = Path.Combine(projectDir.FullName, "config.json");

        await File.WriteAllTextAsync(configPath, """
{
  "materialTranscode": {
    "enabled": true,
    "trimHeadSeconds": 4,
    "trimTailSeconds": 2,
    "speedPercent": 10,
    "frameSamplingEnabled": true,
    "frameSamplingMode": "fixed_interval",
    "frameSamplingInterval": 20,
    "cropWidthPercent": 2,
    "cropHeightPercent": 2
  },
  "video": {
    "bitrateBps": 5000000,
    "bitrateMode": "Cbr",
    "audioBitrateBps": 96000,
    "fps": 30,
    "useHardwareEncoder": false
  }
}
""");

        var inputPath = Path.Combine(inputDir.FullName, "episode01.mp4");
        var outputPath = Path.Combine(outputDir.FullName, "episode01.mp4");
        await File.WriteAllBytesAsync(inputPath, [1, 2, 3]);

        var runner = new ScriptedProcessRunner(new Dictionary<string, ProbeScenario>(StringComparer.Ordinal)
        {
            [inputPath] = new ProbeScenario(DurationSeconds: 95.5d, Width: 1080, Height: 1920, VideoBitrateBps: 5_500_000, AudioBitrateBps: 128_000),
            [outputPath] = new ProbeScenario(DurationSeconds: 80d, Width: 1058, Height: 1882, VideoBitrateBps: 5_000_000, AudioBitrateBps: 96_000)
        });

        var converter = new FfmpegVideoMaterialConverter(
            runner,
            NullLogger<FfmpegVideoMaterialConverter>.Instance);

        var result = await converter.ConvertAsync(
            new VideoMaterialConvertRequest(projectDir.FullName, inputDir.FullName, outputDir.FullName, configPath, Overwrite: true),
            progress: null,
            CancellationToken.None);

        result.ConvertedFiles.Should().Be(1);
        runner.FfmpegInvocations.Should().ContainSingle();
        var args = runner.FfmpegInvocations[0];
        args.Should().Contain(arg => arg.Contains("select='not(eq(mod(n\\,20)\\,19))'", StringComparison.Ordinal));
        args.Should().Contain(arg => arg.Contains("setpts=N/(30*TB)", StringComparison.Ordinal));
        args.Should().Contain(arg => arg.Contains("atempo=1.157895", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConvertAsync_Should_Apply_DynamicSpeed_PortraitLayout_And_Watermark()
    {
        var inputDir = Directory.CreateTempSubdirectory();
        var outputDir = Directory.CreateTempSubdirectory();
        var projectDir = Directory.CreateTempSubdirectory();
        var configPath = Path.Combine(projectDir.FullName, "config.json");

        await File.WriteAllTextAsync(configPath, """
{
  "materialTranscode": {
    "enabled": true,
    "trimHeadSeconds": 0.5,
    "trimTailSeconds": 0.5,
    "speedPercent": 0,
    "dynamicSpeedEnabled": true,
    "dynamicSpeedPresetName": "light_rhythm",
    "dynamicSpeedHeadSeconds": 2.5,
    "dynamicSpeedHeadPercent": 8,
    "dynamicSpeedMiddlePercent": 6,
    "dynamicSpeedTailSeconds": 2.5,
    "dynamicSpeedTailPercent": 8,
    "frameSamplingEnabled": true,
    "frameSamplingMode": "fixed_interval",
    "frameSamplingInterval": 20,
    "cropWidthPercent": 0,
    "cropHeightPercent": 0,
    "foregroundZoomPercent": 7,
    "watermarkEnabled": true,
    "watermarkText": "TEST",
    "watermarkFontSize": 35,
    "watermarkPosition": "top_right",
    "watermarkMarginX": 30,
    "watermarkMarginY": 30,
    "outputWidth": 1080,
    "outputHeight": 1920,
    "pipWidthPercent": 80,
    "pipHeightPercent": 70
  },
  "video": {
    "bitrateBps": 5000000,
    "bitrateMode": "Cbr",
    "audioBitrateBps": 96000,
    "fps": 30,
    "useHardwareEncoder": false
  }
}
""");

        var inputPath = Path.Combine(inputDir.FullName, "episode01.mp4");
        var outputPath = Path.Combine(outputDir.FullName, "episode01.mp4");
        await File.WriteAllBytesAsync(inputPath, [1, 2, 3]);

        var runner = new ScriptedProcessRunner(new Dictionary<string, ProbeScenario>(StringComparer.Ordinal)
        {
            [inputPath] = new ProbeScenario(DurationSeconds: 90d, Width: 1920, Height: 1080, VideoBitrateBps: 5_500_000, AudioBitrateBps: 128_000),
            [outputPath] = new ProbeScenario(DurationSeconds: 80d, Width: 1080, Height: 1920, VideoBitrateBps: 5_000_000, AudioBitrateBps: 96_000)
        });

        var converter = new FfmpegVideoMaterialConverter(
            runner,
            NullLogger<FfmpegVideoMaterialConverter>.Instance);

        var result = await converter.ConvertAsync(
            new VideoMaterialConvertRequest(projectDir.FullName, inputDir.FullName, outputDir.FullName, configPath, Overwrite: true),
            progress: null,
            CancellationToken.None);

        result.ConvertedFiles.Should().Be(1);
        runner.FfmpegInvocations.Should().ContainSingle();
        var args = runner.FfmpegInvocations[0];
        var filterComplex = args.SkipWhile(arg => !string.Equals(arg, "-filter_complex", StringComparison.Ordinal))
            .Skip(1)
            .First();

        filterComplex.Should().Contain("concat=n=3:v=1:a=1[vbase][abase]");
        filterComplex.Should().Contain("crop=864:1344");
        filterComplex.Should().Contain("pad=1080:1920:(ow-iw)/2:(oh-ih)/2:color=black");
        filterComplex.Should().Contain("drawtext=text='TEST'");
        filterComplex.Should().Contain("setpts=N/(30*TB)");
    }

    [Fact]
    public async Task ConvertAsync_Should_Apply_Dedup_Filters_From_Json()
    {
        var inputDir = Directory.CreateTempSubdirectory();
        var outputDir = Directory.CreateTempSubdirectory();
        var projectDir = Directory.CreateTempSubdirectory();
        var configPath = Path.Combine(projectDir.FullName, "config.json");

        await File.WriteAllTextAsync(configPath, """
{
  "materialTranscode": {
    "enabled": true,
    "trimHeadSeconds": 0,
    "trimTailSeconds": 0,
    "frameSamplingEnabled": false,
    "dedupEnabled": true,
    "dedupColorEnabled": true,
    "dedupNoiseEnabled": true,
    "dedupAudioEnabled": true,
    "dedupMetadataEnabled": true,
    "dedupRotateEnabled": true,
    "dedupVignetteEnabled": true,
    "dedupFadeInEnabled": true
  },
  "video": {
    "bitrateBps": 5000000,
    "bitrateMode": "Cbr",
    "audioBitrateBps": 96000,
    "fps": 30,
    "useHardwareEncoder": false
  }
}
""");

        var inputPath = Path.Combine(inputDir.FullName, "episode01.mp4");
        var outputPath = Path.Combine(outputDir.FullName, "episode01.mp4");
        await File.WriteAllBytesAsync(inputPath, [1, 2, 3]);

        var runner = new ScriptedProcessRunner(new Dictionary<string, ProbeScenario>(StringComparer.Ordinal)
        {
            [inputPath] = new ProbeScenario(DurationSeconds: 90d, Width: 1080, Height: 1920, VideoBitrateBps: 5_500_000, AudioBitrateBps: 128_000),
            [outputPath] = new ProbeScenario(DurationSeconds: 90d, Width: 1080, Height: 1920, VideoBitrateBps: 5_000_000, AudioBitrateBps: 96_000)
        });

        var converter = new FfmpegVideoMaterialConverter(
            runner,
            NullLogger<FfmpegVideoMaterialConverter>.Instance);

        var result = await converter.ConvertAsync(
            new VideoMaterialConvertRequest(projectDir.FullName, inputDir.FullName, outputDir.FullName, configPath, Overwrite: true),
            progress: null,
            CancellationToken.None);

        result.ConvertedFiles.Should().Be(1);
        runner.FfmpegInvocations.Should().ContainSingle();
        var args = runner.FfmpegInvocations[0];
        var filterComplex = args.SkipWhile(arg => !string.Equals(arg, "-filter_complex", StringComparison.Ordinal))
            .Skip(1)
            .First();

        filterComplex.Should().Contain("eq=brightness=");
        filterComplex.Should().Contain("noise=alls=1.2");
        filterComplex.Should().Contain("rotate=");
        filterComplex.Should().Contain("vignette=PI/8");
        filterComplex.Should().Contain("fade=t=in:st=0:d=0.25");
        filterComplex.Should().Contain("volume=0.998");
        args.Should().ContainInOrder("-map_metadata", "-1");
    }

    private sealed class RecordingProcessRunner : IExternalProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory)> Invocations { get; } = [];

        public Task<ExternalProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            CancellationToken cancellationToken)
        {
            Invocations.Add((fileName, arguments, workingDirectory));
            return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed record ProbeScenario(
        double DurationSeconds,
        int Width,
        int Height,
        long VideoBitrateBps,
        int AudioBitrateBps);

    private sealed class ScriptedProcessRunner : IExternalProcessRunner
    {
        private readonly IReadOnlyDictionary<string, ProbeScenario> _probes;

        public ScriptedProcessRunner(IReadOnlyDictionary<string, ProbeScenario> probes)
        {
            _probes = probes;
        }

        public List<IReadOnlyList<string>> FfmpegInvocations { get; } = [];

        public Task<ExternalProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            CancellationToken cancellationToken)
        {
            if (fileName.Contains("ffprobe", StringComparison.OrdinalIgnoreCase))
            {
                var path = arguments[^1];
                var probe = _probes[path];
                var totalBitrate = probe.VideoBitrateBps + probe.AudioBitrateBps;

                return Task.FromResult(new ExternalProcessResult(
                    0,
                    $$"""
                    {
                      "streams": [
                        {
                          "codec_type": "video",
                          "width": {{probe.Width}},
                          "height": {{probe.Height}},
                          "bit_rate": "{{probe.VideoBitrateBps}}"
                        },
                        {
                          "codec_type": "audio",
                          "bit_rate": "{{probe.AudioBitrateBps}}"
                        }
                      ],
                      "format": {
                        "duration": "{{probe.DurationSeconds}}",
                        "bit_rate": "{{totalBitrate}}"
                      }
                    }
                    """,
                    string.Empty));
            }

            if (fileName.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                var outputPath = arguments[^1];
                FfmpegInvocations.Add(arguments);
                File.WriteAllBytes(outputPath, [9, 8, 7]);
                return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
            }

            throw new InvalidOperationException($"Unexpected process: {fileName}");
        }
    }
}
