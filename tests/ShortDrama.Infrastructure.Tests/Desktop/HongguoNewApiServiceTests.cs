using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Desktop;

public sealed class HongguoNewApiServiceTests
{
    [Fact]
    public async Task SearchAsync_Should_Use_Rest_Login_And_Map_Search_Items()
    {
        var handler = new RecordingHandler();
        handler.EnqueueJson(RestOk(new
        {
            accessToken = "jwt-token-1",
            email = "test@example.com",
            isMember = true,
            memberEndDate = "2026-08-23T06:35:47",
            expiresIn = 3600
        }, "登录成功"));
        handler.EnqueueJson(RestWrapped(new
        {
            code = 200,
            msg = "综合搜索成功",
            data = new object[]
            {
                new
                {
                    book_id = "book-1",
                    title = "婆婆今天也很飒",
                    type = "家庭 12集",
                    episode_cnt = 12,
                    intro = "测试简介",
                    cover = "https://example.com/poster.jpg",
                    author = "测试作者",
                    publish_time = "2026-06-14 10:20:30"
                }
            }
        }, "综合搜索成功（会员免扣点）"));

        var service = new HongguoNewApiService(new HttpClient(handler));
        var settings = CreateSettings();

        var results = await service.SearchAsync(settings, "婆婆", 1, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].BookId.Should().Be("book-1");
        results[0].Title.Should().Be("婆婆今天也很飒");
        results[0].EpisodeTotal.Should().Be(12);
        results[0].PosterUrl.Should().Be("https://example.com/poster.jpg");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/User/login");
        handler.Requests[1].RequestUri!.AbsolutePath.Should().Be("/api/ThirdParty/unified-search");
        handler.Requests[0].Headers.GetValues("X-Client-Version").Single().Should().Be("1.5.0");
        handler.Requests[0].Headers.GetValues("X-Device-Id").Single().Should().Be("42ce0f9242ea893b241749e35cf894be");
        handler.Requests[1].Headers.Authorization?.Parameter.Should().Be("jwt-token-1");
    }

    [Fact]
    public async Task GetDailyByDatesAsync_Should_Filter_By_Publish_Date()
    {
        var handler = new RecordingHandler();
        handler.EnqueueJson(RestOk(new { accessToken = "jwt-token-1", expiresIn = 3600 }, "登录成功"));
        handler.EnqueueJson(RestWrapped(new
        {
            code = 200,
            message = "今日上新解析成功",
            warming = false,
            ready = true,
            data = new object[]
            {
                new
                {
                    book_id = "today-1",
                    title = "今天的漫剧",
                    type = "漫剧",
                    episode_cnt = 20,
                    publish_time = "2026-06-14 09:30:00"
                },
                new
                {
                    book_id = "old-1",
                    title = "昨天的漫剧",
                    type = "漫剧",
                    episode_cnt = 18,
                    publish_time = "2026-06-13 21:00:00"
                }
            }
        }, "获取最新上架成功（会员免扣点）"));

        var service = new HongguoNewApiService(new HttpClient(handler));
        var settings = CreateSettings();

        var items = await service.GetDailyByDatesAsync(
            settings,
            "mjnew",
            [new DateOnly(2026, 6, 14)],
            CancellationToken.None);

        items.Should().HaveCount(1);
        items[0].BookId.Should().Be("today-1");
        items[0].Title.Should().Be("今天的漫剧");
        handler.Requests[1].RequestUri!.AbsolutePath.Should().Be("/api/ThirdParty/latest");
    }

    [Fact]
    public async Task GetEpisodesAsync_Should_Map_Episodes_From_VideoList()
    {
        var handler = new RecordingHandler();
        handler.EnqueueJson(RestOk(new { accessToken = "jwt-token-1", expiresIn = 3600 }, "登录成功"));
        handler.EnqueueJson(RestWrapped(new
        {
            code = 200,
            msg = "获取列表成功",
            data = new object[]
            {
                new { title = "第01集", video_id = "video-1" },
                new { title = "第02集", video_id = "video-2" }
            }
        }, "获取视频列表成功（会员免扣点）"));

        var service = new HongguoNewApiService(new HttpClient(handler));
        var settings = CreateSettings();

        var episodes = await service.GetEpisodesAsync(settings, "book-1", CancellationToken.None);

        episodes.Should().HaveCount(2);
        episodes[0].EpisodeNumber.Should().Be(1);
        episodes[0].VideoId.Should().Be("video-1");
        episodes[1].EpisodeNumber.Should().Be(2);
        episodes[1].VideoId.Should().Be("video-2");
        handler.Requests[1].RequestUri!.AbsolutePath.Should().Be("/api/ThirdParty/videolist");
    }

    [Fact]
    public async Task GetEpisodesAsync_Should_Relogin_When_Rest_Unauthorized()
    {
        var handler = new RecordingHandler();
        handler.EnqueueJson(RestOk(new { accessToken = "jwt-token-1", expiresIn = 3600 }, "登录成功"));
        handler.EnqueueJson(JsonSerializer.Serialize(new
        {
            success = false,
            message = "请重新登录",
            data = (object?)null
        }));
        handler.EnqueueJson(RestOk(new { accessToken = "jwt-token-2", expiresIn = 3600 }, "登录成功"));
        handler.EnqueueJson(RestWrapped(new
        {
            code = 200,
            msg = "获取列表成功",
            data = new object[]
            {
                new { title = "第1集", video_id = "video-1" }
            }
        }, "获取视频列表成功（会员免扣点）"));

        var service = new HongguoNewApiService(new HttpClient(handler));
        var settings = CreateSettings();

        var episodes = await service.GetEpisodesAsync(settings, "book-1", CancellationToken.None);

        episodes.Should().HaveCount(1);
        episodes[0].VideoId.Should().Be("video-1");
        handler.Requests.Should().HaveCount(4);
        handler.Requests[1].RequestUri!.AbsolutePath.Should().Be("/api/ThirdParty/videolist");
        handler.Requests[1].Headers.Authorization?.Parameter.Should().Be("jwt-token-1");
        handler.Requests[3].RequestUri!.AbsolutePath.Should().Be("/api/ThirdParty/videolist");
        handler.Requests[3].Headers.Authorization?.Parameter.Should().Be("jwt-token-2");
    }

    [Fact]
    public async Task GetVideoPlaybackAsync_Should_Return_Url_And_Size()
    {
        var handler = new RecordingHandler();
        handler.EnqueueJson(RestOk(new { accessToken = "jwt-token-1", expiresIn = 3600 }, "登录成功"));
        handler.EnqueueJson(RestWrapped(new
        {
            code = 200,
            msg = "解析成功",
            url = "https://example.com/video.mp4",
            data = new
            {
                url = "https://example.com/video.mp4",
                info = new
                {
                    size = "12.45MB"
                }
            }
        }, "视频解析成功（会员免扣点）"));

        var service = new HongguoNewApiService(new HttpClient(handler));
        var settings = CreateSettings();

        var detail = await service.GetVideoPlaybackAsync(settings, "video-1", "1080P+", CancellationToken.None);

        detail.Url.Should().Be("https://example.com/video.mp4");
        detail.Size.Should().Be((long)(12.45 * 1024 * 1024));
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].RequestUri!.AbsolutePath.Should().Be("/api/ThirdParty/videoparse");
    }

    [Fact]
    public void HongguoDeviceId_Normalize_Keeps_Hex_Lowercase_And_Guid_Uppercase()
    {
        HongguoDeviceId.Normalize("42CE0F9242EA893B241749E35CF894BE")
            .Should().Be("42ce0f9242ea893b241749e35cf894be");
        HongguoDeviceId.Normalize("64437e32-40bb-440c-8300-99232d63e8f7")
            .Should().Be("64437E32-40BB-440C-8300-99232D63E8F7");
    }

    [Fact]
    public void HongguoClientVersion_Should_Keep_Aes_Patch_And_Rest_Threshold()
    {
        HongguoClientVersion.Default.Should().Be("1.4.1");
        HongguoClientVersion.IsRest("1.5.0").Should().BeTrue();
        HongguoClientVersion.IsRest("1.5.1").Should().BeTrue();
        HongguoClientVersion.IsRest("1.4.1").Should().BeFalse();
        HongguoClientVersion.IsRest("1.4.2").Should().BeFalse();

        HongguoClientVersion.Normalize("1.5.0").Should().Be("1.5.0");
        HongguoClientVersion.Normalize("1.4.1").Should().Be("1.4.1");
        HongguoClientVersion.Normalize("1.4.2").Should().Be("1.4.2");
        HongguoClientVersion.Normalize("1.3.9").Should().Be("1.4.1");
        HongguoClientVersion.BuildAesBaseUrl("1.4.2")
            .Should().Be("https://au.s1o.cc/api/user/1000/win/1.4.2");
    }

    [Fact]
    public async Task SearchAsync_Should_Hit_Aes_Host_When_Version_Is_14x()
    {
        var handler = new RecordingHandler();
        // 返回不可解密的假密文即可：只要发出请求就能断言 host/version
        handler.EnqueueJson("""{"code":0,"data":"not-valid-ciphertext"}""");

        var service = new HongguoNewApiService(new HttpClient(handler));
        var settings = new DramaSourceSettings
        {
            DramaSourceChain = "hgnew",
            HgnewAccount = "test@example.com",
            HgnewPassword = "secret",
            HgnewUdid = "64437E32-40BB-440C-8300-99232D63E8F7",
            HgnewClientVersion = "1.4.2",
            PikachuDramaType = "short",
        };

        var act = async () => await service.SearchAsync(settings, "测试", 1, CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();

        handler.Requests.Should().NotBeEmpty();
        handler.Requests[0].RequestUri!.Host.Should().Be("au.s1o.cc");
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Contain("/win/1.4.2/");
        handler.Requests[0].Headers.GetValues("X-Client-Version").Single().Should().Be("1.4.2");
    }
    private static DramaSourceSettings CreateSettings()
    {
        return new DramaSourceSettings
        {
            DramaSourceChain = "hgnew",
            HgnewAccount = "test@example.com",
            HgnewPassword = "secret",
            // 1.5.0 DeviceId：32hex 小写（避免测试机注册表 GUID 干扰）
            HgnewUdid = "42ce0f9242ea893b241749e35cf894be",
            HgnewClientVersion = "1.5.0",
            PikachuDramaType = "short",
        };
    }

    private static string RestOk(object data, string message) =>
        JsonSerializer.Serialize(new
        {
            success = true,
            message,
            data
        });

    private static string RestWrapped(object inner, string message) =>
        JsonSerializer.Serialize(new
        {
            success = true,
            message,
            data = new
            {
                success = true,
                message,
                rawData = JsonSerializer.Serialize(inner)
            }
        });

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public void EnqueueJson(string json)
        {
            _responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(CloneRequest(request));
            return Task.FromResult(_responses.Dequeue());
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            // 保留 URI/Headers 供断言；Content 不克隆
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }
    }
}
