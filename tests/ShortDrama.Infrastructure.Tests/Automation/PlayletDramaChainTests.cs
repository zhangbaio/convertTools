using FluentAssertions;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using System.Globalization;
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
        PlayletDramaClient.ResetMemberSessionCache();
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
        var memberIndex = handler.Paths.FindIndex(path => path.EndsWith("/api/business/member/memberInfo", StringComparison.Ordinal));
        var videoIndex = handler.Paths.FindIndex(path => path.EndsWith("/api/drama/hongguo/video", StringComparison.Ordinal));
        memberIndex.Should().BeGreaterThanOrEqualTo(0);
        videoIndex.Should().BeGreaterThan(memberIndex);
        handler.MemberBodies.Should().ContainSingle();
        handler.MemberBodies[0].Should().Contain("deviceId=");
        handler.MemberBodies[0].Should().Contain("memberType=");
        handler.MemberBodies[0].Should().Contain("version=");
        handler.VideoPaths.Should().ContainSingle().Which.Should().EndWith("/api/drama/hongguo/video");
        handler.VideoDeviceId.Should().Be("DC8D94CB6E7122F92E");
        handler.VideoPaths.Should().NotContain(path => path.EndsWith("/decryptVideo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Video_reregisters_member_when_server_returns_unknown_error()
    {
        PlayletDramaClient.ResetMemberSessionCache();
        var handler = new PlayletHandler { RejectVideoUntilSecondMember = true };
        using var httpClient = new HttpClient(handler);

        var video = await PlayletDramaClient.GetVideoAsync(
            httpClient,
            PlayletDramaClient.DefaultServerUrl,
            "7650358399711841304",
            "1080P",
            "DC8D94CB6E7122F92E",
            CancellationToken.None);

        video.Url.Should().Be("https://cdn.example.com/1.mp4");
        handler.MemberBodies.Should().HaveCount(2);
        handler.VideoPaths.Should().HaveCount(6);
        handler.VideoPaths.Take(4).Should().OnlyContain(path => path.EndsWith("/api/drama/hongguo/video", StringComparison.Ordinal));
        handler.VideoPaths[4].Should().EndWith("/api/drama/hongguo/decryptVideo");
        handler.VideoPaths[5].Should().EndWith("/api/drama/hongguo/video");
    }

    [Fact]
    public async Task Rank_page_uses_fanqie_board_and_playlet_book_prefix()
    {
        var handler = new PlayletHandler();
        using var httpClient = new HttpClient(handler);

        var page = await PlayletDramaClient.GetRankPageAsync(
            httpClient,
            "AI剧",
            "推荐榜",
            0,
            CancellationToken.None);

        page.HasMore.Should().BeTrue();
        page.NextOffset.Should().Be(15);
        page.Items.Should().ContainSingle();
        page.Items[0].BookId.Should().Be("playlet:7686713195246930968");
        page.Items[0].Title.Should().Be("榜单剧");
        page.Items[0].EpisodeTotal.Should().Be(69);
        handler.RankUrl.Should().Contain("selected_items=ai_playlet");
        handler.RankUrl.Should().Contain("sub_selected_items=ai_playlet_hot_sc");
    }

    [Fact]
    public async Task Comic_new_today_requests_china_today_and_maps_playlet_books()
    {
        var handler = new PlayletHandler();
        using var httpClient = new HttpClient(handler);

        var items = await PlayletDramaClient.GetComicNewTodayAsync(httpClient, CancellationToken.None);

        items.Should().HaveCount(2);
        items[0].BookId.Should().Be("playlet:7694908515348925465");
        items[0].Title.Should().Be("满级小师妹");
        items[0].EpisodeTotal.Should().Be(108);
        items[0].Intro.Should().Be("简介");
        items[1].BookId.Should().Be("playlet:7692318649381000001");
        items[1].Title.Should().Be("入府为婢");
        items[1].Intro.Should().Be("简介");
        var todayDate = ChinaToday();
        var today = todayDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        items.Should().OnlyContain(item => item.PublishTime == todayDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        handler.ComicNewUrls.Should().HaveCount(2);
        handler.ComicNewUrls[0].Should().Contain($"target_date={today}");
        handler.ComicNewUrls[0].Should().Contain("comic_series");
        handler.ComicNewUrls[0].Should().Contain("tab_style=2");
        handler.ComicNewUrls[0].Should().Contain("need_calendar_schema=true");
        handler.ComicNewUrls[0].Should().Contain("session_id=");
        handler.ComicNewUrls[0].Should().NotContain("session_id=cal-session");
        handler.ComicNewUrls[1].Should().Contain("session_id=cal-session");
        handler.ComicNewUrls[0].Should().NotContain("shortplay");
    }

    private static DateOnly ChinaToday()
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        }

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
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
        public bool RejectVideoUntilSecondMember { get; init; }
        public List<string> Paths { get; } = [];
        public List<string> MemberBodies { get; } = [];
        public List<string> VideoPaths { get; } = [];
        public string? VideoDeviceId { get; private set; }
        public string RankUrl { get; private set; } = "";
        public List<string> ComicNewUrls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            Paths.Add(path);
            if (path.EndsWith("/api/business/member/memberInfo", StringComparison.OrdinalIgnoreCase))
            {
                var body = request.Content is null
                    ? ""
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                MemberBodies.Add(body);
                return Json("""{"code":200,"msg":"操作成功","data":"encrypted"}""");
            }
            if (path.Contains("/search/page/", StringComparison.Ordinal))
            {
                var hasCookie = request.Headers.TryGetValues("cookie", out var cookies) &&
                                cookies.Any(value => !string.IsNullOrWhiteSpace(value));
                if (hasCookie)
                {
                    SearchCookieCount++;
                    return Json("""{"code":6000,"message":"网络错误，请稍后重试"}""");
                }

                SearchNoCookieCount++;
                return Json("""
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
                    """);
            }

            if (path.Contains("/directory/list/", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "code": 0,
                      "data": {
                        "item_list": ["7650358399711841304", "7650358459367427096"]
                      }
                    }
                    """);
            }

            if (path.Contains("/subscribe/list/", StringComparison.Ordinal))
            {
                var url = request.RequestUri?.ToString() ?? "";
                ComicNewUrls.Add(url);
                if (ComicNewUrls.Count == 1)
                {
                    return Json("""
                        {
                          "code": 0,
                          "data": {
                            "has_more": true,
                            "next_offset": 15,
                            "session_id": "cal-session",
                            "subscribe_items": [
                              {
                                "subscribe_data": {
                                  "series_id": "7694908515348925465",
                                  "title": "满级小师妹",
                                  "episode_cnt": 108,
                                  "video_desc": "简介",
                                  "cover": "https://cdn.example.com/cover.jpg"
                                }
                              }
                            ]
                          }
                        }
                        """);
                }

                return Json("""
                    {
                      "code": 0,
                      "data": {
                        "has_more": false,
                        "next_offset": 30,
                        "session_id": "cal-session",
                        "subscribe_items": [
                          {
                            "subscribe_data": {
                              "series_id": "7692318649381000001",
                              "title": "入府为婢",
                              "episode_cnt": 80,
                              "video_desc": "简介"
                            }
                          }
                        ]
                      }
                    }
                    """);
            }

            if (path.Contains("/bookmall/cell/change/", StringComparison.Ordinal))
            {
                RankUrl = request.RequestUri?.ToString() ?? "";
                return Json("""
                    {
                      "code": 0,
                      "data": {
                        "has_more": true,
                        "next_offset": 15,
                        "cell_view": {
                          "cell_data": [
                            {
                              "video_data": [
                                {
                                  "series_id": "7686713195246930968",
                                  "title": "榜单剧",
                                  "episode_cnt": 69,
                                  "video_desc": "简介"
                                }
                              ]
                            }
                          ]
                        }
                      }
                    }
                    """);
            }

            if (path.EndsWith("/api/drama/hongguo/video", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/api/drama/hongguo/decryptVideo", StringComparison.OrdinalIgnoreCase))
            {
                VideoPaths.Add(path);
                if (request.Headers.TryGetValues("deviceId", out var values))
                {
                    VideoDeviceId = values.FirstOrDefault();
                }

                if (RejectVideoUntilSecondMember && MemberBodies.Count < 2)
                {
                    return Json("""{"code":500,"msg":"未知错误"}""");
                }

                return Json(JsonSerializer.Serialize(new
                {
                    code = 200,
                    data = new
                    {
                        type = "dp4",
                        url = "https://cdn.example.com/1.mp4",
                        key = new string('a', 32)
                    }
                }));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"unexpected {request.RequestUri}")
            };
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
