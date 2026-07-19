using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class BaiduVodSubtitleClientTests
{
    [Fact]
    public void Authorization_uses_bce_v1_host_signature_and_canonical_query()
    {
        var authorization = BaiduVodSubtitleClient.GenerateAuthorizationForTest(
            "GET", "/v2/desubtitle/list",
            new Dictionary<string, string> { ["taskId"] = "tsk 测试" },
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "2015-04-27T08:23:49Z");

        authorization.Should().MatchRegex(
            "^bce-auth-v1/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/2015-04-27T08:23:49Z/1800/host/[0-9a-f]{64}$");
    }

    [Fact]
    public async Task Upload_start_and_query_follow_the_baidu_vod_contract()
    {
        var handler = new RecordingHandler();
        var settings = new ClientSettings
        {
            BaiduSubtitleAccessKeyId = "ak",
            BaiduSubtitleSecretAccessKey = "sk",
            BaiduSubtitleEndpoint = "https://vod.bj.baidubce.com",
            BaiduSubtitleModelType = "v4",
            BaiduSubtitleType = "dialog",
        };
        var client = new BaiduVodSubtitleClient(settings, new HttpClient(handler));
        var path = Path.Combine(Path.GetTempPath(), $"baidu-vod-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes("video"));
        try
        {
            var mediaId = await client.UploadMediaAsync(path, null, CancellationToken.None);
            var taskId = await client.StartSubtitleRemovalAsync(mediaId, CancellationToken.None);
            var result = await client.GetSubtitleRemovalAsync(taskId, CancellationToken.None);

            mediaId.Should().Be("mda-test");
            taskId.Should().Be("tsk-test");
            result.Should().Be(new BaiduSubtitleResult("FINISHED", "https://output/video.mp4", ""));
            handler.Requests.Should().Contain(request => request.Path == "/v2/medias/upload" &&
                request.Body.Contains("\"isMultipartUpload\":false"));
            handler.Requests.Should().Contain(request => request.Path == "/v2/desubtitle" &&
                request.Body.Contains("\"modelType\":\"v4\"") && request.Body.Contains("\"subtitleType\":\"dialog\""));
            handler.Requests.Where(request => request.Path.StartsWith("/v2/", StringComparison.Ordinal))
                .Should().OnlyContain(request => request.Authorization.StartsWith("bce-auth-v1/ak/"));
        }
        finally { File.Delete(path); }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            request.Headers.TryGetValues("Authorization", out var authorizationValues);
            Requests.Add(new CapturedRequest(request.RequestUri!.AbsolutePath, body,
                request.Headers.Authorization?.ToString() ?? authorizationValues?.SingleOrDefault() ?? ""));

            var json = request.RequestUri.AbsolutePath switch
            {
                "/v2/medias/upload" => "{\"sessionKey\":\"session\",\"urls\":[\"https://upload.example/video.mp4\"]}",
                "/v2/medias/complete_upload" => "{\"mediaId\":\"mda-test\"}",
                "/v2/desubtitle" => "{\"taskId\":\"tsk-test\"}",
                "/v2/desubtitle/list" => "{\"data\":[{\"status\":\"FINISHED\",\"targetUrl\":\"https://output/video.mp4\"}]}",
                _ => "{}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record CapturedRequest(string Path, string Body, string Authorization);
}
