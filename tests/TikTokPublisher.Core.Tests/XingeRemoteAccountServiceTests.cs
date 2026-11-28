using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using PublisherShared.Licensing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class XingeRemoteAccountServiceTests
{
    [Fact]
    public async Task SoftwareLoginProvisionsFromExistingSessionWithoutLoggingInAgain()
    {
        var session = XingeRuntimeConnection.FromLicense(new LicenseState
        {
            ServerUrl = "https://xinge.example/",
            AccountUsername = "software-user",
            MachineId = "bound-machine",
            Token = "software-session",
        })!;
        using var http = new HttpClient(new StubHandler(async request =>
        {
            request.RequestUri!.AbsoluteUri.Should().Be("https://xinge.example/client-api/tt/account/remote-client");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = json.RootElement;
            body.GetProperty("token").GetString().Should().Be("software-session");
            body.GetProperty("machine_id").GetString().Should().Be("bound-machine");
            body.TryGetProperty("password", out _).Should().BeFalse();
            body.TryGetProperty("legacy_client_token", out _).Should().BeFalse();
            return Json(HttpStatusCode.OK, """{"ok":true,"data":{"item":{"client_id":"rc_1"},"client_token":"remote-session"}}""");
        }));

        var result = await new XingeRemoteAccountService(http).ProvisionFromSoftwareSessionAsync(session, "TikTokPublisher", CancellationToken.None,
            new ClientSettings { XingeServerUrl = "https://other.example", XingeAccount = "manual", XingePassword = "manual-only", XingeClientId = "rc_old", XingeClientToken = "legacy-only" });
        result.ClientId.Should().Be("rc_1");
        result.CredentialFingerprint.Should().Be(session.SessionKey);
    }

    [Fact]
    public void MissingModeFollowsLoginAndExplicitOffIgnoresManualCredentials()
    {
        var settings = new ClientSettings { XingeServerUrl = "https://other.example", XingeClientId = "rc_old", XingeClientToken = "legacy-only" };
        XingeRuntimeConnection.Mode(settings).Should().Be(XingeRuntimeConnection.FollowLogin);
        settings.XingeConnectionMode = XingeRuntimeConnection.Off;
        XingeRuntimeConnection.Resolve(settings).Should().BeNull();
        XingeRuntimeConnection.FromLicense(new LicenseState { ServerUrl = "https://xinge.example" }).Should().BeNull();
    }

    [Fact]
    public async Task ProvisionWithHttpAsync_LogsInAndCreatesRemoteClient()
    {
        var requests = new List<(string Path, JsonElement Body)>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            requests.Add((request.RequestUri!.AbsolutePath, body));
            return request.RequestUri.AbsolutePath switch
            {
                "/client-api/account/login" => Json(HttpStatusCode.OK,
                    """{"ok":true,"data":{"username":"alice","account_username":"alice","token":"account-token"}}"""),
                "/client-api/account/remote-client" => Json(HttpStatusCode.Created,
                    """{"ok":true,"data":{"item":{"client_id":"rc_test"},"client_token":"remote-token"}}"""),
                _ => Json(HttpStatusCode.NotFound, """{"ok":false,"message":"not found"}"""),
            };
        }));

        var settings = new ClientSettings
        {
            XingeServerUrl = "https://xinge.example/",
            XingeAccount = "alice",
            XingePassword = "secret",
            XingeClientName = "Publisher PC",
        };

        var result = await new XingeRemoteAccountService(http).ProvisionWithHttpAsync(settings);

        result.Username.Should().Be("alice");
        result.ClientId.Should().Be("rc_test");
        result.ClientToken.Should().Be("remote-token");
        result.CredentialFingerprint.Should().NotBeNullOrWhiteSpace();
        requests.Select(item => item.Path).Should().Equal(
            "/client-api/account/login",
            "/client-api/account/remote-client");
        requests[0].Body.GetProperty("account").GetString().Should().Be("alice");
        requests[0].Body.GetProperty("password").GetString().Should().Be("secret");
        requests[0].Body.GetProperty("machine_id").GetString().Should().NotBeNullOrWhiteSpace();
        requests[1].Body.GetProperty("token").GetString().Should().Be("account-token");
        requests[1].Body.GetProperty("client_name").GetString().Should().Be("Publisher PC");
    }

    [Fact]
    public void NeedsProvisioning_ReusesMatchingCredentialsAndDetectsPasswordChange()
    {
        var settings = new ClientSettings
        {
            XingeServerUrl = "https://xinge.example",
            XingeAccount = "alice",
            XingePassword = "secret",
            XingeClientId = "rc_test",
            XingeClientToken = "remote-token",
        };
        settings.XingeCredentialFingerprint = XingeRemoteAccountService.ComputeCredentialFingerprint(settings);

        XingeRemoteAccountService.NeedsProvisioning(settings).Should().BeFalse();

        settings.XingePassword = "changed";
        XingeRemoteAccountService.NeedsProvisioning(settings).Should().BeTrue();
    }

    [Fact]
    public async Task ProvisionWithHttpAsync_ReportsServerLoginError()
    {
        using var http = new HttpClient(new StubHandler(_ => Task.FromResult(
            Json(HttpStatusCode.Unauthorized, """{"ok":false,"message":"账号或密码错误"}"""))));
        var settings = new ClientSettings
        {
            XingeServerUrl = "https://xinge.example",
            XingeAccount = "alice",
            XingePassword = "bad",
        };

        var action = () => new XingeRemoteAccountService(http).ProvisionWithHttpAsync(settings);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*账号或密码错误*");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }
}
