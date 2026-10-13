using FluentAssertions;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class PlayletDramaChainTests
{
    [Theory]
    [InlineData("1080P", "1", "1080", "2", "720")]
    [InlineData("1080P+", "0", "1080", "1")]
    [InlineData("720P", "2", "720", "1")]
    public void Quality_fallback_uses_downloader_codes(string quality, params string[] expected)
    {
        PlayletDramaClient.BuildQualityFallbackCodes(quality).Should().Equal(expected);
    }

    [Fact]
    public async Task Search_does_not_send_cookie_even_when_pikachu_cookie_is_configured()
    {
        var handler = new PlayletHandler();
        using var httpClient = new HttpClient(handler);
        var router = new DramaSourceRouter(
            httpClient,
            new PlayletSettings(new DramaSourceSettings
            {
                DramaSourceChain = "playlet",
                PikachuFanqieCookie = $"install_id=12345; ttreq=1${new string('b', 32)}; odin_tt={new string('a', 160)}",
                PikachuDramaType = "short",
                PlayletDeviceId = "DC8D94CB6E7122F92E"
            }),
            new HongguoLocalApiService(httpClient),
            new HongguoNewApiService(httpClient),
            new HongguoDramaSearchService(httpClient),
            new HongguoDramaDownloader(httpClient),
            new HongguoMemoryReaderService());

        var results = await router.SearchAsync("财神", 1, CancellationToken.None);

        results.Should().ContainSingle();
        results[0].BookId.Should().Be("playlet:7650355113332050968");
        results[0].Title.Should().Be("傅少宠妻甜蜜蜜");
        handler.SearchCookieCount.Should().Be(0);
        handler.SearchNoCookieCount.Should().Be(1);
    }

    [Fact]
    public async Task Episodes_come_from_directory_and_video_uses_membership_device()
    {
        var handler = new PlayletHandler();
        using var httpClient = new HttpClient(handler);

        var episodes = await PlayletDramaClient.GetEpisodesAsync(
            httpClient,
            PlayletDramaClient.DefaultServerUrl,
            "playlet:7650355113332050968",
            CancellationToken.None);
        var video = await PlayletDramaClient.GetVideoAsync(
            httpClient,
            PlayletDramaClient.DefaultServerUrl,
            episodes[0].VideoId,
            "1080P",
            "dc8d94cb6e7122f92e",
            CancellationToken.None);

        episodes.Should().HaveCount(2);
        episodes[0].VideoId.Should().Be("7650358399711841304");
        episodes[0].Title.Should().Be("第1集");
        video.Url.Should().Be("https://cdn.example.com/1.mp4");
        video.Key.Should().HaveLength(32);
        handler.VideoPaths.Should().ContainSingle().Which.Should().EndWith("/api/drama/hongguo/video");
        handler.VideoDeviceId.Should().Be("DC8D94CB6E7122F92E");
        handler.VideoPaths.Should().NotContain(path => path.EndsWith("/decryptVideo", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class PlayletSettings(DramaSourceSettings settings) : IDramaSettingsProvider
    {
        public DramaSourceSettings Get() => settings;

        public void SavePikachuDeviceId(string deviceId)
        {
        }
    }

    private sealed class PlayletHandler : HttpMessageHandler
    {
        public int SearchCookieCount { get; private set; }
        public int SearchNoCookieCount { get; private set; }
        public List<string> VideoPaths { get; } = [];
        public string? VideoDeviceId { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/search/page/", StringComparison.Ordinal))
            {
                var hasCookie = request.Headers.TryGetValues("cookie", out var cookies) &&
                                cookies.Any(value => !string.IsNullOrWhiteSpace(value));
                if (hasCookie)
                {
                    SearchCookieCount++;
                    return Task.FromResult(Json("""{"code":6000,"message":"网络错误，请稍后重试"}"""));
                }

                SearchNoCookieCount++;
                return Task.FromResult(Json("""
                    {
                      "code": 0,
                      "data": {
                        "search_data": [
                          {
                            "books": [
                              {
                                "book_id": "7650355113332050968",
                                "book_name": "傅少宠妻甜蜜蜜",
                                "super_category": "9",
                                "serial_count": 92
                              }
                            ]
                          }
                        ]
                      }
                    }
                    """));
            }

            if (path.Contains("/directory/list/", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""
                    {
                      "code": 0,
                      "data": {
                        "item_list": ["7650358399711841304", "7650358459367427096"]
                      }
                    }
                    """));
            }

            if (path.EndsWith("/api/drama/hongguo/video", StringComparison.OrdinalIgnoreCase))
            {
                VideoPaths.Add(path);
                if (request.Headers.TryGetValues("deviceId", out var values))
                {
                    VideoDeviceId = values.FirstOrDefault();
                }

                return Task.FromResult(Json(JsonSerializer.Serialize(new
                {
                    code = 200,
                    data = new
                    {
                        type = "dp4",
                        url = "https://cdn.example.com/1.mp4",
                        key = new string('a', 32)
                    }
                })));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"unexpected {request.RequestUri}")
            });
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
