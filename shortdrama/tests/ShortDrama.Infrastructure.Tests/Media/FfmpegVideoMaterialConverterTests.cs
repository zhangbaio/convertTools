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
        var configPath = Path.Combine(projectDir.FullName, "config.txt");

        await File.WriteAllTextAsync(configPath, """
MaterialConvertEnabled=true
MaterialTrimHeadSeconds=4
MaterialTrimTailSeconds=2
MaterialSpeedPercent=10
MaterialDropEveryNFrames=20
MaterialDropCount=1
MaterialCropWidthPercent=2
MaterialCropHeightPercent=2
VideoBitrateBps=5000000
VideoBitrateMode=Cbr
VideoAudioBitrateBps=96000
VideoFps=30
VideoUseHardwareEncoder=false
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
