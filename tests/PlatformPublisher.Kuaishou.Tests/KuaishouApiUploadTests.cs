using System.Net;
using System.Text;
using System.Text.Json;
using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouApiUploadTests
{
    [Theory]
    [InlineData("playwright")]
    [InlineData("browser")]
    public void IsApi_is_false_for_browser(string? value) =>
        Assert.False(KuaishouUploadTransport.IsApi(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("api")]
    [InlineData("API")]
    [InlineData(" api ")]
    public void IsApi_defaults_to_api(string? value) =>
        Assert.True(KuaishouUploadTransport.IsApi(value));

    [Fact]
    public void Cookies_from_storage_state_are_sent_to_kdj()
    {
        var path = Write("""
            {"cookies":[{"name":"kuaishou.api_st","value":"abc","domain":".kuaishou.com","path":"/","expires":4102444800}]}
            """);
        try
        {
            var cookies = KuaishouAuthCookies.Load(path).GetCookies(new Uri("https://kdj.kuaishou.com/"));
            Assert.Contains(cookies.Cast<Cookie>(), cookie => cookie.Name == "kuaishou.api_st" && cookie.Value == "abc");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_cookie_file_asks_for_login()
    {
        var path = Path.Combine(Path.GetTempPath(), "kuaishou-missing-" + Guid.NewGuid().ToString("n") + ".json");
        var error = Assert.Throws<InvalidOperationException>(() => KuaishouAuthCookies.Load(path));
        Assert.Equal("快手登录态无效，请先扫码登录。", error.Message);
    }

    [Fact]
    public void Cookies_outside_kuaishou_do_not_count_as_login()
    {
        var path = Write("""
            {"cookies":[{"name":"sid","value":"abc","domain":".example.com","path":"/"}]}
            """);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => KuaishouAuthCookies.Load(path));
            Assert.Equal("快手登录态无效，请先扫码登录。", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Fragment_url_uses_the_upload_host_and_token()
    {
        Assert.Equal(
            "https://upload.kuaishouzt.com/api/upload/fragment?upload_token=tok%20en&fragment_id=3",
            KuaishouApiVideoUploader.FragmentUrl("https://upload.kuaishouzt.com/path", "tok en", 3).AbsoluteUri);
        Assert.Equal(
            "https://upload.kuaishouzt.com/api/upload/resume?upload_token=tok",
            KuaishouApiVideoUploader.ResumeUrl("upload.kuaishouzt.com", "tok").AbsoluteUri);
        Assert.Equal(
            "https://upload.kuaishouzt.com/api/upload/complete?upload_token=tok&fragment_count=2",
            KuaishouApiVideoUploader.CompleteUrl("upload.kuaishouzt.com", "tok", 2).AbsoluteUri);
    }

    [Fact]
    public void Start_body_sends_only_the_episode_id()
    {
        using var document = JsonDocument.Parse(KuaishouApiVideoUploader.StartBody("3123333560"));
        Assert.Equal(3123333560, document.RootElement.GetProperty("episodeId").GetInt64());
        Assert.Single(document.RootElement.EnumerateObject());
        using var finish = JsonDocument.Parse(KuaishouApiVideoUploader.FinishBody(
            new KuaishouApiVideoUploader.UploadTicket("upload.kuaishouzt.com", "tok", "964861418")));
        Assert.Equal("tok", finish.RootElement.GetProperty("uploadToken").GetString());
        Assert.Equal("964861418", finish.RootElement.GetProperty("uploadTaskId").GetString());
        Assert.True(finish.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void Ticket_requires_endpoint_and_both_tokens()
    {
        var ticket = KuaishouApiVideoUploader.ReadTicket("""
            {"result":1,"data":{"endpoint":"upload.kuaishouzt.com","uploadToken":"t","uploadTaskId":"task"}}
            """);
        Assert.Equal("upload.kuaishouzt.com", ticket.Endpoint);
        Assert.Equal("t", ticket.UploadToken);
        Assert.Equal("task", ticket.UploadTaskId);
        Assert.Throws<InvalidOperationException>(() => KuaishouApiVideoUploader.ReadTicket(
            """{"result":1,"data":{"uploadToken":"t","uploadTaskId":"task"}}"""));
        Assert.Throws<InvalidOperationException>(() => KuaishouApiVideoUploader.ReadTicket(
            """{"endpoint":"upload.kuaishouzt.com","uploadToken":"t"}"""));
        var listed = KuaishouApiVideoUploader.ReadTicket("""
            {"result":1,"data":{"httpEndpointList":["upload.kuaishouzt.com"],"uploadToken":"tok","uploadTaskId":"964861418"}}
            """);
        Assert.Equal("upload.kuaishouzt.com", listed.Endpoint);
        Assert.Equal("964861418", listed.UploadTaskId);
    }

    [Fact]
    public void Resume_index_reads_fragment_index_and_defaults_to_zero()
    {
        Assert.Equal(0, KuaishouApiVideoUploader.ReadFragmentIndex(""));
        Assert.Equal(4, KuaishouApiVideoUploader.ReadFragmentIndex("""{"fragment_index":4}"""));
        Assert.Equal(2, KuaishouApiVideoUploader.ReadFragmentIndex("""{"data":{"fragmentIndex":2}}"""));
        Assert.Equal(-1, KuaishouApiVideoUploader.ReadFragmentIndex("""{"fragment_index":-1}"""));
        Assert.Equal(2, KuaishouApiVideoUploader.ChunkCount(KuaishouApiVideoUploader.ChunkBytes + 1));
    }

    [Fact]
    public void Resume_point_treats_minus_one_as_the_start_and_continues_after_the_last_fragment()
    {
        var fresh = KuaishouApiVideoUploader.ReadResumePoint(
            """{"result":1,"existed":false,"fragment_index":-1,"fragment_index_bytes":0,"fragment_list":[]}""",
            KuaishouApiVideoUploader.ChunkBytes);
        Assert.Equal(0, fresh.NextFragment);
        Assert.Equal(0, fresh.Offset);

        var continued = KuaishouApiVideoUploader.ReadResumePoint(
            """{"fragment_index":1,"fragment_index_bytes":10485760}""",
            KuaishouApiVideoUploader.ChunkBytes);
        Assert.Equal(2, continued.NextFragment);
        Assert.Equal(10485760, continued.Offset);

        var nested = KuaishouApiVideoUploader.ReadResumePoint(
            """{"data":{"fragmentIndex":2}}""",
            KuaishouApiVideoUploader.ChunkBytes);
        Assert.Equal(3, nested.NextFragment);
        Assert.Equal(3L * KuaishouApiVideoUploader.ChunkBytes, nested.Offset);
    }

    [Fact]
    public void Episode_ready_accepts_video_id_or_success_and_rejects_failure()
    {
        using var uploaded = JsonDocument.Parse("""{"videoId":"v1","videoUploadStatus":1}""");
        using var success = JsonDocument.Parse("""{"videoUploadStatus":"SUCCESS"}""");
        using var failed = JsonDocument.Parse("""{"videoUploadStatus":3}""");
        using var waiting = JsonDocument.Parse("""{"videoStatus":0,"previewVideoPlayUrl":null}""");
        using var uploadedStatus = JsonDocument.Parse("""{"videoStatus":1}""");
        Assert.True(KuaishouApiVideoUploader.EpisodeReady(uploaded.RootElement));
        Assert.True(KuaishouApiVideoUploader.EpisodeReady(success.RootElement));
        Assert.False(KuaishouApiVideoUploader.EpisodeReady(waiting.RootElement));
        Assert.True(KuaishouApiVideoUploader.EpisodeReady(uploadedStatus.RootElement));
        var error = Assert.Throws<InvalidOperationException>(() => KuaishouApiVideoUploader.EpisodeReady(failed.RootElement));
        Assert.Equal("快手确认分集视频状态失败。", error.Message);
    }

    [Fact]
    public async Task Fragment_body_without_a_kdj_result_is_kept()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var path = Write("""
            {"cookies":[{"name":"kuaishou.api_st","value":"abc","domain":".kuaishou.com","path":"/"}]}
            """);
        try
        {
            using var session = KuaishouApiSession.Open(path);
            var pending = listener.GetContextAsync();
            var bodyTask = session.GetAsync($"http://127.0.0.1:{port}/api/upload/resume?upload_token=t", CancellationToken.None);
            var context = await pending;
            var payload = Encoding.UTF8.GetBytes("""{"fragment_index":3}""");
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
            var body = await bodyTask;
            Assert.Equal(3, KuaishouApiVideoUploader.ReadFragmentIndex(body));
        }
        finally
        {
            listener.Close();
            File.Delete(path);
        }
    }

    [Fact]
    public void PassToken_cookie_stays_on_the_login_host()
    {
        var path = Write("""
            {"cookies":[
              {"name":"passToken","value":"p","domain":"id.kuaishou.com","path":"/","expires":4102444800,"secure":true},
              {"name":"kuaishou.cebp.skit_st","value":"s","domain":"kdj.kuaishou.com","path":"/","httpOnly":true},
              {"name":"did","value":"d","domain":".kuaishou.com","path":"/","expires":4102444800}
            ]}
            """);
        try
        {
            var cookies = KuaishouAuthCookies.Load(path);
            var kdj = Names(cookies, "https://kdj.kuaishou.com/");
            var login = Names(cookies, "https://id.kuaishou.com/");
            Assert.Contains("kuaishou.cebp.skit_st", kdj);
            Assert.Contains("did", kdj);
            Assert.DoesNotContain("passToken", kdj);
            Assert.Contains("passToken", login);
            Assert.Contains("did", login);
            Assert.DoesNotContain("kuaishou.cebp.skit_st", login);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Refreshed_cookie_is_written_back_without_dropping_storage()
    {
        var path = Write("""
            {"cookies":[
              {"name":"kuaishou.cebp.skit_st","value":"old","domain":"kdj.kuaishou.com","path":"/","expires":-1,"httpOnly":true,"secure":true,"sameSite":"None"},
              {"name":"passToken","value":"p","domain":"id.kuaishou.com","path":"/","expires":4102444800,"httpOnly":true,"secure":true,"sameSite":"None"}
            ],"origins":[{"origin":"https://kdj.kuaishou.com","localStorage":[{"name":"skitTitleGuideShown_paid","value":"1"}]}]}
            """);
        try
        {
            var cookies = KuaishouAuthCookies.Load(path);
            cookies.Add(new Cookie("kuaishou.cebp.skit_st", "fresh", "/", "kdj.kuaishou.com") { HttpOnly = true, Secure = true });
            var current = cookies.GetCookies(new Uri("https://kdj.kuaishou.com/")).Cast<Cookie>()
                .Where(cookie => cookie.Name == "kuaishou.cebp.skit_st")
                .Select(cookie => cookie.Value)
                .ToArray();
            Assert.Equal("fresh", Assert.Single(current));
            KuaishouAuthCookies.Save(path, cookies);
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            var root = saved.RootElement;
            var stored = root.GetProperty("cookies").EnumerateArray()
                .ToDictionary(item => item.GetProperty("name").GetString()!, item => item);
            Assert.Equal("fresh", stored["kuaishou.cebp.skit_st"].GetProperty("value").GetString());
            Assert.True(stored["kuaishou.cebp.skit_st"].GetProperty("httpOnly").GetBoolean());
            Assert.Equal("p", stored["passToken"].GetProperty("value").GetString());
            Assert.Equal("skitTitleGuideShown_paid", root.GetProperty("origins")[0].GetProperty("localStorage")[0].GetProperty("name").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Result_109_refreshes_with_passToken_and_retries()
    {
        var login = "https://id.kuaishou.com/pass/kuaishou/login/passToken?callback=1";
        var handler = new SequenceHandler((index, request) =>
        {
            if (request.Method == HttpMethod.Get)
                return TextResponse("<html></html>");
            var body = request.Content is null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Equal("{\"page\":1}", body);
            return index == 0
                ? JsonResponse("{\"result\":109,\"loginUrl\":\"" + login + "\"}")
                : JsonResponse("{\"result\":1,\"data\":{}}");
        });
        var path = Storage();
        try
        {
            using var session = KuaishouApiSession.Open(path, handler);
            var text = await session.PostJsonAsync("https://kdj.kuaishou.com/rest/ad/miniSeries/product/pc/tagList", "{\"page\":1}", CancellationToken.None);
            Assert.Contains("\"result\":1", text);
            Assert.Equal(["POST", "GET", "POST"], handler.Methods);
            Assert.Equal(login, handler.Urls[1]);
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("keep", saved.RootElement.GetProperty("origins")[0].GetProperty("localStorage")[0].GetProperty("name").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Result_109_after_refresh_asks_for_the_management_platform()
    {
        var login = "https://id.kuaishou.com/pass/kuaishou/login/passToken";
        var handler = new SequenceHandler((_, request) => request.Method == HttpMethod.Get
            ? TextResponse("<html></html>")
            : JsonResponse("{\"result\":109,\"loginUrl\":\"" + login + "\"}"));
        var path = Storage();
        try
        {
            using var session = KuaishouApiSession.Open(path, handler);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.PostJsonAsync("https://kdj.kuaishou.com/rest/ad/miniSeries/product/pc/tagList", "{}", CancellationToken.None));
            Assert.Equal(KuaishouPassTokenLogin.Message, error.Message);
            Assert.Equal(["POST", "GET", "POST"], handler.Methods);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Successful_result_does_not_open_the_login_url()
    {
        var handler = new SequenceHandler((_, _) => JsonResponse("{\"result\":1}"));
        var path = Storage();
        try
        {
            using var session = KuaishouApiSession.Open(path, handler);
            var text = await session.PostJsonAsync("https://kdj.kuaishou.com/rest/ad/miniSeries/product/pc/tagList", "{}", CancellationToken.None);
            Assert.Contains("\"result\":1", text);
            Assert.Equal(["POST"], handler.Methods);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Other_login_urls_are_not_followed()
    {
        var handler = new SequenceHandler((_, request) =>
        {
            if (request.Method == HttpMethod.Get)
                throw new InvalidOperationException("followed");
            return JsonResponse("{\"result\":109,\"loginUrl\":\"https://evil.test/pass/kuaishou/login/passToken\"}");
        });
        var path = Storage();
        try
        {
            using var session = KuaishouApiSession.Open(path, handler);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.PostJsonAsync("https://kdj.kuaishou.com/rest/ad/miniSeries/product/pc/tagList", "{}", CancellationToken.None));
            Assert.Equal(KuaishouPassTokenLogin.Message, error.Message);
            Assert.Equal(["POST"], handler.Methods);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Storage() => Write("""
        {"cookies":[{"name":"kuaishou.cebp.skit_st","value":"s","domain":"kdj.kuaishou.com","path":"/","httpOnly":true,"secure":true,"sameSite":"None"}],"origins":[{"origin":"https://kdj.kuaishou.com","localStorage":[{"name":"keep","value":"1"}]}]}
        """);

    private static List<string> Names(CookieContainer cookies, string uri) =>
        cookies.GetCookies(new Uri(uri)).Cast<Cookie>().Select(cookie => cookie.Name).ToList();

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage TextResponse(string html) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html, Encoding.UTF8, "text/html"),
    };

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpRequestMessage, HttpResponseMessage> _respond;
        public SequenceHandler(Func<int, HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        public List<string> Methods { get; } = new();
        public List<string> Urls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Methods.Count;
            Methods.Add(request.Method.Method);
            Urls.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
            return Task.FromResult(_respond(index, request));
        }
    }

    private static string Write(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "kuaishou-auth-" + Guid.NewGuid().ToString("n") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
