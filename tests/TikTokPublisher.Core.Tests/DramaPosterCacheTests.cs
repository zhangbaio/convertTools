using System.Net;
using System.Net.Http;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TikTokPublisher.Core.Drama;

namespace TikTokPublisher.Core.Tests;

public sealed class DramaPosterCacheTests
{
    [Fact]
    public async Task TryGetLocalPath_Downloads_Resizes_And_Reuses_Disk_Cache()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"poster-cache-{Guid.NewGuid():N}");
        var handler = new CountingHandler(CreatePngBytes(32, 48));
        using var http = new HttpClient(handler);

        try
        {
            var first = await DramaPosterCache.TryGetLocalPathAsync(
                "https://cdn.example.com/cover.webp?x=1",
                http,
                cacheDir);
            var second = await DramaPosterCache.TryGetLocalPathAsync(
                "https://cdn.example.com/cover.webp?x=1",
                http,
                cacheDir);

            first.Should().NotBeNull();
            second.Should().Be(first);
            File.Exists(first!).Should().BeTrue();
            handler.Count.Should().Be(1);

            using var cached = await Image.LoadAsync(first!);
            cached.Width.Should().Be(DramaPosterCache.ThumbWidth);
            cached.Height.Should().Be(DramaPosterCache.ThumbHeight);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task TryGetLocalPath_Returns_Null_For_Invalid_Image()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"poster-cache-{Guid.NewGuid():N}");
        using var http = new HttpClient(new CountingHandler("not-an-image"u8.ToArray()));

        try
        {
            var path = await DramaPosterCache.TryGetLocalPathAsync(
                "https://cdn.example.com/missing.jpg",
                http,
                cacheDir);
            path.Should().BeNull();
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task TryGetLocalPath_Returns_Null_For_Non_Http_Url()
    {
        using var http = new HttpClient(new CountingHandler([1, 2, 3]));
        var path = await DramaPosterCache.TryGetLocalPathAsync("not-a-url", http, Path.GetTempPath());
        path.Should().BeNull();
    }

    private static byte[] CreatePngBytes(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private sealed class CountingHandler(byte[] body) : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            request.Headers.UserAgent.ToString().Should().Contain("WeixinChannelTool");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            });
        }
    }
}
