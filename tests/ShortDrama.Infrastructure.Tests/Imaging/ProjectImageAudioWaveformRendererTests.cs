using System.Buffers.Binary;
using FluentAssertions;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Imaging;

public sealed class ProjectImageAudioWaveformRendererTests
{
    [Fact]
    public void BuildNormalizedLevels_bins_8khz_pcm_at_20_levels_per_second()
    {
        var pcm = BuildPcm(
            seconds: 1,
            bin => bin < 5 ? (short)0 : (short)(1_000 + bin * 700));

        var levels = ProjectImageAudioWaveformRenderer.BuildNormalizedLevels(pcm, 1d);

        levels.Should().HaveCount(20);
        levels.Take(5).Should().OnlyContain(level => level == 0d);
        levels.Skip(5).Should().OnlyContain(level => level >= 0.02d && level <= 1d);
        levels.Max().Should().BeApproximately(1d, 0.0001d);
    }

    [Fact]
    public void NormalizeLevels_uses_a_robust_peak_and_suppresses_noise_floor()
    {
        var levels = ProjectImageAudioWaveformRenderer.NormalizeLevels(
            [0d, 0.005d, 0.011d, 0.20d, 0.40d, 0.80d, 1d, 20d]);

        levels.Take(3).Should().OnlyContain(level => level == 0d);
        levels.Skip(3).Should().OnlyContain(level => level >= 0.02d && level <= 1d);
        levels[^1].Should().Be(1d);
    }

    [Fact]
    public void ResampleLevels_preserves_peaks_when_reducing_width()
    {
        ProjectImageAudioWaveformRenderer.ResampleLevels(
                [0d, 0.1d, 1d, 0.1d, 0d, 0d],
                targetCount: 3)
            .Should().Equal(0.1d, 1d, 0d);
    }

    [Fact]
    public void DetectWaveformRectangles_finds_existing_bottom_tracks()
    {
        using var canvas = new Image<Rgba32>(400, 300, new Rgba32(26, 26, 26, 255));
        Fill(canvas, new Rectangle(10, 220, 380, 29), new Rgba32(14, 48, 88, 255));
        Fill(canvas, new Rectangle(10, 253, 380, 29), new Rgba32(14, 129, 179, 255));

        var rectangles = ProjectImageAudioWaveformRenderer.DetectWaveformRectangles(canvas);

        rectangles.Should().HaveCount(2);
        rectangles[0].Should().Be(new Rectangle(10, 220, 380, 29));
        rectangles[1].Should().Be(new Rectangle(10, 253, 380, 29));
    }

    [Fact]
    public void BuildFallbackRectangles_matches_the_stable_template_5_bottom_area()
    {
        var rectangles = ProjectImageAudioWaveformRenderer.BuildFallbackRectangles(1920, 1080);

        rectangles.Should().HaveCount(3);
        rectangles.Should().OnlyContain(rectangle =>
            rectangle.X >= 37 && rectangle.X <= 39 &&
            rectangle.Width > 1_850 &&
            rectangle.Y >= 870 &&
            rectangle.Bottom <= 1_080);
        rectangles.Should().BeInAscendingOrder(rectangle => rectangle.Y);
    }

    [Fact]
    public async Task DecodeAsync_decodes_each_video_once_and_reuses_data_across_pages()
    {
        await WithTemporaryVideosAsync(2, async videoPaths =>
        {
            var runner = new StubProcessRunner(BuildPcm(1, bin => (short)(2_000 + bin * 500)));

            var waveformData = await ProjectImageAudioWaveformRenderer.DecodeAsync(
                runner,
                "fake-ffmpeg",
                videoPaths,
                [1d, 1d],
                CancellationToken.None);

            waveformData.Should().NotBeNull();
            waveformData!.Episodes.Should().HaveCount(2);
            waveformData.Episodes.Should().OnlyContain(episode => episode.Levels.Count == 20);
            runner.CallCount.Should().Be(2, "每个源视频只应解码一次");
            runner.OutputPaths.Should().OnlyContain(path => !File.Exists(path));

            using var firstPage = new Image<Rgba32>(400, 300, new Rgba32(26, 26, 26, 255));
            using var secondPage = new Image<Rgba32>(400, 300, new Rgba32(26, 26, 26, 255));
            ProjectImageAudioWaveformRenderer.Render(firstPage, waveformData, 0, 120).Should().BeTrue();
            ProjectImageAudioWaveformRenderer.Render(secondPage, waveformData, 1, 240).Should().BeTrue();
            runner.CallCount.Should().Be(2, "多页 Render 只能读取内存波形，不能再次启动 ffmpeg");
        });
    }

    [Fact]
    public async Task DecodeAsync_preserves_episode_alignment_when_one_video_fails()
    {
        await WithTemporaryVideosAsync(2, async videoPaths =>
        {
            var runner = new StubProcessRunner(
                BuildPcm(1, _ => 5_000),
                exitCodes: [1, 0]);

            var waveformData = await ProjectImageAudioWaveformRenderer.DecodeAsync(
                runner,
                "fake-ffmpeg",
                videoPaths,
                [1d, 1d],
                CancellationToken.None);

            waveformData.Should().NotBeNull("后一集仍有可用波形");
            waveformData!.Episodes.Should().HaveCount(2);
            waveformData.Episodes[0].Levels.Should().BeEmpty();
            waveformData.Episodes[1].Levels.Should().NotBeEmpty();
            using var failedPage = new Image<Rgba32>(400, 300);
            using var successfulPage = new Image<Rgba32>(400, 300);
            ProjectImageAudioWaveformRenderer.Render(failedPage, waveformData, 0, null).Should().BeFalse();
            ProjectImageAudioWaveformRenderer.Render(successfulPage, waveformData, 1, null).Should().BeTrue();
            runner.OutputPaths.Should().OnlyContain(path => !File.Exists(path));
        });
    }

    [Fact]
    public async Task DecodeAsync_returns_null_when_no_video_has_a_usable_waveform()
    {
        await WithTemporaryVideosAsync(2, async videoPaths =>
        {
            var runner = new StubProcessRunner(
                BuildPcm(1, _ => 5_000),
                exitCodes: [1, 1]);

            var waveformData = await ProjectImageAudioWaveformRenderer.DecodeAsync(
                runner,
                "fake-ffmpeg",
                videoPaths,
                [1d, 1d],
                CancellationToken.None);

            waveformData.Should().BeNull();
            runner.CallCount.Should().Be(2);
            runner.OutputPaths.Should().OnlyContain(path => !File.Exists(path));
        });
    }

    [Fact]
    public async Task DecodeAsync_propagates_requested_cancellation_without_starting_ffmpeg()
    {
        await WithTemporaryVideoAsync(async videoPath =>
        {
            var runner = new StubProcessRunner(BuildPcm(1, _ => 5_000));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var act = () => ProjectImageAudioWaveformRenderer.DecodeAsync(
                runner,
                "fake-ffmpeg",
                [videoPath],
                [1d],
                cancellation.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            runner.CallCount.Should().Be(0);
        });
    }

    [Fact]
    public async Task DecodeAsync_cleans_current_temp_file_when_ffmpeg_is_cancelled()
    {
        await WithTemporaryVideoAsync(async videoPath =>
        {
            using var cancellation = new CancellationTokenSource();
            var runner = new CancellingProcessRunner(
                BuildPcm(1, _ => 5_000),
                cancellation);

            var act = () => ProjectImageAudioWaveformRenderer.DecodeAsync(
                runner,
                "fake-ffmpeg",
                [videoPath],
                [1d],
                cancellation.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            runner.OutputPath.Should().NotBeNullOrWhiteSpace();
            File.Exists(runner.OutputPath!).Should().BeFalse("取消路径也必须执行 finally 清理");
        });
    }

    [Fact]
    public async Task RenderAsync_extracts_pcm_renders_tracks_restores_playhead_and_cleans_temp_file()
    {
        await WithTemporaryVideoAsync(async videoPath =>
        {
            var runner = new StubProcessRunner(BuildPcm(1, bin => (short)(2_000 + bin * 500)));
            using var canvas = new Image<Rgba32>(400, 300, new Rgba32(26, 26, 26, 255));
            const int playheadX = 200;

            var rendered = await ProjectImageAudioWaveformRenderer.RenderAsync(
                canvas,
                runner,
                "fake-ffmpeg",
                [videoPath],
                [1d],
                episodeIndex: 0,
                playheadX,
                CancellationToken.None);

            rendered.Should().BeTrue();
            runner.Arguments.Should().ContainInOrder("-ac", "1", "-ar", "8000", "-f", "s16le");
            runner.OutputPath.Should().NotBeNullOrWhiteSpace();
            File.Exists(runner.OutputPath!).Should().BeFalse("临时 s16le 文件必须在成功后清理");

            var fallback = ProjectImageAudioWaveformRenderer.BuildFallbackRectangles(canvas.Width, canvas.Height);
            var waveformPixel = canvas[fallback[0].X + 12, fallback[0].Bottom - 5];
            waveformPixel.Should().NotBe(new Rgba32(26, 26, 26, 255));
            var playheadPixel = canvas[playheadX, fallback[1].Y + 3];
            playheadPixel.R.Should().Be(232);
            playheadPixel.G.Should().Be(236);
            playheadPixel.B.Should().Be(240);
        });
    }

    [Fact]
    public async Task RenderAsync_returns_false_and_cleans_temp_file_when_ffmpeg_fails()
    {
        await WithTemporaryVideoAsync(async videoPath =>
        {
            var runner = new StubProcessRunner(BuildPcm(1, _ => 5_000), exitCode: 1);
            using var canvas = new Image<Rgba32>(400, 300, new Rgba32(26, 26, 26, 255));

            var rendered = await ProjectImageAudioWaveformRenderer.RenderAsync(
                canvas,
                runner,
                "fake-ffmpeg",
                [videoPath],
                [1d],
                episodeIndex: 0,
                playheadX: null,
                CancellationToken.None);

            rendered.Should().BeFalse();
            runner.OutputPath.Should().NotBeNullOrWhiteSpace();
            File.Exists(runner.OutputPath!).Should().BeFalse("失败路径也必须清理临时 s16le 文件");
            canvas[200, 250].Should().Be(new Rgba32(26, 26, 26, 255));
        });
    }

    [Fact]
    public async Task RenderAsync_is_best_effort_and_cleans_temp_file_when_runner_throws()
    {
        await WithTemporaryVideoAsync(async videoPath =>
        {
            var runner = new StubProcessRunner(
                BuildPcm(1, _ => 5_000),
                throwAfterWrite: true);
            using var canvas = new Image<Rgba32>(400, 300, new Rgba32(26, 26, 26, 255));

            var rendered = await ProjectImageAudioWaveformRenderer.RenderAsync(
                canvas,
                runner,
                "fake-ffmpeg",
                [videoPath],
                [1d],
                episodeIndex: 0,
                playheadX: null,
                CancellationToken.None);

            rendered.Should().BeFalse();
            runner.OutputPath.Should().NotBeNullOrWhiteSpace();
            File.Exists(runner.OutputPath!).Should().BeFalse();
        });
    }

    [Fact]
    public async Task RenderAsync_propagates_requested_cancellation_without_starting_ffmpeg()
    {
        await WithTemporaryVideoAsync(async videoPath =>
        {
            var runner = new StubProcessRunner(BuildPcm(1, _ => 5_000));
            using var canvas = new Image<Rgba32>(400, 300);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var act = () => ProjectImageAudioWaveformRenderer.RenderAsync(
                canvas,
                runner,
                "fake-ffmpeg",
                [videoPath],
                [1d],
                episodeIndex: 0,
                playheadX: null,
                cancellation.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            runner.CallCount.Should().Be(0);
        });
    }

    private static byte[] BuildPcm(int seconds, Func<int, short> amplitudeForBin)
    {
        var sampleCount = ProjectImageAudioWaveformRenderer.PcmSampleRate * seconds;
        var pcm = new byte[sampleCount * 2];
        var samplesPerBin = ProjectImageAudioWaveformRenderer.PcmSampleRate /
                            ProjectImageAudioWaveformRenderer.WaveformSamplesPerSecond;
        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            var amplitude = amplitudeForBin(sampleIndex / samplesPerBin);
            var value = sampleIndex % 2 == 0 ? amplitude : (short)-amplitude;
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(sampleIndex * 2, 2), value);
        }

        return pcm;
    }

    private static void Fill(Image<Rgba32> canvas, Rectangle rectangle, Rgba32 color)
    {
        for (var y = rectangle.Y; y < rectangle.Bottom; y++)
        {
            for (var x = rectangle.X; x < rectangle.Right; x++)
                canvas[x, y] = color;
        }
    }

    private static async Task WithTemporaryVideoAsync(Func<string, Task> assertion)
    {
        await WithTemporaryVideosAsync(1, paths => assertion(paths[0]));
    }

    private static async Task WithTemporaryVideosAsync(
        int count,
        Func<IReadOnlyList<string>, Task> assertion)
    {
        var videoPaths = Enumerable.Range(0, count)
            .Select(_ => Path.Combine(Path.GetTempPath(), $"waveform-source-{Guid.NewGuid():N}.mp4"))
            .ToArray();
        foreach (var videoPath in videoPaths)
            await File.WriteAllBytesAsync(videoPath, [0]);
        try
        {
            await assertion(videoPaths);
        }
        finally
        {
            foreach (var videoPath in videoPaths)
            {
                try { File.Delete(videoPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private sealed class StubProcessRunner(
        byte[] pcm,
        int exitCode = 0,
        bool throwAfterWrite = false,
        IReadOnlyList<int>? exitCodes = null) : IExternalProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public string? OutputPath { get; private set; }
        public List<string> OutputPaths { get; } = [];
        public int CallCount { get; private set; }

        public Task<ExternalProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Arguments = arguments.ToArray();
            OutputPath = arguments[^1];
            OutputPaths.Add(OutputPath);
            File.WriteAllBytes(OutputPath, pcm);
            if (throwAfterWrite)
                throw new InvalidOperationException("simulated ffmpeg failure");

            var resolvedExitCode = exitCodes is not null && CallCount <= exitCodes.Count
                ? exitCodes[CallCount - 1]
                : exitCode;
            return Task.FromResult(new ExternalProcessResult(resolvedExitCode, string.Empty, "simulated stderr"));
        }
    }

    private sealed class CancellingProcessRunner(
        byte[] pcm,
        CancellationTokenSource cancellation) : IExternalProcessRunner
    {
        public string? OutputPath { get; private set; }

        public Task<ExternalProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            CancellationToken cancellationToken)
        {
            OutputPath = arguments[^1];
            File.WriteAllBytes(OutputPath, pcm);
            cancellation.Cancel();
            return Task.FromCanceled<ExternalProcessResult>(cancellation.Token);
        }
    }
}
