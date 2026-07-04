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
        var font = FitTitleFont(title, rw, rh, fontSize);
        var strokeWidth = Math.Max(2f, fontSize / 18f);
        var strokeColor = ChooseStrokeColor(layout.BackgroundColor);
        var textBounds = TextMeasurer.MeasureBounds(title, new TextOptions(font) { Dpi = 72 });
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

        canvas.Mutate(ctx =>
        {
            ctx.DrawText(
                new DrawingOptions { GraphicsOptions = new GraphicsOptions { Antialias = true } },
                title,
                font,
                Brushes.Solid(layout.TextColor),
                Pens.Solid(strokeColor, strokeWidth),
                origin);
        });

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (Path.GetExtension(outputPath).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(outputPath).Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            using var rgb = canvas.CloneAs<Rgba32>();
            rgb.SaveAsJpeg(outputPath);
        }
        else
        {
            canvas.SaveAsPng(outputPath);
        }
    }

    private static Font FitTitleFont(string title, int maxWidth, int maxHeight, int initialSize)
    {
        var size = Math.Max(20, initialSize);
        while (size >= 18)
        {
            var font = LoadPosterFont(size);
            var bounds = TextMeasurer.MeasureBounds(title, new TextOptions(font));
            if (bounds.Width <= maxWidth * 0.96f && bounds.Height <= maxHeight * 0.9f)
                return font;
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
        foreach (var candidate in FontCandidates)
        {
            if (!File.Exists(candidate))
                continue;
            try
            {
                var collection = new FontCollection();
                var family = collection.Add(candidate);
                return family.CreateFont(size, FontStyle.Bold);
            }
            catch
            {
                // try next
            }
        }

        foreach (var familyName in new[] { "Microsoft YaHei", "SimHei", "PingFang SC", "Arial Unicode MS" })
        {
            if (SystemFonts.TryGet(familyName, out var family))
                return family.CreateFont(size, FontStyle.Bold);
        }

        return SystemFonts.CreateFont(SystemFonts.Families.First().Name, size, FontStyle.Bold);
    }

    private static Rgba32 ChooseStrokeColor(Rgba32 background)
    {
        var luminance = (background.R * 299 + background.G * 587 + background.B * 114) / 1000.0;
        return luminance > 80 ? new Rgba32(0, 0, 0, 255) : new Rgba32(18, 18, 18, 255);
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
