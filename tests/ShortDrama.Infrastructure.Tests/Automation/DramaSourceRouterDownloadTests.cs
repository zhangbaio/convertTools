using FluentAssertions;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using System.Net;
using System.Text;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class DramaSourceRouterDownloadTests
{
    [Fact]
    public void TryBuildSuccessfulResultWhenVideosExist_Should_Return_Null_When_OutputDir_Has_No_Videos()
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

            DramaSourceRouter.TryBuildSuccessfulResultWhenVideosExist(request).Should().BeNull();
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public void TryBuildSuccessfulResultWhenVideosExist_Should_Return_Success_When_Videos_Already_Present()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-videos-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        File.WriteAllBytes(Path.Combine(outputDir, "第01集.mp4"), [1, 2, 3]);

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

            var result = DramaSourceRouter.TryBuildSuccessfulResultWhenVideosExist(request);

            result.Should().NotBeNull();
            result!.Ok.Should().BeTrue();
            result.VideoCount.Should().Be(1);
            result.Message.Should().Contain("跳过 legacy 红果下载重试");
        }
        finally
        {
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
                    return new DramaSourceRouter.ProcessRunResult(0, """
                        {
                          "streams": [
                            { "codec_type": "video", "codec_name": "hevc" },
                            { "codec_type": "audio", "codec_name": "aac" }
                          ]
                        }
                        """, "");
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
                DramaServiceOrderDownload = "hglocal,hgnew,pikachu",
                HongguoDownloadTimeoutSeconds = "10",
                HongguoEpisodeDownloadAttempts = "1",
                HongguoLocalBaseUrl = "https://local.example.com",
                HongguoLocalApiKey = "local-key",
                HongguoLocalDownloadMode = "compatible"
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
                request.RequestUri!.AbsolutePath.EndsWith("/api/hongguo/stream", StringComparison.OrdinalIgnoreCase));
            handler.Requests.Should().NotContain(request =>
                string.Equals(request.RequestUri!.Host, "cdn.example.com", StringComparison.OrdinalIgnoreCase));
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
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;

        try
        {
            DramaSourceRouter.RunProcessAsyncForTests.Value = (_, _) =>
                throw new InvalidOperationException("fast mode should not run ffprobe or ffmpeg");

            var settings = new DramaSourceSettings
            {
                DramaSourceChain = "hglocal",
                DramaServiceOrderDownload = "hglocal,hgnew,pikachu",
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
            var videoPath = Directory.GetFiles(outputDir, "*.mp4").Should().ContainSingle().Subject;
            File.ReadAllBytes(videoPath).Should().Equal(LocalStreamRecordingHandler.StreamBytes);
            handler.Requests.Should().Contain(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/api/hongguo/stream", StringComparison.OrdinalIgnoreCase));
            handler.Requests.Should().NotContain(request =>
                string.Equals(request.RequestUri!.Host, "cdn.example.com", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    private sealed class LocalStreamRecordingHandler : HttpMessageHandler
    {
        public static readonly byte[] StreamBytes = Encoding.UTF8.GetBytes("hevc-video");

        public List<HttpRequestMessage> Requests { get; } = [];

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

            if (path.EndsWith("/api/hongguo/stream", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(StreamBytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }

    private sealed class TestDramaSettingsProvider(DramaSourceSettings settings) : IDramaSettingsProvider
    {
        public DramaSourceSettings Get() => settings;

        public void SavePikachuDeviceId(string deviceId)
        {
        }
    }
}
