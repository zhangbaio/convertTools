using FluentAssertions;
using ShortDrama.Infrastructure.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Imaging;

public sealed class ProjectImageAdvancedTrackRendererTests
{
    [Fact]
    public void RenderStrip_replace_image_only_preserves_template_text_boundaries_and_playhead()
    {
        using var canvas = BuildPatternedCanvas(140, 90);
        var rect = new ProjectImageTemplateRegion(
            20,
            20,
            100,
            50,
            "replace_image_only=1;thumbnail_y=8;thumbnail_height=25;preserve_text_height=10;" +
            "preserve_boundary_xs=50|105;preserve_boundary_width=3;" +
            "playhead_x=80;playhead_brightness=255;playhead_alpha=255");
        using var frame = new Image<Rgba32>(40, 30, new Rgba32(238, 31, 44, 255));
        using var before = canvas.Clone();

        PaintPlayhead(canvas, x: 80, rect);
        PaintPlayhead(before, x: 80, rect);
        ProjectImageAdvancedTrackRenderer.RenderStrip(canvas, rect, [frame]);

        canvas[30, 23].Should().Be(before[30, 23], "preserve_text_height keeps the top timeline label band");
        canvas[30, 60].Should().Be(before[30, 60], "replace_image_only leaves pixels below the thumbnail band untouched");
        canvas[30, 35].Should().Be(new Rgba32(238, 31, 44, 255), "the configured thumbnail band is replaced");

        for (var y = rect.Y; y < rect.Y + rect.Height; y++)
        {
            for (var x = 49; x <= 51; x++)
            {
                canvas[x, y].Should().Be(before[x, y], "the first configured segment boundary is restored pixel-for-pixel");
            }

            for (var x = 104; x <= 106; x++)
            {
                canvas[x, y].Should().Be(before[x, y], "the second configured segment boundary is restored pixel-for-pixel");
            }

            canvas[79, y].Should().Be(new Rgba32(255, 255, 255, 255));
            canvas[80, y].Should().Be(new Rgba32(255, 255, 255, 255));
        }
    }

    [Fact]
    public void RenderStrip_standard_mode_builds_a_filled_repeated_thumbnail_strip()
    {
        using var canvas = new Image<Rgba32>(150, 90, new Rgba32(18, 22, 26, 255));
        var rect = new ProjectImageTemplateRegion(
            15,
            20,
            120,
            40,
            "thumbnail_y=7;thumbnail_height=24;playhead_x=75;playhead_brightness=255;playhead_alpha=255");
        using var first = new Image<Rgba32>(20, 20, new Rgba32(220, 30, 30, 255));
        using var second = new Image<Rgba32>(20, 20, new Rgba32(30, 190, 70, 255));
        PaintPlayhead(canvas, x: 75, rect);

        ProjectImageAdvancedTrackRenderer.RenderStrip(canvas, rect, [first, second]);

        canvas[25, rect.Y + 12].Should().Be(new Rgba32(220, 30, 30, 255));
        canvas[60, rect.Y + 12].Should().Be(new Rgba32(30, 190, 70, 255));
        canvas[74, rect.Y + 20].Should().Be(new Rgba32(255, 255, 255, 255));
        canvas[75, rect.Y + 20].Should().Be(new Rgba32(255, 255, 255, 255));
    }

    [Fact]
    public void RenderStrip_with_no_frames_is_a_no_op()
    {
        using var canvas = BuildPatternedCanvas(40, 30);
        using var before = canvas.Clone();
        var rect = new ProjectImageTemplateRegion(4, 5, 30, 20, "replace_image_only=1");

        ProjectImageAdvancedTrackRenderer.RenderStrip(canvas, rect, []);

        for (var y = 0; y < canvas.Height; y++)
        {
            for (var x = 0; x < canvas.Width; x++)
            {
                canvas[x, y].Should().Be(before[x, y]);
            }
        }
    }

    private static Image<Rgba32> BuildPatternedCanvas(int width, int height)
    {
        var canvas = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                canvas[x, y] = new Rgba32(
                    (byte)(20 + x % 90),
                    (byte)(25 + y % 80),
                    (byte)(30 + (x + y) % 70),
                    255);
            }
        }

        return canvas;
    }

    private static void PaintPlayhead(
        Image<Rgba32> canvas,
        int x,
        ProjectImageTemplateRegion rect)
    {
        for (var y = 0; y < canvas.Height; y++)
        {
            if (y < rect.Y || y >= rect.Y + rect.Height)
            {
                canvas[x, y] = new Rgba32(255, 255, 255, 255);
            }
        }
    }
}
