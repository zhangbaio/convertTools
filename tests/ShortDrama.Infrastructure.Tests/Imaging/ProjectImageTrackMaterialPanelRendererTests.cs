using FluentAssertions;
using ShortDrama.Infrastructure.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Imaging;

public sealed class ProjectImageTrackMaterialPanelRendererTests
{
    private static readonly Rgba32 PanelBackground = new(31, 31, 34, 255);

    [Fact]
    public void TryRender_track_frames_uses_four_columns_and_honors_item_count()
    {
        using var canvas = new Image<Rgba32>(480, 420, PanelBackground);
        var rect = new ProjectImageTemplateRegion(
            20,
            30,
            420,
            360,
            "source=track_frames;item_count=6");
        var colors = new[]
        {
            new Rgba32(220, 30, 30, 255),
            new Rgba32(30, 190, 70, 255),
            new Rgba32(35, 80, 220, 255),
            new Rgba32(220, 175, 25, 255),
            new Rgba32(180, 45, 190, 255),
            new Rgba32(25, 185, 195, 255),
            new Rgba32(245, 110, 25, 255)
        };
        var frames = colors
            .Select(color => new Image<Rgba32>(97, 62, color))
            .ToArray();

        try
        {
            var rendered = ProjectImageTrackMaterialPanelRenderer.TryRender(
                canvas,
                rect,
                frames,
                episodeDurationSeconds: 37d);

            rendered.Should().BeTrue();
            // The source composition transforms this manifest rect to
            // (12, 28, 428, 362). Its 4-column card centers are stable.
            canvas[68, 69].Should().Be(colors[0]);
            canvas[173, 69].Should().Be(colors[1]);
            canvas[278, 69].Should().Be(colors[2]);
            canvas[383, 69].Should().Be(colors[3]);
            canvas[68, 155].Should().Be(colors[4]);
            canvas[173, 155].Should().Be(colors[5]);
            canvas[278, 155].Should().Be(PanelBackground, "item_count=6 leaves the seventh slot empty");
        }
        finally
        {
            foreach (var frame in frames)
            {
                frame.Dispose();
            }
        }
    }

    [Fact]
    public void TryRender_draws_rounded_cards_separate_status_and_duration_badges_and_title()
    {
        using var canvas = new Image<Rgba32>(480, 420, PanelBackground);
        var rect = new ProjectImageTemplateRegion(20, 30, 420, 360, "source=track_frames;item_count=1");
        var frameColor = new Rgba32(220, 30, 30, 255);
        using var frame = new Image<Rgba32>(97, 62, frameColor);

        ProjectImageTrackMaterialPanelRenderer.TryRender(canvas, rect, [frame], 7d).Should().BeTrue();

        const int cardX = 20;
        const int cardY = 38;
        canvas[cardX, cardY].Should().Be(PanelBackground, "the thumbnail card has an 8px rounded corner");
        canvas[cardX + 5, cardY + 10].Should().NotBe(frameColor, "the 已添加 badge is independently overlaid at top-left");
        canvas[cardX + 91, cardY + 10].Should().NotBe(frameColor, "the duration badge is independently overlaid at top-right");

        var titlePixels = PixelsIn(canvas, new Rectangle(cardX, 100, 97, 18));
        titlePixels.Should().Contain(pixel => pixel.R >= 170 && pixel.G >= 170 && pixel.B >= 170,
            "素材1.mp4 is rendered below the first card");
    }

    [Fact]
    public void TryRender_non_track_source_is_a_no_op()
    {
        using var canvas = new Image<Rgba32>(200, 160, PanelBackground);
        using var frame = new Image<Rgba32>(40, 30, new Rgba32(220, 30, 30, 255));
        var rect = new ProjectImageTemplateRegion(10, 10, 150, 120, "source=episodes;item_count=1");

        var rendered = ProjectImageTrackMaterialPanelRenderer.TryRender(canvas, rect, [frame], 10d);

        rendered.Should().BeFalse();
        PixelsIn(canvas, new Rectangle(0, 0, canvas.Width, canvas.Height))
            .Should().OnlyContain(pixel => pixel == PanelBackground);
    }

    private static IReadOnlyList<Rgba32> PixelsIn(Image<Rgba32> image, Rectangle rectangle)
    {
        var pixels = new List<Rgba32>(rectangle.Width * rectangle.Height);
        for (var y = rectangle.Top; y < rectangle.Bottom; y++)
        {
            for (var x = rectangle.Left; x < rectangle.Right; x++)
            {
                pixels.Add(image[x, y]);
            }
        }

        return pixels;
    }
}
