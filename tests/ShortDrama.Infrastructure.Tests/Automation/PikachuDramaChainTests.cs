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
    public async Task SearchAsync_Should_Repair_Cookie_And_Filter_Non_Drama_Results()
    {
        var cleanCookie = BuildFanqieCookie();
        var settings = new DramaSourceSettings
        {
            DramaSourceChain = "pikachu",
            PikachuFanqieCookie = $"{cleanCookie}\0\b\u0003metadata",
            PikachuDramaType = "short"
        };
        var handler = new PikachuRecordingHandler();
        using var httpClient = new HttpClient(handler);
        var router = new DramaSourceRouter(
            httpClient,
            new TestDramaSettingsProvider(settings),
            new HongguoLocalApiService(httpClient),
            new HongguoNewApiService(httpClient),
            new HongguoDramaSearchService(httpClient),
            new HongguoDramaDownloader(httpClient),
            new HongguoMemoryReaderService());

        var results = await router.SearchAsync("  测试短剧  ", 1, CancellationToken.None);

        results.Select(item => item.Title).Should().Equal("保留短剧", "兼容旧短剧");
        results.Select(item => item.BookId).Should().Equal(
            "pikachu:7599558182226119705",
            "pikachu:7599558182226119708");
        handler.SearchCookie.Should().Be(cleanCookie);
        handler.SearchAccept.Should().Contain("*/*");
        handler.SearchForm["limit"].Should().Be("20");
        handler.SearchForm["offset"].Should().Be("0");
        handler.SearchForm["query"].Should().Be("测试短剧");

        using var searchContext = JsonDocument.Parse(handler.SearchForm["search_ctx_info"]);
        searchContext.RootElement.GetProperty("search_tab_id").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task SearchAsync_Should_Not_Fallback_To_Pikachu_When_Hglocal_Returns_Empty()
    {
        var settings = new DramaSourceSettings
        {
            DramaSourceChain = "hglocal",
            HongguoLocalBaseUrl = "https://local.example.com",
            HongguoLocalApiKey = "local-key",
            PikachuFanqieCookie = BuildFanqieCookie(),
            PikachuDramaType = "short"
        };
        var handler = new HglocalEmptyRecordingHandler();
        using var httpClient = new HttpClient(handler);
        var router = new DramaSourceRouter(
            httpClient,
            new TestDramaSettingsProvider(settings),
            new HongguoLocalApiService(httpClient),
            new HongguoNewApiService(httpClient),
            new HongguoDramaSearchService(httpClient),
            new HongguoDramaDownloader(httpClient),
            new HongguoMemoryReaderService());

        var results = await router.SearchAsync("本地无结果", 1, CancellationToken.None);

        results.Should().BeEmpty();
        handler.Requests.Should().Contain(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/api/hongguo/search", StringComparison.OrdinalIgnoreCase));
        handler.Requests.Should().OnlyContain(request =>
            request.RequestUri!.AbsolutePath.StartsWith("/api/hongguo/", StringComparison.OrdinalIgnoreCase));
        handler.Requests.Should().NotContain(request =>
            request.RequestUri!.AbsolutePath.Contains("novelfm", StringComparison.OrdinalIgnoreCase) ||
            request.RequestUri!.AbsolutePath.Contains("/api/drama/hongguo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DownloadAsync_Should_Use_Pikachu_DecryptVideo_Endpoint()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"pikachu-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var handler = new PikachuRecordingHandler();
        using var httpClient = new HttpClient(handler);
        var previousFfprobeResolver = DramaSourceRouter.ResolveFfprobeBinaryForTests.Value;
        var previousProcessRunner = DramaSourceRouter.RunProcessAsyncForTests.Value;

        try
        {
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = () => "fake-ffprobe";
            DramaSourceRouter.RunProcessAsyncForTests.Value = (startInfo, _) =>
            {
                startInfo.FileName.Should().Be("fake-ffprobe");
                return Task.FromResult(ProbeResult("h264"));
            };

            var settings = new DramaSourceSettings
            {
                DramaSourceChain = "pikachu",
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
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
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
                    return ProbeResult("h264");
                }

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
            DramaSourceRouter.ResolveFfprobeBinaryForTests.Value = previousFfprobeResolver;
            DramaSourceRouter.RunProcessAsyncForTests.Value = previousProcessRunner;
            Directory.Delete(outputDir, recursive: true);
        }
    }

    private sealed class PikachuRecordingHandler(string? decryptKey = null, byte[]? cdnContent = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public string? SearchCookie { get; private set; }
        public IReadOnlyList<string> SearchAccept { get; private set; } = [];
        public IReadOnlyDictionary<string, string> SearchForm { get; private set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly byte[] _cdnContent = cdnContent ?? [0, 0, 0, 24, 102, 116, 121, 112];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);

            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/novelfm/bookmall/search/page/v1/", StringComparison.OrdinalIgnoreCase))
            {
                SearchCookie = request.Headers.TryGetValues("cookie", out var cookieValues)
                    ? cookieValues.Single()
                    : null;
                SearchAccept = request.Headers.TryGetValues("Accept", out var acceptValues)
                    ? acceptValues.ToArray()
                    : [];
                SearchForm = ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
                return JsonResponse("""
                    {
                      "code": 0,
                      "data": {
                        "search_data": [
                          {
                            "cell_slices": [
                              {
                                "book_slice": {
                                  "book_info": {
                                    "book_id": "7599558182226119705",
                                    "book_name": "保留短剧",
                                    "super_category": "9",
                                    "genre": "203",
                                    "serial_count": "12"
                                  }
                                }
                              },
                              {
                                "book_slice": {
                                  "book_info": {
                                    "book_id": "7599558182226119706",
                                    "book_name": "剔除音乐",
                                    "super_category": "1",
                                    "genre": "10"
                                  }
                                }
                              },
                              {
                                "book_slice": {
                                  "book_info": {
                                    "book_id": "7599558182226119707",
                                    "book_name": "剔除旧音乐",
                                    "genre": "262"
                                  }
                                }
                              },
                              {
                                "book_slice": {
                                  "book_info": {
                                    "book_id": "7599558182226119708",
                                    "book_name": "兼容旧短剧",
                                    "genre": "203",
                                    "serial_count": 8
                                  }
                                }
                              }
                            ]
                          }
                        ]
                      }
                    }
                    """);
            }

            if (path.EndsWith("/api/drama/hongguo/detail", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                    {
                      "code": 200,
                      "data": {
                        "data": [
                          { "videoId": "video-1", "title": "episode-1" }
                        ]
                      }
                    }
                    """);
            }

            if (path.EndsWith("/api/drama/hongguo/decryptVideo", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    code = 200,
                    data = new
                    {
                        url = "https://cdn.example.com/video-1.mp4",
                        key = decryptKey
                    }
                }));
            }

            if (request.Method == HttpMethod.Get &&
                string.Equals(request.RequestUri.Host, "cdn.example.com", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_cdnContent)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"unexpected request: {request.RequestUri}")
            };
        }

        private static IReadOnlyDictionary<string, string> ParseForm(string body)
        {
            return body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(
                    pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
                    pair => pair.Length > 1
                        ? Uri.UnescapeDataString(pair[1].Replace('+', ' '))
                        : string.Empty,
                    StringComparer.Ordinal);
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class HglocalEmptyRecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/hongguo/search", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""{"results":[]}"""));
            }

            if (path.EndsWith("/api/hongguo/latest", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""{"items":[]}"""));
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

    private static DramaSourceRouter.ProcessRunResult ProbeResult(string codec) =>
        new(0, $$"""
            {
              "streams": [
                { "codec_type": "video", "codec_name": "{{codec}}" }
              ]
            }
            """, "");

    private static string BuildFanqieCookie() =>
        $"install_id=12345; ttreq=1${new string('b', 32)}; odin_tt={new string('a', 160)}";

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
