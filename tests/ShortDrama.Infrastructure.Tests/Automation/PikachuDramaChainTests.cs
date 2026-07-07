using FluentAssertions;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class PikachuDramaChainTests
{
    [Fact]
    public async Task TestConnectivityAsync_Should_Probe_Default_Startvlog_DecryptVideo_Endpoint()
    {
        var handler = new PikachuRecordingHandler();
        using var httpClient = new HttpClient(handler);

        var result = await PikachuDramaClient.TestConnectivityAsync(
            httpClient,
            serverUrl: null,
            fanqieCookie: null,
            dramaType: "short",
            deviceId: "HG0123456789ABCDEF",
            clientVersion: null,
            timeoutSeconds: 15,
            cancellationToken: CancellationToken.None);

        result.DetailOk.Should().BeTrue();
        handler.Requests.Should().Contain(request =>
            request.RequestUri!.ToString() == "https://startvlog.cn/start-prod-api/api/drama/hongguo/detail");
        handler.Requests.Should().Contain(request =>
            request.RequestUri!.ToString() == "https://startvlog.cn/start-prod-api/api/drama/hongguo/decryptVideo");
        handler.Requests.Should().NotContain(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/api/drama/hongguo/video", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DownloadAsync_Should_Use_Pikachu_DecryptVideo_Endpoint()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"pikachu-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new PikachuRecordingHandler();
        using var httpClient = new HttpClient(handler);

        try
        {
            var settings = new DramaSourceSettings
            {
                DramaSourceChain = "pikachu",
                DramaServiceOrderDownload = "pikachu,hglocal,hgnew",
                HongguoDownloadTimeoutSeconds = "10",
                HongguoEpisodeDownloadAttempts = "1",
                PikachuServerUrl = "",
                PikachuDeviceId = "HG0123456789ABCDEF",
                PikachuClientVersion = ""
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
                BookId: "pikachu:book-1",
                Episodes: "1",
                Quality: "1080P+",
                Concurrent: 1,
                EpisodeNumberMode: "source");

            var result = await router.DownloadAsync(request, progress: null, CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            result.VideoCount.Should().Be(1);
            handler.Requests.Should().Contain(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/api/drama/hongguo/decryptVideo", StringComparison.OrdinalIgnoreCase));
            handler.Requests.Should().NotContain(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/api/drama/hongguo/video", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_Should_Decrypt_Pikachu_Cenc_Video_When_Key_Returned()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"pikachu-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var decryptKey = "0123456789abcdef0123456789abcdef";
        var encryptedBytes = Encoding.UTF8.GetBytes("encrypted-video");
        var decryptedBytes = Encoding.UTF8.GetBytes("decrypted-video");
        var handler = new PikachuRecordingHandler(decryptKey, encryptedBytes);
        using var httpClient = new HttpClient(handler);
        var previousFfmpegResolver = DramaSourceRouter.ResolveFfmpegBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;

        try
        {
            DramaSourceRouter.ResolveFfmpegBinaryForTests.Value = () => "fake-ffmpeg";
            DramaSourceRouter.RunProcessAsyncForTests.Value = async (startInfo, cancellationToken) =>
            {
                startInfo.FileName.Should().Be("fake-ffmpeg");
                var args = startInfo.ArgumentList;
                args.Should().Contain("-decryption_key");
                args[args.IndexOf("-decryption_key") + 1].Should().Be(decryptKey);
                args.Should().Contain("-i");
                var encryptedPath = args[args.IndexOf("-i") + 1];
                encryptedPath.Should().EndWith(".enc.part");
                File.ReadAllBytes(encryptedPath).Should().Equal(encryptedBytes);
                var outputPath = args[args.Count - 1];
                outputPath.Should().EndWith(".part");
                await File.WriteAllBytesAsync(outputPath, decryptedBytes, cancellationToken);
                return new DramaSourceRouter.ProcessRunResult(0, "", "");
            };

            var settings = new DramaSourceSettings
            {
                DramaSourceChain = "pikachu",
                DramaServiceOrderDownload = "pikachu,hglocal,hgnew",
                HongguoDownloadTimeoutSeconds = "10",
                HongguoEpisodeDownloadAttempts = "1",
                PikachuServerUrl = "",
                PikachuDeviceId = "HG0123456789ABCDEF",
                PikachuClientVersion = ""
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
                BookId: "pikachu:book-1",
                Episodes: "1",
                Quality: "1080P+",
                Concurrent: 1,
                EpisodeNumberMode: "source");

            var result = await router.DownloadAsync(request, progress: null, CancellationToken.None);

            result.Ok.Should().BeTrue(result.Message);
            result.VideoCount.Should().Be(1);
            var videoPath = Directory.GetFiles(outputDir, "*.mp4").Should().ContainSingle().Subject;
            File.ReadAllBytes(videoPath).Should().Equal(decryptedBytes);
            Directory.GetFiles(outputDir, "*.enc.part").Should().BeEmpty();
            handler.Requests.Should().Contain(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/api/drama/hongguo/decryptVideo", StringComparison.OrdinalIgnoreCase));
            handler.Requests.Should().NotContain(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/api/drama/hongguo/video", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DramaSourceRouter.ResolveFfmpegBinaryForTests.Value = previousFfmpegResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    private sealed class PikachuRecordingHandler(string? decryptKey = null, byte[]? cdnContent = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        private readonly byte[] _cdnContent = cdnContent ?? [0, 0, 0, 24, 102, 116, 121, 112];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/drama/hongguo/detail", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""
                    {
                      "code": 200,
                      "data": {
                        "data": [
                          { "videoId": "video-1", "title": "episode-1" }
                        ]
                      }
                    }
                    """));
            }

            if (path.EndsWith("/api/drama/hongguo/decryptVideo", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    code = 200,
                    data = new
                    {
                        url = "https://cdn.example.com/video-1.mp4",
                        key = decryptKey
                    }
                })));
            }

            if (request.Method == HttpMethod.Get &&
                string.Equals(request.RequestUri.Host, "cdn.example.com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_cdnContent)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"unexpected request: {request.RequestUri}")
            });
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class TestDramaSettingsProvider(DramaSourceSettings settings) : IDramaSettingsProvider
    {
        private DramaSourceSettings _settings = settings;

        public DramaSourceSettings Get() => _settings;

        public void SavePikachuDeviceId(string deviceId)
        {
            _settings = _settings.WithPikachuDeviceId(deviceId);
        }
    }
}
