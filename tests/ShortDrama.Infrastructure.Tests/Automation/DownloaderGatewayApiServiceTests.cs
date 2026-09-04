using System.Net;
using System.Text;
using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class DownloaderGatewayApiServiceTests
{
    [Fact]
    public async Task Search_UsesIndependentGatewaySettingsAndOpaqueReferences()
    {
        var handler = new StubHandler(_ => Json("""
            {"results":[{"series_id":"standard|book-1","title":"剧名","source":"standard","episode_cnt":35}]}
            """));
        var service = new DownloaderGatewayApiService(new HttpClient(handler));

        var items = await service.SearchAsync(Settings(), "剧名", 1, CancellationToken.None);

        items.Should().ContainSingle();
        items[0].BookId.Should().Be("downloader:standard|book-1");
        handler.Request!.RequestUri!.AbsolutePath.Should().Be("/api/v1/catalog/search");
        handler.Request.Headers.GetValues("x-api-key").Single().Should().Be("gateway-key");
    }

    [Fact]
    public async Task EpisodesAndPlayback_PreserveDownloaderOpaqueIdsAndEncryptionMetadata()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("episodes")
            ? Json("""{"episodes":[{"vid":"standard|book-1|video-1|1","index":1,"title":"第1集"}]}""")
            : Json("""{"url":"https://cdn.example/video.mp4","spade_a":"material","encrypt":true}"""));
        var service = new DownloaderGatewayApiService(new HttpClient(handler));

        var episodes = await service.GetEpisodesAsync(Settings(), "downloader:standard|book-1", CancellationToken.None);
        var playback = await service.GetPlaybackAsync(Settings(), episodes[0].VideoId, "1080p", CancellationToken.None);

        episodes[0].VideoId.Should().Be("downloader_ep:standard|book-1|video-1|1");
        playback.Url.Should().Be("https://cdn.example/video.mp4");
        playback.SpadeA.Should().Be("material");
        playback.Encrypted.Should().BeTrue();
    }

    [Fact]
    public async Task Health_AlsoProbesAuthenticatedCapabilities()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHandler(request =>
        {
            requests.Add(request);
            return request.RequestUri!.AbsolutePath.EndsWith("health")
                ? Json("""{"ok":true,"activeSource":"high","highEdition":"standard"}""")
                : Json("""{"apiVersion":"1.0"}""");
        });
        var health = await new DownloaderGatewayApiService(new HttpClient(handler))
            .GetHealthAsync(Settings(), CancellationToken.None);

        health.Ok.Should().BeTrue();
        health.HighEdition.Should().Be("standard");
        requests.Should().HaveCount(2);
        requests[1].Headers.GetValues("x-api-key").Single().Should().Be("gateway-key");
    }

    [Fact]
    public async Task Live_AutoDiscoversKeyAndStartsInstalledDownloader_WhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("DOWNLOADER_GATEWAY_LIVE_TEST") != "1") return;
        var settings = new DramaSourceSettings
        {
            DownloaderApiBaseUrl = "http://127.0.0.1:17891",
            DownloaderApiKey = ""
        };

        var health = await new DownloaderGatewayApiService(new HttpClient())
            .GetHealthAsync(settings, CancellationToken.None);

        health.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Live_SearchEpisodesAndPlayback_RoundTripsThroughInstalledDownloader_WhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("DOWNLOADER_GATEWAY_FULL_LIVE_TEST") != "1") return;
        var settings = new DramaSourceSettings
        {
            DownloaderApiBaseUrl = "http://127.0.0.1:17891",
            DownloaderApiKey = ""
        };
        var keyword = Environment.GetEnvironmentVariable("DOWNLOADER_GATEWAY_TEST_KEYWORD") ?? "人间烟火热";
        var service = new DownloaderGatewayApiService(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });

        var results = await service.SearchAsync(settings, keyword, 1, CancellationToken.None);
        results.Should().NotBeEmpty();
        DownloaderGatewayApiService.GatewayPlayback? playback = null;
        foreach (var result in results.Take(3))
        {
            var episodes = await service.GetEpisodesAsync(settings, result.BookId, CancellationToken.None);
            foreach (var episode in episodes.Take(5))
            {
                try
                {
                    playback = await service.GetPlaybackAsync(
                        settings, episode.VideoId, "1080p", CancellationToken.None);
                    break;
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("视频流", StringComparison.Ordinal))
                {
                    // Some upstream records have no playable rendition; continue probing the returned catalog.
                }
            }
            if (playback is not null) break;
        }

        playback.Should().NotBeNull();
        playback!.Url.Should().StartWith("http");
    }

    private static DramaSourceSettings Settings() => new()
    {
        DownloaderApiBaseUrl = "http://127.0.0.1:17891",
        DownloaderApiKey = "gateway-key"
    };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(responder(request));
        }
    }
}
