using FluentAssertions;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class DramaSourceRouterDownloadTests
{
    [Fact]
    public async Task TryBuildSuccessfulResultWhenVideosExist_Should_Return_Null_When_OutputDir_Has_No_Videos()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        try
        {
            var request = new DramaDownloadRequest(
                ProjectDir: outputDir,
                OutputDir: outputDir,
                DisplayName: "测试剧",
                BookId: "book-1",
                Episodes: "all",
                Quality: "1080P",
                Concurrent: 3,
                EpisodeNumberMode: "source");

            (await DramaSourceRouter.TryBuildSuccessfulResultWhenVideosExistAsync(
                request,
                progress: null,
                CancellationToken.None)).Should().BeNull();
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task TryBuildSuccessfulResultWhenVideosExist_Should_Return_Success_When_Videos_Already_Present()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-videos-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        File.WriteAllBytes(Path.Combine(outputDir, "第01集.mp4"), [1, 2, 3]);

        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;

        try
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = (_, _) => Task.FromResult(ProbeResult("h264"));
            var request = new DramaDownloadRequest(
                ProjectDir: outputDir,
                OutputDir: outputDir,
                DisplayName: "测试剧",
                BookId: "book-1",
                Episodes: "all",
                Quality: "1080P",
                Concurrent: 3,
                EpisodeNumberMode: "source");

            var result = await DramaSourceRouter.TryBuildSuccessfulResultWhenVideosExistAsync(
                request,
                progress: null,
                CancellationToken.None);

            result.Should().NotBeNull();
            result!.Ok.Should().BeTrue();
            result.VideoCount.Should().Be(1);
            result.Message.Should().Contain("跳过 legacy 红果下载重试");
        }
        finally
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Use_Local_Stream_And_Transcode_Hevc_To_H264()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-hglocal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new LocalStreamRecordingHandler();
        using var httpClient = new HttpClient(handler);
        var previousFfmpegResolver = DramaSourceRouter.ResolveFfmpegBinaryForTests.Value;
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;

        try
        {
            DramaSourceRouter.ResolveFfmpegBinaryForTests.Value = () => "fake-ffmpeg";
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = async (startInfo, cancellationToken) =>
            {
                if (startInfo.FileName == "fake-ffprobe")
                {
                    return startInfo.ArgumentList[^1].EndsWith(".h264.mp4", StringComparison.OrdinalIgnoreCase)
                        ? ProbeResult("h264")
                        : ProbeResult("hevc");
                }

                startInfo.FileName.Should().Be("fake-ffmpeg");
                startInfo.ArgumentList.Should().Contain("libx264");
                startInfo.ArgumentList.Should().Contain("aac");
                var inputPath = startInfo.ArgumentList[startInfo.ArgumentList.IndexOf("-i") + 1];
                File.ReadAllBytes(inputPath).Should().Equal(LocalStreamRecordingHandler.StreamBytes);
                var outputPath = startInfo.ArgumentList[^1];
                outputPath.Should().EndWith(".h264.mp4");
                await File.WriteAllBytesAsync(outputPath, Encoding.UTF8.GetBytes("h264-video"), cancellationToken);
                return new DramaSourceRouter.ProcessRunResult(0, "", "");
            };

            var settings = new DramaSourceSettings
            {
                DramaSourceChain = "hglocal",
                HongguoDownloadTimeoutSeconds = "10",
                HongguoEpisodeDownloadAttempts = "1",
                HongguoLocalBaseUrl = "https://local.example.com",
                HongguoLocalApiKey = "local-key",
                HongguoLocalDownloadMode = "compatible",
                HongguoLocalTranscodeEngine = "cpu"
            };
            var router = new DramaSourceRouter(
                httpClient,
                new TestDramaSettingsProvider(settings),
                new HongguoLocalApiService(httpClient),
                new HongguoNewApiService(httpClient),
                new HongguoDramaSearchService(httpClient),
                new HongguoDramaDownloader(httpClient),
                new HongguoMemoryReaderService());

            var request = new DramaDownloadRequest(
                ProjectDir: outputDir,
                OutputDir: outputDir,
                DisplayName: "test-drama",
                BookId: "hglocal:series-1",
                Episodes: "1",
                Quality: "1080P+",
                Concurrent: 1,
                EpisodeNumberMode: "source");

            var result = await router.DownloadAsync(request, progress: null, CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            result.VideoCount.Should().Be(1);
            var videoPath = Directory.GetFiles(outputDir, "*.mp4").Should().ContainSingle().Subject;
            File.ReadAllText(videoPath).Should().Be("h264-video");
            handler.Requests.Should().Contain(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/api/hongguo/video_url", StringComparison.OrdinalIgnoreCase));
            handler.Requests.Should().Contain(request =>
                string.Equals(request.RequestUri!.Host, "cdn.example.com", StringComparison.OrdinalIgnoreCase));
            handler.Requests.Should().NotContain(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/api/hongguo/stream", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DramaSourceRouter.ResolveFfmpegBinaryForTests.Value = previousFfmpegResolver;
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Use_Nvenc_When_Local_Compatible_Mode_Is_Auto_And_Nvenc_Available()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-hglocal-nvenc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new LocalStreamRecordingHandler();
        using var httpClient = new HttpClient(handler);
        var previousFfmpegResolver = DramaSourceRouter.ResolveFfmpegBinaryForTests.Value;
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;
        var sawEncoderProbe = false;
        var sawNvencTranscode = false;
        var sawCpuTranscode = false;

        try
        {
            DramaSourceRouter.ResolveFfmpegBinaryForTests.Value = () => "fake-ffmpeg";
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = async (startInfo, cancellationToken) =>
            {
                if (startInfo.FileName == "fake-ffprobe")
                {
                    return startInfo.ArgumentList[^1].EndsWith(".h264.mp4", StringComparison.OrdinalIgnoreCase)
                        ? ProbeResult("h264")
                        : ProbeResult("hevc");
                }

                startInfo.FileName.Should().Be("fake-ffmpeg");
                if (startInfo.ArgumentList.Contains("-encoders"))
                {
                    sawEncoderProbe = true;
                    return new DramaSourceRouter.ProcessRunResult(0, " V....D h264_nvenc NVIDIA NVENC H.264 encoder", "");
                }

                if (startInfo.ArgumentList.Contains("libx264"))
                {
                    sawCpuTranscode = true;
                }

                sawNvencTranscode = true;
                startInfo.ArgumentList.Should().Contain("hevc_cuvid");
                startInfo.ArgumentList.Should().Contain("h264_nvenc");
                startInfo.ArgumentList.Should().Contain("-cq");
                startInfo.ArgumentList[startInfo.ArgumentList.IndexOf("-cq") + 1].Should().Be("26");
                startInfo.ArgumentList.Should().Contain("aac");
                var inputPath = startInfo.ArgumentList[startInfo.ArgumentList.IndexOf("-i") + 1];
                File.ReadAllBytes(inputPath).Should().Equal(LocalStreamRecordingHandler.StreamBytes);
                await File.WriteAllBytesAsync(startInfo.ArgumentList[^1], Encoding.UTF8.GetBytes("nvenc-video"), cancellationToken);
                return new DramaSourceRouter.ProcessRunResult(0, "", "");
            };

            var settings = new DramaSourceSettings
            {
                DramaSourceChain = "hglocal",
                HongguoDownloadTimeoutSeconds = "10",
                HongguoEpisodeDownloadAttempts = "1",
                HongguoLocalBaseUrl = "https://local.example.com",
                HongguoLocalApiKey = "local-key",
                HongguoLocalDownloadMode = "compatible",
                HongguoLocalTranscodeEngine = "auto"
            };
            var router = new DramaSourceRouter(
                httpClient,
                new TestDramaSettingsProvider(settings),
                new HongguoLocalApiService(httpClient),
                new HongguoNewApiService(httpClient),
                new HongguoDramaSearchService(httpClient),
                new HongguoDramaDownloader(httpClient),
                new HongguoMemoryReaderService());

            var request = new DramaDownloadRequest(
                ProjectDir: outputDir,
                OutputDir: outputDir,
                DisplayName: "test-drama",
                BookId: "hglocal:series-1",
                Episodes: "1",
                Quality: "1080P+",
                Concurrent: 1,
                EpisodeNumberMode: "source");

            var result = await router.DownloadAsync(request, progress: null, CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            File.ReadAllText(Directory.GetFiles(outputDir, "*.mp4").Should().ContainSingle().Subject).Should().Be("nvenc-video");
            sawEncoderProbe.Should().BeTrue();
            sawNvencTranscode.Should().BeTrue();
            sawCpuTranscode.Should().BeFalse();
        }
        finally
        {
            DramaSourceRouter.ResolveFfmpegBinaryForTests.Value = previousFfmpegResolver;
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Fall_Back_To_Cpu_When_Forced_Nvenc_Is_Unavailable()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-hglocal-nvenc-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new LocalStreamRecordingHandler();
        using var httpClient = new HttpClient(handler);
        var previousFfmpegResolver = DramaSourceRouter.ResolveFfmpegBinaryForTests.Value;
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;
        var sawCpuTranscode = false;

        try
        {
            DramaSourceRouter.ResolveFfmpegBinaryForTests.Value = () => "fake-ffmpeg";
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = async (startInfo, cancellationToken) =>
            {
                if (startInfo.FileName == "fake-ffprobe")
                {
                    return startInfo.ArgumentList[^1].EndsWith(".h264.mp4", StringComparison.OrdinalIgnoreCase)
                        ? ProbeResult("h264")
                        : ProbeResult("hevc");
                }

                if (startInfo.ArgumentList.Contains("-encoders"))
                {
                    return new DramaSourceRouter.ProcessRunResult(0, " V..... libx264", "");
                }

                startInfo.ArgumentList.Should().Contain("libx264");
                startInfo.ArgumentList.Should().NotContain("h264_nvenc");
                sawCpuTranscode = true;
                await File.WriteAllTextAsync(startInfo.ArgumentList[^1], "cpu-video", cancellationToken);
                return new DramaSourceRouter.ProcessRunResult(0, "", "");
            };

            var progress = new RecordingProgress();
            var result = await CreateRouter(
                    httpClient,
                    CreateLocalSettings(downloadMode: "compatible", transcodeEngine: "nvenc"))
                .DownloadAsync(CreateDownloadRequest(outputDir), progress, CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            sawCpuTranscode.Should().BeTrue();
            File.ReadAllText(Directory.GetFiles(outputDir, "*.mp4").Should().ContainSingle().Subject).Should().Be("cpu-video");
            progress.Messages.Should().Contain(message => message.Contains("CPU libx264", StringComparison.Ordinal));
        }
        finally
        {
            DramaSourceRouter.ResolveFfmpegBinaryForTests.Value = previousFfmpegResolver;
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Keep_Hglocal_Hevc_When_Mode_Is_Fast()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-hglocal-fast-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new LocalStreamRecordingHandler();
        using var httpClient = new HttpClient(handler);
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;
        var sawFfprobe = false;

        try
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = (startInfo, _) =>
            {
                startInfo.FileName.Should().Be("fake-ffprobe", "fast mode should validate media but never run ffmpeg");
                sawFfprobe = true;
                return Task.FromResult(ProbeResult("hevc"));
            };

            var settings = new DramaSourceSettings
            {
                DramaSourceChain = "hglocal",
                HongguoDownloadTimeoutSeconds = "10",
                HongguoEpisodeDownloadAttempts = "1",
                HongguoLocalBaseUrl = "https://local.example.com",
                HongguoLocalApiKey = "local-key"
            };
            var router = new DramaSourceRouter(
                httpClient,
                new TestDramaSettingsProvider(settings),
                new HongguoLocalApiService(httpClient),
                new HongguoNewApiService(httpClient),
                new HongguoDramaSearchService(httpClient),
                new HongguoDramaDownloader(httpClient),
                new HongguoMemoryReaderService());

            var request = new DramaDownloadRequest(
                ProjectDir: outputDir,
                OutputDir: outputDir,
                DisplayName: "test-drama",
                BookId: "hglocal:series-1",
                Episodes: "1",
                Quality: "1080P+",
                Concurrent: 1,
                EpisodeNumberMode: "source");

            var result = await router.DownloadAsync(request, progress: null, CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            sawFfprobe.Should().BeTrue();
            var videoPath = Directory.GetFiles(outputDir, "*.mp4").Should().ContainSingle().Subject;
            File.ReadAllBytes(videoPath).Should().Equal(LocalStreamRecordingHandler.StreamBytes);
            handler.Requests.Should().Contain(request =>
                string.Equals(request.RequestUri!.Host, "cdn.example.com", StringComparison.OrdinalIgnoreCase));
            handler.Requests.Should().NotContain(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/api/hongguo/stream", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Keep_H264_Without_Starting_Ffmpeg()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-h264-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var h264Bytes = Encoding.UTF8.GetBytes("h264-source-video");
        var handler = new LocalStreamRecordingHandler { DownloadBytes = h264Bytes };
        using var httpClient = new HttpClient(handler);
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;
        var progress = new RecordingProgress();

        try
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = (startInfo, _) =>
            {
                startInfo.FileName.Should().Be("fake-ffprobe", "H.264 media should not start ffmpeg");
                return Task.FromResult(ProbeResult("h264"));
            };

            var settings = CreateLocalSettings(downloadMode: "compatible", transcodeEngine: "cpu");
            var result = await CreateRouter(httpClient, settings).DownloadAsync(
                CreateDownloadRequest(outputDir),
                progress,
                CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            File.ReadAllBytes(Directory.GetFiles(outputDir, "*.mp4").Should().ContainSingle().Subject).Should().Equal(h264Bytes);
            progress.Messages.Should().Contain(message => message.Contains("编码 H264", StringComparison.Ordinal) &&
                                                          message.Contains("无需转码", StringComparison.Ordinal));
        }
        finally
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Reject_Html_Response_And_Not_Create_Mp4()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-html-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new LocalStreamRecordingHandler
        {
            DownloadBytes = Encoding.UTF8.GetBytes("<html>not a video</html>"),
            DownloadMediaType = "text/html"
        };
        using var httpClient = new HttpClient(handler);
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;

        try
        {
            DramaSourceRouter.RunProcessAsyncForTests.Value = (_, _) =>
                throw new InvalidOperationException("HTML responses should be rejected before ffprobe");

            var result = await CreateRouter(httpClient, CreateLocalSettings()).DownloadAsync(
                CreateDownloadRequest(outputDir),
                progress: null,
                CancellationToken.None);

            result.Ok.Should().BeFalse();
            result.Message.Should().Contain("Content-Type: text/html");
            Directory.GetFiles(outputDir, "*.mp4").Should().BeEmpty();
            Directory.GetFiles(outputDir, "*.part").Should().BeEmpty();
        }
        finally
        {
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Fail_When_Ffprobe_Finds_No_Video_Stream()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-invalid-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new LocalStreamRecordingHandler
        {
            DownloadBytes = Encoding.UTF8.GetBytes("not-media"),
            DownloadMediaType = null
        };
        using var httpClient = new HttpClient(handler);
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;

        try
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = (_, _) => Task.FromResult(
                new DramaSourceRouter.ProcessRunResult(1, "", "Invalid data found when processing input"));

            var result = await CreateRouter(httpClient, CreateLocalSettings(downloadMode: "compatible")).DownloadAsync(
                CreateDownloadRequest(outputDir),
                progress: null,
                CancellationToken.None);

            result.Ok.Should().BeFalse();
            result.Message.Should().Contain("ffprobe 无法识别媒体");
            Directory.GetFiles(outputDir, "*.mp4").Should().BeEmpty();
            Directory.GetFiles(outputDir, "*.part").Should().BeEmpty();
        }
        finally
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Delete_Invalid_Existing_Mp4_And_Redownload()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-invalid-existing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var finalPath = Path.Combine(outputDir, "第1集.mp4");
        await File.WriteAllTextAsync(finalPath, "<html>stale response</html>");
        var handler = new LocalStreamRecordingHandler
        {
            DownloadBytes = Encoding.UTF8.GetBytes("valid-video")
        };
        using var httpClient = new HttpClient(handler);
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;
        var progress = new RecordingProgress();

        try
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = (startInfo, _) =>
            {
                var probedPath = startInfo.ArgumentList[^1];
                return Task.FromResult(string.Equals(probedPath, finalPath, StringComparison.OrdinalIgnoreCase)
                    ? new DramaSourceRouter.ProcessRunResult(1, "", "Invalid data")
                    : ProbeResult("h264"));
            };

            var result = await CreateRouter(httpClient, CreateLocalSettings()).DownloadAsync(
                CreateDownloadRequest(outputDir),
                progress,
                CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            File.ReadAllText(finalPath).Should().Be("valid-video");
            progress.Messages.Should().Contain(message => message.Contains("无效的已有视频", StringComparison.Ordinal) &&
                                                          message.Contains("重新下载", StringComparison.Ordinal));
            handler.Requests.Should().Contain(request =>
                string.Equals(request.RequestUri!.Host, "cdn.example.com", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    private static DramaSourceSettings CreateLocalSettings(
        string downloadMode = "fast",
        string transcodeEngine = "auto") =>
        new()
        {
            DramaSourceChain = "hglocal",
            HongguoDownloadTimeoutSeconds = "10",
            HongguoEpisodeDownloadAttempts = "1",
            HongguoLocalBaseUrl = "https://local.example.com",
            HongguoLocalApiKey = "local-key",
            HongguoLocalDownloadMode = downloadMode,
            HongguoLocalTranscodeEngine = transcodeEngine
        };

    private static DramaDownloadRequest CreateDownloadRequest(string outputDir) =>
        new(
            ProjectDir: outputDir,
            OutputDir: outputDir,
            DisplayName: "test-drama",
            BookId: "hglocal:series-1",
            Episodes: "1",
            Quality: "1080P+",
            Concurrent: 1,
            EpisodeNumberMode: "source");

    private static DramaSourceRouter CreateRouter(HttpClient httpClient, DramaSourceSettings settings) =>
        new(
            httpClient,
            new TestDramaSettingsProvider(settings),
            new HongguoLocalApiService(httpClient),
            new HongguoNewApiService(httpClient),
            new HongguoDramaSearchService(httpClient),
            new HongguoDramaDownloader(httpClient),
            new HongguoMemoryReaderService());

    private sealed class RecordingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value) => Messages.Add(value);
    }

    private sealed class LocalStreamRecordingHandler : HttpMessageHandler
    {
        public static readonly byte[] StreamBytes = Encoding.UTF8.GetBytes("hevc-video");

        public List<HttpRequestMessage> Requests { get; } = [];

        public byte[] DownloadBytes { get; init; } = StreamBytes;

        public string? DownloadMediaType { get; init; } = "video/mp4";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/hongguo/episodes", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""
                    {
                      "episodes": [
                        { "index": 1, "title": "第1集", "vid": "video-1" }
                      ]
                    }
                    """));
            }

            if (path.EndsWith("/api/hongguo/video_url", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""
                    {
                      "url": "https://cdn.example.com/encrypted.mp4",
                      "encrypted_url": "https://cdn.example.com/encrypted.mp4",
                      "spade_a": "test-spade",
                      "encrypt": true
                    }
                    """));
            }

            if (string.Equals(request.RequestUri.Host, "cdn.example.com", StringComparison.OrdinalIgnoreCase))
            {
                var content = new ByteArrayContent(DownloadBytes);
                if (!string.IsNullOrWhiteSpace(DownloadMediaType))
                {
                    content.Headers.ContentType = new MediaTypeHeaderValue(DownloadMediaType);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }

    private static DramaSourceRouter.ProcessRunResult ProbeResult(string codec) =>
        new(0, $$"""
            {
              "streams": [
                { "codec_type": "video", "codec_name": "{{codec}}" }
              ]
            }
            """, "");

    private sealed class TestDramaSettingsProvider(DramaSourceSettings settings) : IDramaSettingsProvider
    {
        public DramaSourceSettings Get() => settings;

        public void SavePikachuDeviceId(string deviceId)
        {
        }
    }
}
