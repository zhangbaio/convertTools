using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Desktop.Models;
using ShortDrama.Desktop.Services;
using System.Net;
using System.Text;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Desktop;

public sealed class HongguoLocalApiServiceTests
{
    [Fact]
    public async Task SearchAsync_Should_Map_Search_Items_And_Prefix_BookId()
    {
        var handler = new RecordingHandler();
        handler.EnqueueJson("""
            {
              "results": [
                {
                  "series_id": "series-1",
                  "title": "云后归来",
                  "category": "都市·68集",
                  "episode_cnt": 68,
                  "intro": "测试简介",
                  "cover": "https://example.com/poster.jpg",
                  "author": "测试作者",
                  "first_seen": "2026-06-14 19:50:18",
                  "favorite_count": 100
                }
              ]
            }
            """);

        var service = new HongguoLocalApiService(new HttpClient(handler));

        var results = await service.SearchAsync(CreateSettings(), "云后归来", 1, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].BookId.Should().Be("hglocal:series-1");
        results[0].Title.Should().Be("云后归来");
        results[0].EpisodeTotal.Should().Be(68);
        results[0].PosterUrl.Should().Be("https://example.com/poster.jpg");
        handler.Requests.Single().RequestUri!.ToString().Should().Contain("/api/hongguo/search?");
        handler.Requests.Single().Headers.GetValues("x-api-key").Single().Should().Be("local-key");
    }

    [Fact]
    public async Task GetTodayNewAsync_Should_Filter_Today_Items()
    {
        var handler = new RecordingHandler();
        handler.EnqueueJson("""
            {
              "items": [
                {
                  "series_id": "today-1",
                  "title": "今日短剧",
                  "episode_cnt": 20,
                  "today": true,
                  "first_seen": "2026-06-14 10:20:30"
                },
                {
                  "series_id": "old-1",
                  "title": "昨日短剧",
                  "episode_cnt": 18,
                  "today": false,
                  "first_seen": "2026-06-13 21:00:00"
                }
              ]
            }
            """);

        var service = new HongguoLocalApiService(new HttpClient(handler));

        var items = await service.GetTodayNewAsync(CreateSettings(), "short_play", CancellationToken.None);

        items.Should().HaveCount(1);
        items[0].BookId.Should().Be("hglocal:today-1");
        items[0].Title.Should().Be("今日短剧");
    }

    [Fact]
    public async Task GetLatestByGenreAsync_Should_Filter_By_Recent_Days()
    {
        var handler = new RecordingHandler();
        handler.EnqueueJson("""
            {
              "items": [
                {
                  "series_id": "recent-1",
                  "title": "最近上新",
                  "episode_cnt": 12,
                  "first_seen": "2026-06-14 09:30:00"
                },
                {
                  "series_id": "old-1",
                  "title": "更早上新",
                  "episode_cnt": 10,
                  "first_seen": "2026-06-01 09:30:00"
                }
              ]
            }
            """);

        var service = new HongguoLocalApiService(new HttpClient(handler));

        var items = await service.GetLatestByGenreAsync(CreateSettings(), "comic_series", 3, CancellationToken.None);

        items.Should().ContainSingle();
        items[0].BookId.Should().Be("hglocal:recent-1");
    }

    [Fact]
    public async Task GetEpisodesAsync_Should_Map_And_Prefix_VideoId()
    {
        var handler = new RecordingHandler();
        handler.EnqueueJson("""
            {
              "episodes": [
                { "index": 1, "title": "第1集", "vid": "video-1", "cover": "https://example.com/1.jpg" },
                { "index": 2, "title": "第2集", "video_id": "video-2", "cover": "https://example.com/2.jpg" }
              ]
            }
            """);

        var service = new HongguoLocalApiService(new HttpClient(handler));

        var items = await service.GetEpisodesAsync(CreateSettings(), "hglocal:series-1", CancellationToken.None);

        items.Should().HaveCount(2);
        items[0].VideoId.Should().Be("hglocal_ep:video-1");
        items[1].VideoId.Should().Be("hglocal_ep:video-2");
        handler.Requests.Single().RequestUri!.ToString().Should().Contain("series_id=series-1");
    }

    [Fact]
    public async Task GetVideoPlaybackAsync_Should_Accept_PlayUrl_Alias()
    {
        var handler = new RecordingHandler();
        handler.EnqueueJson("""
            {
              "playUrl": "https://example.com/video.mp4",
              "size": 12345
            }
            """);

        var service = new HongguoLocalApiService(new HttpClient(handler));

        var playback = await service.GetVideoPlaybackAsync(CreateSettings(), "hglocal_ep:video-1", CancellationToken.None);

        playback.Url.Should().Be("https://example.com/video.mp4");
        handler.Requests.Single().RequestUri!.ToString().Should().Contain("vid=video-1");
    }

    private static GlobalConfigSnapshot CreateSettings()
    {
        return CreateSnapshot(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SettingsFilePath"] = "C:\\temp\\global-settings.json",
            ["DramaSourceChain"] = "hglocal",
            ["DramaServiceOrderSearch"] = "hglocal,hgnew,pikachu",
            ["DramaServiceOrderDownload"] = "hglocal,hgnew,pikachu",
            ["DramaServiceOrderNewRelease"] = "hglocal,hgnew",
            ["DramaServiceOrderRanking"] = "hglocal",
            ["XingeEnabled"] = false,
            ["XingeWsEnabled"] = true,
            ["XingePollIntervalSeconds"] = "3",
            ["XingeUploadLoginQr"] = true,
            ["HgnewClientVersion"] = "1.3.6",
            ["HongguoLocalBaseUrl"] = "https://local.example.com",
            ["HongguoLocalApiKey"] = "local-key",
            ["PikachuDramaType"] = "short",
            ["FeishuReceiveIdType"] = "chat_id",
            ["FeishuNotifyOnStepSuccess"] = true,
            ["FeishuNotifyOnStepFailure"] = true,
            ["FeishuNotifyOnQueueSummary"] = true,
            ["FeishuNotifyOnLoginQr"] = true,
        });
    }

    private static GlobalConfigSnapshot CreateSnapshot(IReadOnlyDictionary<string, object?> values)
    {
        var ctor = typeof(GlobalConfigSnapshot).GetConstructors().Single();
        var args = ctor.GetParameters()
            .Select(parameter => values.TryGetValue(parameter.Name ?? string.Empty, out var value)
                ? value
                : GetDefaultValue(parameter.ParameterType))
            .ToArray();
        return (GlobalConfigSnapshot)ctor.Invoke(args);
    }

    private static object? GetDefaultValue(Type type)
    {
        if (type == typeof(string))
        {
            return string.Empty;
        }

        if (type == typeof(bool))
        {
            return false;
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

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
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
