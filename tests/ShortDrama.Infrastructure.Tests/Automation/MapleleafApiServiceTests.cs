using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class MapleleafApiServiceTests
{
    [Fact]
    public async Task ProbeLogin_Uses_165_Headers_And_Reads_AccessToken()
    {
        var handler = new MapleleafHandler();
        var service = CreateService(handler);

        var result = await service.ProbeLoginAsync(Settings(), CancellationToken.None);

        result.Token.Should().Be("maple-token");
        var login = handler.Requests.Single(item => item.Path.EndsWith("/User/login", StringComparison.Ordinal));
        login.Headers["X-Client-Name"].Should().Be("Mapleleaf");
        login.Headers["X-Client-Version"].Should().Be("1.6.5");
        login.Headers["X-Device-Id"].Should().Be("device-guid");
    }

    [Fact]
    public async Task Search_Uses_SearchPhp_And_Maps_RawData()
    {
        var handler = new MapleleafHandler();
        var service = CreateService(handler);

        var items = await service.SearchAsync(Settings(), "测试剧", 2, CancellationToken.None);

        items.Should().ContainSingle();
        items[0].BookId.Should().Be("mapleleaf:book-1");
        items[0].Title.Should().Be("测试剧");
        items[0].EpisodeTotal.Should().Be(24);
        items[0].FavoriteCount.Should().Be(8765);
        handler.Requests.Should().Contain(item => item.Path.EndsWith("/search.php", StringComparison.Ordinal) && item.Body.Contains("\"offset\":10", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("djnew", "short")]
    [InlineData("mjnew", "comic")]
    [InlineData("aiju", "ai_real")]
    public async Task Latest_Uses_165_Type_Names(string mode, string expectedType)
    {
        var handler = new MapleleafHandler();
        var service = CreateService(handler);

        var items = await service.GetLatestAsync(Settings(), mode, 1, CancellationToken.None);

        items.Should().ContainSingle();
        var request = handler.Requests.Single(item => item.Path.EndsWith("/ThirdParty/latest", StringComparison.Ordinal));
        request.Body.Should().Contain($"\"type\":\"{expectedType}\"");
        request.Body.Should().Contain($"\"action\":\"{expectedType}\"");
    }

    [Fact]
    public async Task Latest_Follows_HasMore_Pages_And_Deduplicates_Books()
    {
        var handler = new MapleleafHandler(latestHasMore: true);
        var service = CreateService(handler);

        var items = await service.GetLatestAsync(Settings(), "mjnew", 1, CancellationToken.None);

        items.Select(item => item.BookId).Should().Equal("mapleleaf:latest-1", "mapleleaf:latest-2");
        var requests = handler.Requests
            .Where(item => item.Path.EndsWith("/ThirdParty/latest", StringComparison.Ordinal))
            .OrderBy(item => item.Body.Contains("\"page\":1", StringComparison.Ordinal) ? 1 : 2)
            .ToArray();
        requests.Should().HaveCount(2);
        requests[0].Body.Should().Contain("\"type\":\"comic\"");
        requests[0].Body.Should().Contain("\"page\":1");
        requests[1].Body.Should().Contain("\"page\":2");
    }

    [Fact]
    public async Task Episodes_And_VideoParse_Keep_Mapleleaf_Provenance()
    {
        var handler = new MapleleafHandler();
        var service = CreateService(handler);

        var episodes = await service.GetEpisodesAsync(Settings(), "mapleleaf:book-1", CancellationToken.None);
        var playback = await service.GetVideoPlaybackAsync(Settings(), episodes[0].VideoId, "1080P", CancellationToken.None);

        episodes.Should().ContainSingle();
        episodes[0].EpisodeNumber.Should().Be(3);
        episodes[0].VideoId.Should().Be("mapleleaf_ep:video-3");
        playback.Url.Should().Be("https://cdn.example/video-3.mp4");
    }

    [Fact]
    public async Task Concurrent_Searches_Login_Only_Once()
    {
        var handler = new MapleleafHandler(delayLogin: true);
        var service = CreateService(handler);

        await Task.WhenAll(
            service.SearchAsync(Settings(), "甲", 1, CancellationToken.None),
            service.SearchAsync(Settings(), "乙", 1, CancellationToken.None));

        handler.LoginCount.Should().Be(1);
    }

    [Fact]
    public void DeviceId_Generator_Returns_Guid()
    {
        Guid.TryParse(MapleleafDeviceStore.GenerateDeviceId(), out _).Should().BeTrue();
    }

    private static MapleleafApiService CreateService(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        return new MapleleafApiService(
            http,
            new HongguoLocalApiService(http),
            ["https://maple.test/api"],
            "https://maple.test/index.php");
    }

    private static DramaSourceSettings Settings() => new()
    {
        DramaSourceChain = "mapleleaf",
        MapleleafAccount = "member@example.com",
        MapleleafPassword = "secret",
        MapleleafUdid = "device-guid"
    };

    private sealed class MapleleafHandler(bool delayLogin = false, bool latestHasMore = false) : HttpMessageHandler
    {
        public ConcurrentBag<CapturedRequest> Requests { get; } = [];
        public int LoginCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(
                item => item.Key,
                item => string.Join(",", item.Value),
                StringComparer.OrdinalIgnoreCase);
            Requests.Add(new CapturedRequest(request.RequestUri!.AbsolutePath, body, headers));

            if (request.RequestUri.AbsolutePath.EndsWith("/User/login", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref LoginCount);
                if (delayLogin) await Task.Delay(30, cancellationToken);
                return Json("""{"success":true,"data":{"accessToken":"maple-token","email":"member@example.com"}}""");
            }

            request.Headers.Authorization?.Scheme.Should().Be("Bearer");
            request.Headers.Authorization?.Parameter.Should().Be("maple-token");

            if (request.RequestUri.AbsolutePath.EndsWith("/search.php", StringComparison.Ordinal))
            {
                return Json("""{"success":true,"data":{"rawData":"{\"data\":[{\"bookId\":\"book-1\",\"title\":\"测试剧\",\"episodeCount\":24,\"playCnt\":8765,\"cover\":\"https://img.example/1.jpg\"}]}"}}""");
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/ThirdParty/videolist", StringComparison.Ordinal))
                return Json("""{"success":true,"data":{"data":[{"videoId":"video-3","title":"第3集"}]}}""");

            if (request.RequestUri.AbsolutePath.EndsWith("/ThirdParty/videoparse", StringComparison.Ordinal))
                return Json("""{"success":true,"data":{"url":"https://cdn.example/video-3.mp4","size":"12MB"}}""");

            if (request.RequestUri.AbsolutePath.EndsWith("/ThirdParty/latest", StringComparison.Ordinal))
            {
                if (latestHasMore && body.Contains("\"page\":1", StringComparison.Ordinal))
                    return Json("""{"success":true,"data":{"has_more":true,"data":[{"bookId":"latest-1","title":"上新剧一"}]}}""");
                if (latestHasMore)
                    return Json("""{"success":true,"data":{"has_more":false,"data":[{"bookId":"latest-1","title":"上新剧一"},{"bookId":"latest-2","title":"上新剧二"}]}}""");
                return Json("""{"success":true,"data":{"has_more":false,"data":[{"bookId":"latest-1","title":"上新剧"}]}}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"success\":false,\"message\":\"not found\"}", Encoding.UTF8, "application/json")
            };
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed record CapturedRequest(string Path, string Body, IReadOnlyDictionary<string, string> Headers);
}
