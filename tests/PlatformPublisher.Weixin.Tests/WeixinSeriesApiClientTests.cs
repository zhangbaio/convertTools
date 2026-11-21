using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using PlatformPublisher.Weixin.Publishing;
using Xunit;

namespace PlatformPublisher.Weixin.Tests;

public sealed class WeixinSeriesApiClientTests
{
    [Fact]
    public async Task Upload_uses_streaming_cloud_storage_and_preserves_large_media_id()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(root.FullName, "第1集.mp4");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var info = new FileInfo(path);
            var file = new WeixinSeriesApiFile("剧集视频", path, 1, info.Length, info.LastWriteTimeUtc.Ticks);
            using var channelsHandler = new ScriptedHandler(
                request =>
                {
                    Assert.Equal("mp.weixin.qq.com", request.RequestUri!.Host);
                    Assert.EndsWith("vodCliSdkGetSign", request.RequestUri.AbsolutePath);
                    return Json("""{"retInNode":0,"sign":"fixture-ticket"}""");
                },
                request =>
                {
                    Assert.EndsWith("onVodCliSdkUploadFinished", request.RequestUri!.AbsolutePath);
                    Assert.Equal("text/plain", request.Content!.Headers.ContentType!.MediaType);
                    var payload = JsonNode.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
                    Assert.Equal("cloud-file", payload["fileId"]!.GetValue<string>());
                    return Json("""{"retInNode":0,"mediaInfo":{"_id":9007199254740993}}""");
                });
            using var cloudHandler = new ScriptedHandler(
                request =>
                {
                    Assert.Equal("?Action=ApplyUploadUGC", request.RequestUri!.Query);
                    var expiry = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
                    return Json("""
                        {"code":0,"data":{"storageRegionV5":"fixture-region","storageBucket":"fixture-bucket","storageAppId":123,
                        "video":{"storagePath":"/episodes/1.mp4"},"vodSessionKey":"fixture-session",
                        "tempCertificate":{"secretId":"fixture-id","secretKey":"fixture-secret","token":"fixture-token","expiredTime":EXPIRY}}}
                        """.Replace("EXPIRY", expiry.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
                },
                request =>
                {
                    Assert.Equal(HttpMethod.Put, request.Method);
                    Assert.Equal("fixture-bucket-123.cos.fixture-region.myqcloud.com", request.RequestUri!.Host);
                    Assert.IsType<StreamContent>(request.Content);
                    Assert.Equal(4L, request.Content!.Headers.ContentLength);
                    Assert.False(request.Headers.Contains("Cookie"));
                    Assert.Equal("fixture-token", Assert.Single(request.Headers.GetValues("x-cos-security-token")));
                    return new HttpResponseMessage(HttpStatusCode.OK) { Headers = { ETag = new EntityTagHeaderValue("\"fixture\"") } };
                },
                request =>
                {
                    Assert.Equal("?Action=CommitUploadUGC", request.RequestUri!.Query);
                    return Json("""{"code":0,"data":{"fileId":"cloud-file"}}""");
                });
            using var client = new WeixinSeriesApiClient(new HttpClient(channelsHandler, false), new HttpClient(cloudHandler, false), "");
            var id = await client.UploadAsync(file, CancellationToken.None);
            Assert.Equal("9007199254740993", id);
            Assert.Empty(channelsHandler.Remaining);
            Assert.Empty(cloudHandler.Remaining);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Login_exchange_is_used_before_authenticated_review_requests()
    {
        using var handler = new ScriptedHandler(
            request =>
            {
                Assert.Equal("channels.weixin.qq.com", request.RequestUri!.Host);
                Assert.EndsWith("/genExternalCode", request.RequestUri.AbsolutePath);
                var payload = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
                Assert.Equal("fixture-finder", payload["_log_finder_id"]!.GetValue<string>());
                return Json("""{"errCode":0,"data":{"externalCode":"fixture-code"}}""");
            },
            request =>
            {
                Assert.Equal("mp.weixin.qq.com", request.RequestUri!.Host);
                Assert.Equal("/webpocnew/newAuditForm", request.RequestUri.AbsolutePath);
                Assert.Equal("?code=fixture-code", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            request =>
            {
                Assert.EndsWith("/cpFinderLogin", request.RequestUri!.AbsolutePath);
                return Json("""{"base_resp":{"ret":0}}""");
            },
            request =>
            {
                Assert.EndsWith("/checkDramaName", request.RequestUri!.AbsolutePath);
                return Json("""{"baseResp":{"errCode":0,"errMsg":"ok"}}""");
            });
        using var client = new WeixinSeriesApiClient(new HttpClient(handler, false), new HttpClient(), "");
        await client.AuthenticateAsync(new("fixture-finder", "", "", "", new CookieContainer()), CancellationToken.None);
        await client.CheckNameAsync("测试剧", CancellationToken.None);
        Assert.Empty(handler.Remaining);
    }

    [Theory]
    [InlineData("""{"retInNode":42,"baseResp":{"errCode":42}}""", true)]
    [InlineData("""{"retInNode":"0","baseResp":{"errCode":0}}""", false)]
    [InlineData("""{"baseResp":{"errCode":0}}""", false)]
    public async Task Only_explicit_platform_errors_are_classified_as_rejection(string response, bool rejected)
    {
        using var handler = new ScriptedHandler(_ => Json(response));
        using var client = new WeixinSeriesApiClient(new HttpClient(handler, false), new HttpClient(), "");
        var error = await Record.ExceptionAsync(() => client.SubmitAsync(new JsonObject(), CancellationToken.None));
        Assert.NotNull(error);
        Assert.Equal(rejected, error is WeixinSeriesApiRejectedException);
    }

    [Fact]
    public async Task A_platform_quota_rejection_can_stop_a_batch()
    {
        using var handler = new ScriptedHandler(_ => Json("""
            {"retInNode":42,"baseResp":{"errCode":42,"errMsg":"主体提审额度已达上限"}}
            """));
        using var client = new WeixinSeriesApiClient(new HttpClient(handler, false), new HttpClient(), "");
        await Assert.ThrowsAsync<WeixinSeriesApiQuotaExceededException>(
            () => client.SubmitAsync(new JsonObject(), CancellationToken.None));
    }

    private static HttpResponseMessage Json(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) : HttpMessageHandler
    {
        public Queue<Func<HttpRequestMessage, HttpResponseMessage>> Remaining { get; } = new(steps);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Remaining.Dequeue()(request));
    }
}
