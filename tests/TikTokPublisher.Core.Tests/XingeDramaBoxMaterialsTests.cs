using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Remote;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class XingeDramaBoxMaterialsTests
{
    private const string Manifest = """
        {"items":[{"number":2,"download_url":"https://media.example/second.mp4"},
                  {"number":1,"download_url":"https://media.example/first.mp4"}],"intro":"Sample"}
        """;

    [Fact]
    public async Task DownloadsOnlyRequestedEpisodesAndKeepsCredentialsOnXinge()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            using var handler = new StubHandler(request =>
            {
                if (request.RequestUri?.Host == "xinge.example")
                {
                    Assert.Equal("/client-api/dramabox/assignments/42/episodes", request.RequestUri.AbsolutePath);
                    Assert.Equal("test-id", Assert.Single(request.Headers.GetValues("X-Remote-Client-ID")));
                    Assert.Equal("test-token", Assert.Single(request.Headers.GetValues("X-Remote-Client-Token")));
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest, Encoding.UTF8, "application/json") };
                }

                Assert.Equal("https://media.example/first.mp4", request.RequestUri?.AbsoluteUri);
                Assert.False(request.Headers.Contains("X-Remote-Client-Token"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("video/mp4") } },
                };
            });
            using var http = new HttpClient(handler);
            await XingeDramaBoxMaterials.DownloadAsync(dir.FullName, new XingeAutoPublishMarker { AssignmentId = 42 },
                Settings(), [1], false, _ => { }, CancellationToken.None, http);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(dir.FullName, "第1集.mp4")));
            Assert.Equal("Sample", await File.ReadAllTextAsync(Path.Combine(dir.FullName, "简介.txt")));
            Assert.False(File.Exists(Path.Combine(dir.FullName, "第2集.mp4")));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task KeepsExistingVideoWhenMaterialResponseIsNotAVideo()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "第1集.mp4");
            await File.WriteAllBytesAsync(path, [4, 5, 6]);
            using var handler = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri?.Host == "xinge.example"
                    ? new StringContent(Manifest, Encoding.UTF8, "application/json")
                    : new StringContent("Access denied", Encoding.UTF8, "text/html"),
            });
            using var http = new HttpClient(handler);
            await Assert.ThrowsAsync<InvalidOperationException>(() => XingeDramaBoxMaterials.DownloadAsync(
                dir.FullName, new XingeAutoPublishMarker { AssignmentId = 42 }, Settings(), [1], true, _ => { }, CancellationToken.None, http));
            Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.GetFiles(dir.FullName, "*.download"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static ClientSettings Settings() => new()
    {
        XingeConnectionMode = XingeRuntimeConnection.Manual,
        XingeServerUrl = "https://xinge.example",
        XingeClientId = "test-id",
        XingeClientToken = "test-token",
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handle(request));
    }
}
