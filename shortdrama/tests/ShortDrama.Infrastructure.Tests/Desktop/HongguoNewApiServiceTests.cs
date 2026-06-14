using FluentAssertions;
using ShortDrama.Desktop.Models;
using ShortDrama.Desktop.Services;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Desktop;

public sealed class HongguoNewApiServiceTests
{
    private const string AppKey = "c8b9d4a1f3e265c89a0b1d3f4e5a6c7b";
    private static readonly byte[] AesKey = Encoding.UTF8.GetBytes("asKVK4K5tEPg4inz");

    [Fact]
    public async Task SearchAsync_Should_Use_Hgnew_Login_And_Map_Search_Items()
    {
        var handler = new RecordingHandler();
        handler.EnqueueJson(EncryptOuter(new
        {
            state = "y",
            token = "token-1"
        }));
        handler.EnqueueJson(EncryptOuter(new
        {
            code = 200,
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
                    publish_time = "2026-06-14 10:20:30",
                    favorite_count = 1234
                }
            },
            message = "ok"
        }));

        var service = new HongguoNewApiService(new HttpClient(handler));
        var settings = CreateSettings();

        var results = await service.SearchAsync(settings, "婆婆", 1, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].BookId.Should().Be("book-1");
        results[0].Title.Should().Be("婆婆今天也很飒");
        results[0].EpisodeTotal.Should().Be(12);
        results[0].PosterUrl.Should().Be("https://example.com/poster.jpg");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().EndWith("/logon");
        handler.Requests[1].RequestUri!.AbsolutePath.Should().EndWith("/cloudFunction");
        handler.Requests[0].Headers.GetValues("X-Client-Version").Single().Should().Be("1.3.6");
    }

    [Fact]
    public async Task GetDailyByDatesAsync_Should_Filter_By_Publish_Date()
    {
        var handler = new RecordingHandler();
        handler.EnqueueJson(EncryptOuter(new
        {
            state = "y",
            token = "token-1"
        }));
        handler.EnqueueJson(EncryptOuter(new
        {
            code = 200,
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
            },
            message = "ok"
        }));

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
    }

    [Fact]
    public async Task GetEpisodesAsync_Should_Map_Episodes_From_Book_Data()
    {
        var handler = new RecordingHandler();
        handler.EnqueueJson(EncryptOuter(new
        {
            state = "y",
            token = "token-1"
        }));
        handler.EnqueueJson(EncryptOuter(new
        {
            code = 200,
            data = new object[]
            {
                new { title = "第01集", video_id = "video-1" },
                new { title = "第02集", video_id = "video-2" }
            },
            message = "ok"
        }));

        var service = new HongguoNewApiService(new HttpClient(handler));
        var settings = CreateSettings();

        var episodes = await service.GetEpisodesAsync(settings, "book-1", CancellationToken.None);

        episodes.Should().HaveCount(2);
        episodes[0].EpisodeNumber.Should().Be(1);
        episodes[0].VideoId.Should().Be("video-1");
        episodes[1].EpisodeNumber.Should().Be(2);
        episodes[1].VideoId.Should().Be("video-2");
    }

    [Fact]
    public async Task GetVideoPlaybackAsync_Should_Return_Url_And_Size()
    {
        var handler = new RecordingHandler();
        handler.EnqueueJson(EncryptOuter(new
        {
            state = "y",
            token = "token-1"
        }));
        handler.EnqueueJson(EncryptOuter(new
        {
            code = 0,
            data = new { pong = true }
        }));
        handler.EnqueueJson(EncryptOuter(new
        {
            code = 200,
            data = new
            {
                url = "https://example.com/video.mp4",
                info = new
                {
                    size = 123456789
                }
            },
            message = "ok"
        }));

        var service = new HongguoNewApiService(new HttpClient(handler));
        var settings = CreateSettings();

        var detail = await service.GetVideoPlaybackAsync(settings, "video-1", "1080P+", CancellationToken.None);

        detail.Url.Should().Be("https://example.com/video.mp4");
        detail.Size.Should().Be(123456789);
        handler.Requests.Should().HaveCount(3);
        handler.Requests[1].RequestUri!.AbsolutePath.Should().EndWith("/info");
        handler.Requests[2].RequestUri!.AbsolutePath.Should().EndWith("/cloudFunction");
    }

    private static GlobalConfigSnapshot CreateSettings()
    {
        return new GlobalConfigSnapshot(
            SettingsFilePath: "C:\\temp\\global-settings.json",
            DramaSourceChain: "hgnew",
            DramaServiceOrderSearch: "hgnew,hglocal,pikachu",
            DramaServiceOrderDownload: "hgnew,hglocal,pikachu",
            DramaServiceOrderNewRelease: "hgnew,hglocal",
            DramaServiceOrderRanking: "hglocal,pikachu",
            XingeEnabled: false,
            XingeServerUrl: string.Empty,
            XingeUsername: string.Empty,
            XingePassword: string.Empty,
            XingeClientId: string.Empty,
            XingeClientToken: string.Empty,
            XingeUserRole: string.Empty,
            XingeClientName: string.Empty,
            XingeWsEnabled: true,
            XingePollIntervalSeconds: "3",
            XingeUploadLoginQr: true,
            HgnewAccount: "test@example.com",
            HgnewPassword: "secret",
            HgnewUdid: "64437E32-40BB-440C-8300-99232D63E8F7",
            HgnewClientVersion: "1.3.6",
            HongguoLocalBaseUrl: string.Empty,
            HongguoLocalApiKey: string.Empty,
            PikachuServerUrl: string.Empty,
            PikachuFanqieCookie: string.Empty,
            PikachuDramaType: "short",
            AiTextEndpoint: string.Empty,
            AiTextApiKey: string.Empty,
            AiTextModel: string.Empty,
            AiTextTimeoutSeconds: string.Empty,
            AiTextMaxBatchSize: string.Empty,
            AiTextSystemPrompt: string.Empty,
            AiTextBatchPrompt: string.Empty,
            AiTextRetryPrompt: string.Empty,
            ImageModelId: string.Empty,
            ImageModelApiKey: string.Empty,
            ImageModelEndpoint: string.Empty,
            ImageEditModelId: string.Empty,
            ImageEditApiKey: string.Empty,
            ImageEditEndpoint: string.Empty,
            ImageEditPath: string.Empty,
            PosterLayoutDetectPrompt: string.Empty,
            PosterInpaintPrompt: string.Empty,
            PosterInpaintSafeRetryPrompt: string.Empty,
            PosterGenerationPrompt: string.Empty,
            PosterGenerationSafeRetryPrompt: string.Empty,
            PosterNameSystemPrompt: string.Empty,
            PosterNameUserPrompt: string.Empty,
            FeishuNotificationEnabled: false,
            FeishuAppId: string.Empty,
            FeishuAppSecret: string.Empty,
            FeishuReceiveId: string.Empty,
            FeishuReceiveIdType: "chat_id",
            FeishuNotifyOnStepStart: false,
            FeishuNotifyOnStepSuccess: true,
            FeishuNotifyOnStepFailure: true,
            FeishuNotifyOnQueueSummary: true,
            FeishuNotifyOnLoginQr: true,
            FeishuNotifyStepKeysText: string.Empty);
    }

    private static string EncryptOuter(object innerData)
    {
        var plainJson = JsonSerializer.Serialize(innerData);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = AesKey;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainJson);
        var encrypted = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var payload = new byte[aes.IV.Length + encrypted.Length];
        Buffer.BlockCopy(aes.IV, 0, payload, 0, aes.IV.Length);
        Buffer.BlockCopy(encrypted, 0, payload, aes.IV.Length, encrypted.Length);

        var outer = new
        {
            code = 0,
            msg = "ok",
            data = Convert.ToBase64String(payload)
        };
        return JsonSerializer.Serialize(outer);
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
