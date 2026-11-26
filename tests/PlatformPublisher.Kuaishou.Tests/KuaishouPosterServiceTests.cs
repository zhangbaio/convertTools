using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PlatformPublisher.Kuaishou.Publishing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouPosterServiceTests
{
    [Fact]
    public void Horizontal_request_size_is_landscape()
    {
        Assert.Equal("2560x1600", KuaishouPosterService.HorizontalRequestSize("doubao"));
        Assert.Equal("1536x1024", KuaishouPosterService.HorizontalRequestSize("ofox_image2"));
        Assert.Equal("1776x2368", KuaishouPosterService.VerticalRequestSize("doubao"));
    }

    [Fact]
    public void Find_source_poster_skips_generated_covers()
    {
        var root = Path.Combine(Path.GetTempPath(), "kuaishou-poster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "快手竖屏海报.jpg"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "快手横屏封面.jpg"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "海报图片.png"), [1, 2, 3]);
            Assert.Equal(
                Path.Combine(root, "海报图片.png"),
                KuaishouPosterService.FindSourcePoster(root, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_source_poster_is_detected_before_generation()
    {
        var root = Path.Combine(Path.GetTempPath(), "kuaishou-poster-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "快手竖屏海报.jpg"), [1, 2, 3]);
            Assert.False(KuaishouPosterService.HasSourcePoster(root, root));
            File.WriteAllBytes(Path.Combine(root, "别拿我的钱疼你全家.jpg"), [1, 2, 3]);
            Assert.True(KuaishouPosterService.HasSourcePoster(root, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expands_landscape_and_reinserts_the_whole_portrait(bool erase)
    {
        var root = Directory.CreateTempSubdirectory("kuaishou-outpaint-").FullName;
        var target = Path.Combine(root, "快手横屏封面.jpg");
        try
        {
            await WriteProjectAsync(root);
            using var handler = new PosterModel();
            using var http = new HttpClient(handler);
            await new KuaishouPosterService(http).PrepareAsync(root, Settings(erase), null, CancellationToken.None);
            var vertical = await Image.IdentifyAsync(Path.Combine(root, KuaishouPosterService.VerticalPosterFileName));
            Assert.Equal((1200, 1600), (vertical.Width, vertical.Height));
            using var image = await Image.LoadAsync<Rgba32>(target);
            Assert.Equal((1600, 1000), (image.Width, image.Height));
            Assert.True(image[20, 100].G > 190);
            Assert.True(image[470, 15].R > 190);
            Assert.True(image[470, 985].B > 190);
            Assert.True(image[1100, 15].R > 190);
            Assert.Equal(2, handler.Prompts.Count);
            Assert.Equal(KuaishouPosterService.ComposePrompt("重绘 {title}", "归途灯火", false, erase), handler.Prompts[0]);
            Assert.Equal(KuaishouPosterService.ComposePrompt("扩图 {title}", "归途灯火", true, erase), handler.Prompts[1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Failed_landscape_request_does_not_replace_an_existing_cover()
    {
        var root = Directory.CreateTempSubdirectory("kuaishou-outpaint-failure-").FullName;
        try
        {
            await WriteProjectAsync(root);
            var target = Path.Combine(root, KuaishouPosterService.HorizontalCoverFileName);
            await File.WriteAllTextAsync(target, "已保存封面");
            using var handler = new PosterModel(failLandscape: true);
            using var http = new HttpClient(handler);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new KuaishouPosterService(http).PrepareAsync(root, Settings(false), null, CancellationToken.None));
            Assert.Contains("横屏扩图未完成", ex.Message);
            Assert.Equal("已保存封面", await File.ReadAllTextAsync(target));
            Assert.False(File.Exists(Path.Combine(root, KuaishouPosterService.VerticalPosterFileName)));
            Assert.Empty(Directory.EnumerateDirectories(root, ".kuaishou-poster-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static KuaishouPosterSettings Settings(bool erase) => new(
        erase, "重绘 {title}", "重绘重试", "扩图 {title}", "扩图重试",
        "doubao", "test-model", "test-key", "https://model.example/v1", "/images/generations",
        "2K", KuaishouPosterService.HorizontalRequestSize("doubao"), "", "", "", false, "", 0, "");

    private static async Task WriteProjectAsync(string root)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "短剧信息.txt"), "新剧名：归途灯火");
        using var source = new Image<Rgba32>(90, 120);
        await source.SaveAsPngAsync(Path.Combine(root, "海报原图.png"));
    }

    private sealed class PosterModel(bool failLandscape = false) : HttpMessageHandler
    {
        public List<string> Prompts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Prompts.Add(json.RootElement.GetProperty("prompt").GetString()!);
            var horizontal = Prompts.Count > 1;
            if (horizontal && failLandscape)
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("test failure") };
            if (horizontal)
            {
                var seed = json.RootElement.GetProperty("image").GetString()!;
                using var canvas = Image.Load<Rgba32>(Convert.FromBase64String(seed[(seed.IndexOf(',') + 1)..]));
                Assert.Equal((1600, 1000), (canvas.Width, canvas.Height));
                Assert.True(canvas[470, 15].R > 190);
                Assert.True(canvas[470, 985].B > 190);
            }
            using var generated = horizontal
                ? new Image<Rgba32>(320, 200, new Rgba32(15, 230, 15))
                : new Image<Rgba32>(120, 160);
            if (!horizontal)
                generated.ProcessPixelRows(rows =>
                {
                    for (var y = 0; y < rows.Height; y++)
                        rows.GetRowSpan(y).Fill(y < rows.Height / 2 ? new Rgba32(240, 20, 20) : new Rgba32(20, 20, 240));
                });
            using var buffer = new MemoryStream();
            await generated.SaveAsPngAsync(buffer, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { data = new[] { new { b64_json = Convert.ToBase64String(buffer.ToArray()) } } }),
            };
        }
    }
}
