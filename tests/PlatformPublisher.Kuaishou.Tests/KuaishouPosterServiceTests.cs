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
        Assert.Equal("2816x1584", KuaishouPosterService.HorizontalRequestSize("doubao"));
        Assert.Equal("1536x1024", KuaishouPosterService.HorizontalRequestSize("ofox_image2"));
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
    public async Task Crop_landscape_outputs_1600_by_1000()
    {
        var root = Path.Combine(Path.GetTempPath(), "kuaishou-crop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "海报图片.png");
        var target = Path.Combine(root, "快手横屏封面.jpg");
        try
        {
            using (var image = new Image<Rgba32>(900, 1600))
                await image.SaveAsPngAsync(source);
            await KuaishouPosterService.CropLandscapeAsync(source, target, CancellationToken.None);
            var info = await Image.IdentifyAsync(target);
            Assert.Equal(1600, info.Width);
            Assert.Equal(1000, info.Height);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
