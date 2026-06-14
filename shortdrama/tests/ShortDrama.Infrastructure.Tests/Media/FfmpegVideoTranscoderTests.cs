using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Media;
using ShortDrama.Infrastructure.Parsing;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Media;

public sealed class FfmpegVideoTranscoderTests
{
    [Fact]
    public async Task TranscodeAsync_Should_Skip_Existing_Output_When_Not_Overwrite()
    {
        var projectDir = Directory.CreateTempSubdirectory();
        var inputDir = Directory.CreateTempSubdirectory();
        var outputDir = Directory.CreateTempSubdirectory();

        var inputPath = Path.Combine(inputDir.FullName, "episode01.mov");
        var outputPath = Path.Combine(outputDir.FullName, "末世禁区求生局-第1集.mp4");

        await File.WriteAllTextAsync(Path.Combine(projectDir.FullName, "短剧信息.txt"), """
原剧名: 原剧名
新剧名: 末世禁区求生局
时长: 8 分钟
集数: 3
成本: 1 万元
制作公司: 湖北云漫科技有限公司
""");

        await File.WriteAllBytesAsync(inputPath, [1, 2, 3]);
        await File.WriteAllBytesAsync(outputPath, [4, 5, 6]);

        var runner = new RecordingProcessRunner();
        var transcoder = new FfmpegVideoTranscoder(
            new TxtProjectInfoParser(),
            runner,
            NullLogger<FfmpegVideoTranscoder>.Instance);

        var result = await transcoder.TranscodeAsync(
            new VideoTranscodeRequest(projectDir.FullName, inputDir.FullName, outputDir.FullName, Overwrite: false),
            progress: null,
            CancellationToken.None);

        result.TotalFiles.Should().Be(1);
        result.TranscodedFiles.Should().Be(0);
        result.SkippedFiles.Should().Be(1);
        result.FailedFiles.Should().Be(0);
        result.Outputs.Should().ContainSingle().Which.Should().Be(outputPath);
        result.Failures.Should().BeEmpty();
        runner.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task TranscodeAsync_Should_Continue_When_One_File_Fails_Validation()
    {
        var projectDir = Directory.CreateTempSubdirectory();
        var inputDir = Directory.CreateTempSubdirectory();
        var outputDir = Directory.CreateTempSubdirectory();

        await File.WriteAllTextAsync(Path.Combine(projectDir.FullName, "短剧信息.txt"), """
原剧名: 原剧名
新剧名: 流水席涨价后
时长: 8 分钟
集数: 2
成本: 1 万元
制作公司: 湖北云漫科技有限公司
""");

        var inputPath1 = Path.Combine(inputDir.FullName, "episode01.mov");
        var inputPath2 = Path.Combine(inputDir.FullName, "episode02.mov");
        await File.WriteAllBytesAsync(inputPath1, [1, 2, 3]);
        await File.WriteAllBytesAsync(inputPath2, [4, 5, 6]);

        var outputPath1 = Path.Combine(outputDir.FullName, "流水席涨价后-第1集.mp4");
        var outputPath2 = Path.Combine(outputDir.FullName, "流水席涨价后-第2集.mp4");

        var runner = new ScriptedProcessRunner(new Dictionary<string, ProbeScenario>(StringComparer.Ordinal)
        {
            [inputPath1] = new ProbeScenario(DurationSeconds: 28d, Width: 1080, Height: 1920, VideoBitrateBps: 5_500_000, AudioBitrateBps: 128_000),
            [outputPath1] = new ProbeScenario(DurationSeconds: 30d, Width: 1080, Height: 1920, VideoBitrateBps: 5_500_000, AudioBitrateBps: 128_000),
            [inputPath2] = new ProbeScenario(DurationSeconds: 35d, Width: 1080, Height: 1920, VideoBitrateBps: 5_500_000, AudioBitrateBps: 128_000),
            [outputPath2] = new ProbeScenario(DurationSeconds: 35d, Width: 1080, Height: 1920, VideoBitrateBps: 5_500_000, AudioBitrateBps: 128_000)
        });

        var transcoder = new FfmpegVideoTranscoder(
            new TxtProjectInfoParser(),
            runner,
            NullLogger<FfmpegVideoTranscoder>.Instance);

        var result = await transcoder.TranscodeAsync(
            new VideoTranscodeRequest(projectDir.FullName, inputDir.FullName, outputDir.FullName, Overwrite: true),
            progress: null,
            CancellationToken.None);

        result.TotalFiles.Should().Be(2);
        result.TranscodedFiles.Should().Be(1);
        result.SkippedFiles.Should().Be(0);
        result.FailedFiles.Should().Be(1);
        result.Outputs.Should().ContainSingle().Which.Should().Be(outputPath2);
        result.Failures.Should().ContainSingle();
        result.Failures[0].InputPath.Should().Be(inputPath1);
        result.Failures[0].Message.Should().Contain("源视频和转码后有效时长均低于 31 秒");
        File.Exists(outputPath1).Should().BeFalse();
        File.Exists(outputPath2).Should().BeTrue();
    }

    [Fact]
    public async Task TranscodeAsync_Should_Report_Corrupt_Source_When_InputDuration_Looks_Normal_But_Output_Is_Short()
    {
        var projectDir = Directory.CreateTempSubdirectory();
        var inputDir = Directory.CreateTempSubdirectory();
        var outputDir = Directory.CreateTempSubdirectory();

        await File.WriteAllTextAsync(Path.Combine(projectDir.FullName, "短剧信息.txt"), """
原剧名: 原剧名
新剧名: 容器时长正常
时长: 8 分钟
集数: 1
成本: 1 万元
制作公司: 湖北云漫科技有限公司
""");

        var inputPath = Path.Combine(inputDir.FullName, "episode01.mov");
        await File.WriteAllBytesAsync(inputPath, [1, 2, 3]);

        var outputPath = Path.Combine(outputDir.FullName, "容器时长正常-第1集.mp4");

        var runner = new ScriptedProcessRunner(new Dictionary<string, ProbeScenario>(StringComparer.Ordinal)
        {
            [inputPath] = new ProbeScenario(DurationSeconds: 95.5d, Width: 1080, Height: 1920, VideoBitrateBps: 5_500_000, AudioBitrateBps: 128_000),
            [outputPath] = new ProbeScenario(DurationSeconds: 12.3d, Width: 1080, Height: 1920, VideoBitrateBps: 5_500_000, AudioBitrateBps: 128_000)
        });

        var transcoder = new FfmpegVideoTranscoder(
            new TxtProjectInfoParser(),
            runner,
            NullLogger<FfmpegVideoTranscoder>.Instance);

        var result = await transcoder.TranscodeAsync(
            new VideoTranscodeRequest(projectDir.FullName, inputDir.FullName, outputDir.FullName, Overwrite: true),
            progress: null,
            CancellationToken.None);

        result.FailedFiles.Should().Be(1);
        result.Failures.Should().ContainSingle();
        result.Failures[0].Message.Should().Contain("源视频容器时长约 95.5 秒，但转码后仅得到 12.3 秒有效内容");
        File.Exists(outputPath).Should().BeFalse();
    }

    [Fact]
    public async Task TranscodeAsync_Should_Process_Files_In_Episode_Order()
    {
        var projectDir = Directory.CreateTempSubdirectory();
        var inputDir = Directory.CreateTempSubdirectory();
        var outputDir = Directory.CreateTempSubdirectory();

        await File.WriteAllTextAsync(Path.Combine(projectDir.FullName, "短剧信息.txt"), """
原剧名: 原剧名
新剧名: 顺序验证
时长: 8 分钟
集数: 3
成本: 1 万元
制作公司: 湖北云漫科技有限公司
""");

        var inputPath10 = Path.Combine(inputDir.FullName, "顺序验证-第10集.mp4");
        var inputPath2 = Path.Combine(inputDir.FullName, "顺序验证-第2集.mp4");
        var inputPath1 = Path.Combine(inputDir.FullName, "顺序验证-第1集.mp4");
        await File.WriteAllBytesAsync(inputPath10, [1]);
        await File.WriteAllBytesAsync(inputPath2, [2]);
        await File.WriteAllBytesAsync(inputPath1, [3]);

        var outputPath1 = Path.Combine(outputDir.FullName, "顺序验证-第1集.mp4");
        var outputPath2 = Path.Combine(outputDir.FullName, "顺序验证-第2集.mp4");
        var outputPath3 = Path.Combine(outputDir.FullName, "顺序验证-第3集.mp4");

        var runner = new ScriptedProcessRunner(new Dictionary<string, ProbeScenario>(StringComparer.Ordinal)
        {
            [inputPath1] = new ProbeScenario(DurationSeconds: 35d, Width: 1080, Height: 1920, VideoBitrateBps: 5_500_000, AudioBitrateBps: 128_000),
            [inputPath2] = new ProbeScenario(DurationSeconds: 35d, Width: 1080, Height: 1920, VideoBitrateBps: 5_500_000, AudioBitrateBps: 128_000),
            [inputPath10] = new ProbeScenario(DurationSeconds: 35d, Width: 1080, Height: 1920, VideoBitrateBps: 5_500_000, AudioBitrateBps: 128_000),
            [outputPath1] = new ProbeScenario(DurationSeconds: 35d, Width: 1080, Height: 1920, VideoBitrateBps: 5_500_000, AudioBitrateBps: 128_000),
            [outputPath2] = new ProbeScenario(DurationSeconds: 35d, Width: 1080, Height: 1920, VideoBitrateBps: 5_500_000, AudioBitrateBps: 128_000),
            [outputPath3] = new ProbeScenario(DurationSeconds: 35d, Width: 1080, Height: 1920, VideoBitrateBps: 5_500_000, AudioBitrateBps: 128_000)
        });

        var transcoder = new FfmpegVideoTranscoder(
            new TxtProjectInfoParser(),
            runner,
            NullLogger<FfmpegVideoTranscoder>.Instance);

        var result = await transcoder.TranscodeAsync(
            new VideoTranscodeRequest(projectDir.FullName, inputDir.FullName, outputDir.FullName, Overwrite: true),
            progress: null,
            CancellationToken.None);

        result.TranscodedFiles.Should().Be(3);
        runner.FfmpegOutputs.Should().ContainInOrder(outputPath1, outputPath2, outputPath3);
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
        public List<string> FfmpegOutputs { get; } = [];

        public ScriptedProcessRunner(IReadOnlyDictionary<string, ProbeScenario> probes)
        {
            _probes = probes;
        }

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
                FfmpegOutputs.Add(outputPath);
                File.WriteAllBytes(outputPath, [9, 8, 7]);
                return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
            }

            throw new InvalidOperationException($"Unexpected process: {fileName}");
        }
    }
}
