using System.Net;
using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokApiLoginServiceTests
{
    private static readonly Uri Origin = new(TikTokApiSites.DomesticOrigin);

    [Fact]
    public async Task A_verified_new_session_can_replace_the_saved_authorization()
    {
        var cookies = new CookieContainer();
        using var handler = new StubHandler(async (request, step, ct) =>
        {
            if (step == 1)
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/login", request.RequestUri?.AbsolutePath);
                Assert.Empty(cookies.GetAllCookies());
                return Reply("""{}""");
            }

            if (step == 2)
            {
                Assert.Equal("/passport/web/user/login/", request.RequestUri?.AbsolutePath);
                Assert.Contains("aid=920662", request.RequestUri?.Query);
                var form = await request.Content!.ReadAsStringAsync(ct);
                Assert.Contains("email=6445607d64687569602b666a68", form);
                Assert.Contains("password=4467663437362b", form);
                cookies.SetCookies(Origin, "sessionid=synthetic-session; Domain=tiktokdramacenter.com; Path=/; Secure; HttpOnly");
                return Reply("""{"message":"success"}""");
            }

            Assert.Equal(TikTokApiUploadService.SessionVerificationPath, request.RequestUri?.AbsolutePath);
            Assert.Equal("synthetic-session", cookies.GetCookies(request.RequestUri!)["sessionid"]?.Value);
            return Reply("""{"code":0,"data":{"seriesList":[]}}""");
        });
        using var client = new HttpClient(handler);
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(folder.FullName, "auth.json");
            File.WriteAllText(path, "previous authorization");

            var result = await TikTokApiLoginService.TryLoginAsync(client, cookies, "a@example.com", "Abc123.", CancellationToken.None);
            Assert.Equal(3, handler.Calls);
            Assert.NotNull(result.State);
            Assert.Equal("previous authorization", File.ReadAllText(path));

            var saved = TikTokApiLoginService.SaveVerifiedLogin(path, "a@example.com", result.State!);
            Assert.Equal(path, saved.AuthPath);
            using var state = JsonDocument.Parse(File.ReadAllText(path));
            var cookie = Assert.Single(state.RootElement.GetProperty("cookies").EnumerateArray());
            Assert.Equal("sessionid", cookie.GetProperty("name").GetString());
            Assert.Equal(".tiktokdramacenter.com", cookie.GetProperty("domain").GetString());
            Assert.True(cookie.GetProperty("httpOnly").GetBoolean());
            Assert.True(cookie.GetProperty("secure").GetBoolean());
            Assert.Empty(state.RootElement.GetProperty("origins").EnumerateArray());
            Assert.Single(folder.EnumerateFiles());
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("tiktokdramacenter.com", ".tiktokdramacenter.com")]
    [InlineData(".tiktokdramacenter.com", ".tiktokdramacenter.com")]
    [InlineData("www.tiktokdramacenter.com", "www.tiktokdramacenter.com")]
    [InlineData("drama.tiktok.com", "drama.tiktok.com")]
    public void Browser_import_corrects_legacy_domain_cookies_and_preserves_other_scopes(string domain, string expected)
    {
        Assert.Equal(expected, EmbeddedAuthExportService.NormalizeCookieDomain(domain));
    }

    [Theory]
    [InlineData(429, "{}", "限流")]
    [InlineData(200, """{"message":"error","data":{"error_code":7}}""", "限流")]
    [InlineData(200, """{"message":"error","data":{"description":"Captcha required"}}""", "验证")]
    [InlineData(503, "{}", "失败")]
    public async Task Rejected_login_stops_before_verification(int status, string body, string reason)
    {
        using var handler = new StubHandler((_, step, _) =>
            Task.FromResult(step == 1 ? Reply("{}") : Reply(body, (HttpStatusCode)status)));
        using var client = new HttpClient(handler);

        var result = await TikTokApiLoginService.TryLoginAsync(client, new CookieContainer(), "a@example.com", "Abc123.", CancellationToken.None);
        Assert.Null(result.State);
        Assert.Contains(reason, result.FailureReason);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Login_success_without_partner_access_does_not_return_a_saveable_session()
    {
        var cookies = new CookieContainer();
        cookies.Add(Origin, new Cookie("sessionid", "synthetic-session", "/"));
        using var handler = new StubHandler((_, step, _) => Task.FromResult(step switch
        {
            1 => Reply("{}"),
            2 => Reply("""{"message":"success"}"""),
            _ => Reply("""{"code":403,"data":{}}"""),
        }));
        using var client = new HttpClient(handler);

        var result = await TikTokApiLoginService.TryLoginAsync(client, cookies, "a@example.com", "Abc123.", CancellationToken.None);
        Assert.Null(result.State);
        Assert.Contains("未通过", result.FailureReason);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Cancelling_verification_does_not_return_a_session()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new StubHandler((_, step, ct) =>
        {
            if (step == 3)
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return Task.FromResult(Reply(step == 1 ? "{}" : """{"message":"success"}"""));
        });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TikTokApiLoginService.TryLoginAsync(client, new CookieContainer(), "a@example.com", "Abc123.", cancellation.Token));
        Assert.Equal(3, handler.Calls);
    }

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private sealed class StubHandler(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, ++Calls, cancellationToken);
    }
}
