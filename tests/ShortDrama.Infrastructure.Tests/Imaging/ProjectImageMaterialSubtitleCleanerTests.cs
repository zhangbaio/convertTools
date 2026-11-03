using FluentAssertions;
using ShortDrama.Infrastructure.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Imaging;

public sealed class ProjectImageMaterialSubtitleCleanerTests
{
    [Fact]
    public void Clean_removes_a_bright_caption_in_the_lower_band_and_keeps_the_upper_picture()
    {
        using var frame = new Image<Rgba32>(200, 120, new Rgba32(24, 28, 32, 255));
        for (var y = 82; y < 92; y++)
        {
            for (var x = 40; x < 90; x++)
            {
                frame[x, y] = new Rgba32(245, 245, 245, 255);
            }
        }

        using var cleaned = ProjectImageMaterialSubtitleCleaner.Clean(frame, projectDirectory: null);

        cleaned[20, 12].Should().Be(new Rgba32(24, 28, 32, 255));
        cleaned[55, 86].R.Should().BeLessThan(80);
        cleaned[55, 86].G.Should().BeLessThan(80);
        cleaned[55, 86].B.Should().BeLessThan(80);
    }
}
