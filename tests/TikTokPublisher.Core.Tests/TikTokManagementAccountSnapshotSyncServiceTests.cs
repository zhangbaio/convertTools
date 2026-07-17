using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokManagementAccountSnapshotSyncServiceTests
{
    [Fact]
    public void BuildSnapshot_UsesOnlyTikTokUsernameAndSkipsBlankProfiles()
    {
        var profiles = new[]
        {
            new TikTokAccountProfile
            {
                Id = "acct-a",
                TiktokAccountNickname = "昵称不能上传",
                TiktokLoginEmail = " 2720937754@qq.com ",
                TiktokLoginPassword = "secret-a",
            },
            new TikTokAccountProfile
            {
                Id = "acct-b",
                TiktokAccountNickname = "也不能上传",
                TiktokLastLoginEmail = "15327086817@163.com",
                TiktokLoginPassword = "secret-b",
            },
            new TikTokAccountProfile
            {
                Id = "acct-empty",
                TiktokAccountNickname = "只有昵称",
            },
        };

        var snapshot = TikTokManagementAccountSnapshotSyncService.BuildSnapshot(profiles);

        snapshot.Should().Equal(
            new TikTokClientAccountSnapshotItem("acct-a", "2720937754@qq.com"),
            new TikTokClientAccountSnapshotItem("acct-b", "15327086817@163.com"));
    }

    [Fact]
    public async Task SyncAsync_SendsPutWithTtHeadersAndWhitelistedPayload()
    {
        HttpMethod? method = null;
        string? path = null;
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        JsonElement body = default;
        using var http = new HttpClient(new StubHandler(async request =>
        {
            method = request.Method;
            path = request.RequestUri!.AbsolutePath;
            headers = request.Headers.ToDictionary(
                pair => pair.Key,
                pair => string.Join(",", pair.Value),
                StringComparer.OrdinalIgnoreCase);
            body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            return Json(HttpStatusCode.OK, """{"ok":true,"data":{"total":1}}""");
        }));
        var service = CreateService(http);

        var result = await service.SyncAsync(
        [
            new TikTokClientAccountSnapshotItem("acct-a", "2720937754@qq.com"),
        ]);

        result.Ok.Should().BeTrue();
        method.Should().Be(HttpMethod.Put);
        path.Should().Be("/client-api/tt/accounts/snapshot");
        headers["X-TT-Account"].Should().Be("software-user");
        headers["X-TT-Machine-Id"].Should().Be("machine-a");
        headers["X-TT-Token"].Should().Be("signed-token");
        body.EnumerateObject().Select(item => item.Name).Should().Equal("accounts");
        var account = body.GetProperty("accounts")[0];
        account.EnumerateObject().Select(item => item.Name).Should().BeEquivalentTo(
            "client_account_id",
            "tiktok_username");
        account.GetProperty("client_account_id").GetString().Should().Be("acct-a");
        account.GetProperty("tiktok_username").GetString().Should().Be("2720937754@qq.com");
        body.GetRawText().Should().NotContain("password").And.NotContain("nickname");
    }

    [Fact]
    public async Task SyncAsync_SendsEmptySnapshotAndClassifiesFailures()
    {
        var requestBodies = new List<string>();
        var responses = new Queue<HttpResponseMessage>(
        [
            Json(HttpStatusCode.OK, """{"ok":true}"""),
            Json(HttpStatusCode.Unauthorized, """{"ok":false,"message":"登录态无效"}"""),
            Json(HttpStatusCode.InternalServerError, """{"ok":false,"message":"稍后重试"}"""),
            Json(HttpStatusCode.OK, """{"message":{"unexpected":true}}"""),
        ]);
        using var http = new HttpClient(new StubHandler(async request =>
        {
            requestBodies.Add(await request.Content!.ReadAsStringAsync());
            return responses.Dequeue();
        }));
        var service = CreateService(http);

        var empty = await service.SyncAsync([]);
        var unauthorized = await service.SyncAsync([]);
        var serverError = await service.SyncAsync([]);
        var malformedSuccess = await service.SyncAsync([]);

        empty.Ok.Should().BeTrue();
        JsonDocument.Parse(requestBodies[0]).RootElement
            .GetProperty("accounts").GetArrayLength().Should().Be(0);
        unauthorized.Ok.Should().BeFalse();
        unauthorized.ShouldRetry.Should().BeFalse();
        serverError.Ok.Should().BeFalse();
        serverError.ShouldRetry.Should().BeTrue();
        malformedSuccess.Ok.Should().BeFalse();
        malformedSuccess.ShouldRetry.Should().BeTrue();
        malformedSuccess.Message.Should().Contain("响应格式无效");
    }

    private static TikTokManagementAccountSnapshotSyncService CreateService(HttpClient http) =>
        new(
            http,
            () => new ClientSettings { AuthServerUrl = "https://fallback.example" },
            () => new LicenseState
            {
                ServerUrl = "https://manage.example/",
                AccountUsername = "software-user",
                MachineId = "machine-a",
                Token = "signed-token",
            });

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
