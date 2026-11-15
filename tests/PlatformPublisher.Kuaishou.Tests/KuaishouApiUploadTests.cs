using System.Net;
using System.Text;
using System.Text.Json;
using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouApiUploadTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("playwright")]
    [InlineData("browser")]
    public void IsApi_is_false_unless_the_value_is_api(string? value) =>
        Assert.False(KuaishouUploadTransport.IsApi(value));

    [Theory]
    [InlineData("api")]
    [InlineData("API")]
    [InlineData(" api ")]
    public void IsApi_accepts_only_api(string value) =>
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
        Assert.Equal(2, KuaishouApiVideoUploader.ChunkCount(KuaishouApiVideoUploader.ChunkBytes + 1));
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
