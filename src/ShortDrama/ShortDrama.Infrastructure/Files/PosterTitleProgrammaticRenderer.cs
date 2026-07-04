using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ShortDrama.Infrastructure.Files;

internal static class PosterTitleProgrammaticRenderer
{
    private static readonly string[] FontCandidates =
    [
        "C:/Windows/Fonts/msyhbd.ttc",
        "C:/Windows/Fonts/msyh.ttc",
        "C:/Windows/Fonts/simhei.ttf",
        "C:/Windows/Fonts/simsun.ttc",
        "/System/Library/Fonts/PingFang.ttc",
        "/System/Library/Fonts/STHeiti Medium.ttc",
    ];

    private static readonly FontCollection SharedFontCollection = new();
    private static readonly object FontLoadLock = new();
    private static FontFamily? _cachedPosterFamily;

    public static void Render(
        string inputPath,
        string outputPath,
        string title,
        PosterTitleLayout layout)
    {
        using var source = Image.Load<Rgba32>(inputPath);
        using var canvas = source.CloneAs<Rgba32>();

        var width = canvas.Width;
        var height = canvas.Height;
        var rx = Math.Max(0, (int)Math.Round(width * layout.X));
        var ry = Math.Max(0, (int)Math.Round(height * layout.Y));
        var rw = Math.Min(width - rx, (int)Math.Round(width * layout.Width));
        var rh = Math.Min(height - ry, (int)Math.Round(height * layout.Height));

        if (layout.BackgroundOpacity > 0)
        {
            var alpha = (byte)Math.Clamp((int)Math.Round(layout.BackgroundOpacity * 255), 0, 255);
            var overlay = new Rgba32(layout.BackgroundColor.R, layout.BackgroundColor.G, layout.BackgroundColor.B, alpha);
            canvas.Mutate(ctx => ctx.Fill(overlay, new Rectangle(rx, ry, rw, rh)));
        }

        var fontSize = Math.Max(24, (int)Math.Round(height * layout.FontScale));
        var strokeWidth = Math.Max(2f, fontSize / 18f);
        var font = FitTitleFont(title, rw, rh, fontSize, strokeWidth);
        var textBounds = TextMeasurer.MeasureBounds(
            title,
            new TextOptions(font) { Dpi = 72 });
        var textWidth = textBounds.Width;
        var textHeight = textBounds.Height;
        var tx = layout.Align switch
        {
            HorizontalAlignment.Left => rx + Math.Max(8, (int)Math.Round(rw * 0.04f)) - textBounds.Left,
            HorizontalAlignment.Right => rx + rw - textWidth - Math.Max(8, (int)Math.Round(rw * 0.04f)) - textBounds.Left,
            _ => rx + (rw - textWidth) / 2f - textBounds.Left,
        };
        var ty = ry + (rh - textHeight) / 2f - textBounds.Top;
        var origin = new PointF(tx, ty);
        var strokeColor = ChooseStrokeColor(layout.TextColor);

        canvas.Mutate(ctx => DrawOutlinedText(ctx, title, font, layout.TextColor, strokeColor, strokeWidth, origin));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (Path.GetExtension(outputPath).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(outputPath).Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            canvas.SaveAsJpeg(outputPath);
        }
        else
        {
            canvas.SaveAsPng(outputPath);
        }
    }

    private static void DrawOutlinedText(
        IImageProcessingContext ctx,
        string title,
        Font font,
        Rgba32 fillColor,
        Rgba32 strokeColor,
        float strokeWidth,
        PointF origin)
    {
        var offsets = new (float X, float Y)[]
        {
            (-strokeWidth, 0), (strokeWidth, 0), (0, -strokeWidth), (0, strokeWidth),
            (-strokeWidth, -strokeWidth), (strokeWidth, -strokeWidth),
            (-strokeWidth, strokeWidth), (strokeWidth, strokeWidth),
        };

        foreach (var (ox, oy) in offsets)
        {
            ctx.DrawText(title, font, strokeColor, new PointF(origin.X + ox, origin.Y + oy));
        }

        ctx.DrawText(title, font, fillColor, origin);
    }

    private static Font FitTitleFont(string title, int maxWidth, int maxHeight, int initialSize, float strokeWidth)
    {
        var size = Math.Max(20, initialSize);
        while (size >= 18)
        {
            var font = LoadPosterFont(size);
            var bounds = TextMeasurer.MeasureBounds(title, new TextOptions(font) { Dpi = 72 });
            if (bounds.Width + strokeWidth * 2 <= maxWidth * 0.96f
                && bounds.Height + strokeWidth * 2 <= maxHeight * 0.9f)
            {
                return font;
            }

            size -= 2;
        }

        return LoadPosterFont(18);
    }

    public static PosterTitleLayout ToTitleLayout(
        float x,
        float y,
        float width,
        float height,
        float fontScale,
        Rgba32 textColor,
        Rgba32 backgroundColor,
        float backgroundOpacity,
        HorizontalAlignment align) =>
        new(x, y, width, height, fontScale, textColor, backgroundColor, backgroundOpacity, align);

    private static Font LoadPosterFont(int size)
    {
        var family = ResolvePosterFontFamily();
        return family.CreateFont(size, FontStyle.Bold);
    }

    private static FontFamily ResolvePosterFontFamily()
    {
        if (_cachedPosterFamily is not null)
        {
            return _cachedPosterFamily.Value;
        }

        lock (FontLoadLock)
        {
            if (_cachedPosterFamily is not null)
            {
                return _cachedPosterFamily.Value;
            }

            foreach (var candidate in FontCandidates)
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    _cachedPosterFamily = SharedFontCollection.Add(candidate);
                    return _cachedPosterFamily.Value;
                }
                catch
                {
                    // try next
                }
            }

            foreach (var familyName in new[] { "Microsoft YaHei", "SimHei", "PingFang SC", "Arial Unicode MS" })
            {
                if (SystemFonts.TryGet(familyName, out var family))
                {
                    _cachedPosterFamily = family;
                    return family;
                }
            }

            _cachedPosterFamily = SystemFonts.Families.First();
            return _cachedPosterFamily.Value;
        }
    }

    private static Rgba32 ChooseStrokeColor(Rgba32 textColor)
    {
        var luminance = (textColor.R * 299 + textColor.G * 587 + textColor.B * 114) / 1000.0;
        return luminance > 140 ? new Rgba32(0, 0, 0, 255) : new Rgba32(18, 18, 18, 255);
    }
}

internal readonly record struct PosterTitleLayout(
    float X,
    float Y,
    float Width,
    float Height,
    float FontScale,
    Rgba32 TextColor,
    Rgba32 BackgroundColor,
    float BackgroundOpacity,
    HorizontalAlignment Align);
