using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ShortDrama.Infrastructure.Imaging;

/// <summary>
/// Renders a repeated thumbnail strip while retaining timeline decorations that
/// are part of the source template image.
/// </summary>
public static class ProjectImageAdvancedTrackRenderer
{
    public static void RenderStrip(
        Image<Rgba32> canvas,
        ProjectImageTemplateRegion rect,
        IReadOnlyList<Image<Rgba32>> frames)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(rect);
        ArgumentNullException.ThrowIfNull(frames);

        if (frames.Count == 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var bounds = IntersectWithCanvas(canvas, rect);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        // Template manifests use in-canvas rectangles. Keeping this fallback
        // makes malformed/custom manifests safe without changing normal output.
        if (bounds.X != rect.X || bounds.Y != rect.Y || bounds.Width != rect.Width || bounds.Height != rect.Height)
        {
            RenderClippedFallback(canvas, bounds, frames);
            return;
        }

        using var original = canvas.Clone(context => context.Crop(bounds));
        using var textOverlay = CaptureTextOverlay(original, rect);
        using var boundaryOverlay = CaptureBoundaryOverlay(original, rect);
        using var playheadOverlay = BuildConfiguredPlayheadOverlay(canvas, rect);

        var thumbnailHeight = ResolveThumbnailHeight(rect);
        var thumbnailY = NoteInt(
            rect,
            "thumbnail_y",
            Math.Max(0, (rect.Height - thumbnailHeight) / 2),
            0,
            Math.Max(0, rect.Height - thumbnailHeight));
        var background = SampleTrackBackground(original, thumbnailY, thumbnailHeight);
        var replaceImageOnly = NoteInt(rect, "replace_image_only", 0, 0, 1) > 0;

        using var strip = replaceImageOnly
            ? original.Clone()
            : new Image<Rgba32>(rect.Width, rect.Height, background);

        var clipWidth = Math.Clamp((int)Math.Round(thumbnailHeight * 1.35), 28, 72);
        var clipCount = Math.Max(1, (int)Math.Ceiling(rect.Width / (double)clipWidth));
        for (var index = 0; index < clipCount; index++)
        {
            var x = index * clipWidth;
            var width = Math.Min(clipWidth, rect.Width - x);
            if (width <= 0)
            {
                break;
            }

            using var thumbnail = ResizeCrop(frames[index % frames.Count], clipWidth, thumbnailHeight);
            if (width == clipWidth)
            {
                strip.Mutate(context => context.DrawImage(thumbnail, new Point(x, thumbnailY), 1f));
                continue;
            }

            using var partial = thumbnail.Clone(context => context.Crop(new Rectangle(0, 0, width, thumbnailHeight)));
            strip.Mutate(context => context.DrawImage(partial, new Point(x, thumbnailY), 1f));
        }

        // Match the source renderer's restoration order: text, segment
        // boundaries, then the playhead as the top-most timeline decoration.
        DrawOverlay(strip, textOverlay);
        DrawOverlay(strip, boundaryOverlay);
        DrawOverlay(strip, playheadOverlay);
        canvas.Mutate(context => context.DrawImage(strip, new Point(rect.X, rect.Y), 1f));
    }

    private static Image<Rgba32>? CaptureTextOverlay(
        Image<Rgba32> original,
        ProjectImageTemplateRegion rect)
    {
        var height = NoteInt(rect, "preserve_text_height", 0, 0, rect.Height);
        return height <= 0
            ? null
            : original.Clone(context => context.Crop(new Rectangle(0, 0, rect.Width, height)));
    }

    private static Image<Rgba32>? CaptureBoundaryOverlay(
        Image<Rgba32> original,
        ProjectImageTemplateRegion rect)
    {
        var positions = (NoteValue(rect, "preserve_boundary_xs") ?? string.Empty)
            .Split(['|', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var position) ? (int?)position : null)
            .Where(static position => position.HasValue)
            .Select(static position => position!.Value)
            .ToArray();
        if (positions.Length == 0)
        {
            return null;
        }

        var boundaryWidth = NoteInt(rect, "preserve_boundary_width", 3, 1, 12);
        var halfWidth = boundaryWidth / 2;
        var overlay = new Image<Rgba32>(rect.Width, rect.Height, new Rgba32(0, 0, 0, 0));

        foreach (var position in positions)
        {
            var sourceLeft = Math.Max(rect.X, position - halfWidth);
            var sourceRight = Math.Min(rect.X + rect.Width, sourceLeft + boundaryWidth);
            if (sourceRight <= sourceLeft)
            {
                continue;
            }

            var localX = sourceLeft - rect.X;
            using var column = original.Clone(context => context.Crop(
                new Rectangle(localX, 0, sourceRight - sourceLeft, rect.Height)));
            overlay.Mutate(context => context.DrawImage(column, new Point(localX, 0), 1f));
        }

        return overlay;
    }

    private static Image<Rgba32>? BuildConfiguredPlayheadOverlay(
        Image<Rgba32> canvas,
        ProjectImageTemplateRegion rect)
    {
        var configuredX = NoteInt(rect, "playhead_x", int.MinValue, -10_000, 10_000);
        if (configuredX == int.MinValue)
        {
            return null;
        }

        configuredX += NoteInt(rect, "playhead_x_offset", 0, -20, 20);
        if (configuredX < rect.X || configuredX >= rect.X + rect.Width)
        {
            return null;
        }

        var overlay = new Image<Rgba32>(rect.Width, rect.Height, new Rgba32(0, 0, 0, 0));
        var localX = configuredX - rect.X;
        var lineLeft = Math.Max(0, localX - 1);
        var lineRight = Math.Min(rect.Width - 1, localX);
        var color = SamplePlayheadColor(canvas, configuredX, rect);
        overlay.Mutate(context => context.Fill(
            color,
            new RectangleF(lineLeft, 0, lineRight - lineLeft + 1, rect.Height)));
        return overlay;
    }

    private static Rgba32 SamplePlayheadColor(
        Image<Rgba32> canvas,
        int x,
        ProjectImageTemplateRegion rect)
    {
        var samples = new List<Rgba32>();
        var ranges = new[]
        {
            (Start: Math.Max(0, rect.Y - 260), End: Math.Clamp(rect.Y, 0, canvas.Height)),
            (Start: Math.Clamp(rect.Y + rect.Height, 0, canvas.Height), End: Math.Clamp(rect.Y + rect.Height + 260, 0, canvas.Height))
        };

        canvas.ProcessPixelRows(accessor =>
        {
            foreach (var range in ranges)
            {
                for (var y = range.Start; y < range.End; y++)
                {
                    var pixel = accessor.GetRowSpan(y)[x];
                    if (IsPlayheadPixel(pixel))
                    {
                        samples.Add(pixel);
                    }
                }
            }
        });

        var red = samples.Count == 0 ? 170 : Median(samples.Select(static pixel => pixel.R));
        var green = samples.Count == 0 ? 170 : Median(samples.Select(static pixel => pixel.G));
        var blue = samples.Count == 0 ? 170 : Median(samples.Select(static pixel => pixel.B));
        var alpha = samples.Count == 0 ? 165 : Median(samples.Select(static pixel => pixel.A));
        var brightnessLimit = NoteInt(rect, "playhead_brightness", 178, 80, 255);
        var alphaLimit = NoteInt(rect, "playhead_alpha", 170, 40, 255);
        var peak = Math.Max(red, Math.Max(green, blue));
        if (peak > brightnessLimit)
        {
            var scale = brightnessLimit / (double)peak;
            red = Math.Clamp((int)Math.Round(red * scale), 0, 255);
            green = Math.Clamp((int)Math.Round(green * scale), 0, 255);
            blue = Math.Clamp((int)Math.Round(blue * scale), 0, 255);
        }

        alpha = Math.Clamp(alpha, Math.Min(120, alphaLimit), alphaLimit);
        return new Rgba32((byte)red, (byte)green, (byte)blue, (byte)alpha);
    }

    private static bool IsPlayheadPixel(Rgba32 pixel)
    {
        var peak = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
        var floor = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
        return pixel.A > 0 && pixel.R >= 150 && pixel.G >= 150 && pixel.B >= 150 && peak - floor <= 55;
    }

    private static int ResolveThumbnailHeight(ProjectImageTemplateRegion rect)
    {
        var value = NoteValue(rect, "thumbnail_height") ?? NoteValue(rect, "thumb_height");
        return int.TryParse(value, out var parsed)
            ? Math.Clamp(parsed, 1, rect.Height)
            : rect.Height;
    }

    private static Rgba32 SampleTrackBackground(
        Image<Rgba32> original,
        int thumbnailY,
        int thumbnailHeight)
    {
        var samples = new List<Rgba32>(original.Width * Math.Max(1, original.Height - thumbnailHeight));
        original.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < original.Height; y++)
            {
                if (y >= thumbnailY && y < thumbnailY + thumbnailHeight)
                {
                    continue;
                }

                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    samples.Add(row[x]);
                }
            }
        });

        if (samples.Count == 0)
        {
            original.ProcessPixelRows(accessor => samples.Add(accessor.GetRowSpan(original.Height / 2)[original.Width / 2]));
        }

        return new Rgba32(
            (byte)Median(samples.Select(static pixel => pixel.R)),
            (byte)Median(samples.Select(static pixel => pixel.G)),
            (byte)Median(samples.Select(static pixel => pixel.B)),
            (byte)Median(samples.Select(static pixel => pixel.A)));
    }

    private static void RenderClippedFallback(
        Image<Rgba32> canvas,
        Rectangle bounds,
        IReadOnlyList<Image<Rgba32>> frames)
    {
        using var thumbnail = ResizeCrop(frames[0], bounds.Width, bounds.Height);
        canvas.Mutate(context => context.DrawImage(thumbnail, bounds.Location, 1f));
    }

    private static Rectangle IntersectWithCanvas(Image<Rgba32> canvas, ProjectImageTemplateRegion rect)
    {
        var left = Math.Clamp(rect.X, 0, canvas.Width);
        var top = Math.Clamp(rect.Y, 0, canvas.Height);
        var right = Math.Clamp(rect.X + rect.Width, left, canvas.Width);
        var bottom = Math.Clamp(rect.Y + rect.Height, top, canvas.Height);
        return new Rectangle(left, top, right - left, bottom - top);
    }

    private static Image<Rgba32> ResizeCrop(Image<Rgba32> source, int width, int height)
    {
        return source.Clone(context => context.Resize(new ResizeOptions
        {
            Size = new Size(Math.Max(1, width), Math.Max(1, height)),
            Mode = ResizeMode.Crop,
            Sampler = KnownResamplers.Lanczos3,
            Position = AnchorPositionMode.Center
        }));
    }

    private static void DrawOverlay(Image<Rgba32> target, Image<Rgba32>? overlay)
    {
        if (overlay is not null)
        {
            target.Mutate(context => context.DrawImage(overlay, Point.Empty, 1f));
        }
    }

    private static string? NoteValue(ProjectImageTemplateRegion rect, string key)
    {
        foreach (var item in (rect.Note ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = item.IndexOf('=');
            if (separator <= 0 || !item[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return item[(separator + 1)..].Trim();
        }

        return null;
    }

    private static int NoteInt(
        ProjectImageTemplateRegion rect,
        string key,
        int fallback,
        int minimum,
        int maximum)
    {
        return int.TryParse(NoteValue(rect, key), out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
    }

    private static int Median(IEnumerable<byte> values)
    {
        var ordered = values.Select(static value => (int)value).OrderBy(static value => value).ToArray();
        return ordered[ordered.Length / 2];
    }
}
