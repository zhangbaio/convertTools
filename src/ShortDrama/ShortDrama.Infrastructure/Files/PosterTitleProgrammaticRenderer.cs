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
        var displayTitle = FormatTitleLines(title);
        var renderLayout = CreateFixedTemplateLayout(displayTitle, width, height);
        var rx = Math.Max(0, (int)Math.Round(width * renderLayout.X));
        var ry = Math.Max(0, (int)Math.Round(height * renderLayout.Y));
        var rw = Math.Min(width - rx, (int)Math.Round(width * renderLayout.Width));
        var rh = Math.Min(height - ry, (int)Math.Round(height * renderLayout.Height));

        if (renderLayout.BackgroundOpacity > 0)
        {
            var alpha = (byte)Math.Clamp((int)Math.Round(renderLayout.BackgroundOpacity * 255), 0, 255);
            var overlay = new Rgba32(renderLayout.BackgroundColor.R, renderLayout.BackgroundColor.G, renderLayout.BackgroundColor.B, alpha);
            canvas.Mutate(ctx => ctx.Fill(overlay, new Rectangle(rx, ry, rw, rh)));
        }

        var fontSize = Math.Max(24, (int)Math.Round(height * renderLayout.FontScale));
        var minimumFontSize = Math.Max(18, (int)Math.Round(height * (56f / 858f)));
        var strokeWidth = Math.Max(2f, fontSize / 18f);
        var font = FitTitleFont(displayTitle, rw, rh, fontSize, minimumFontSize, strokeWidth);
        var textBounds = TextMeasurer.MeasureBounds(
            displayTitle,
            new TextOptions(font) { Dpi = 72 });
        var textWidth = textBounds.Width;
        var textHeight = textBounds.Height;
        var tx = renderLayout.Align switch
        {
            HorizontalAlignment.Left => rx + Math.Max(8, (int)Math.Round(rw * 0.04f)) - textBounds.Left,
            HorizontalAlignment.Right => rx + rw - textWidth - Math.Max(8, (int)Math.Round(rw * 0.04f)) - textBounds.Left,
            _ => rx + (rw - textWidth) / 2f - textBounds.Left,
        };
        var ty = ry + (rh - textHeight) / 2f - textBounds.Top;
        var origin = new PointF(tx, ty);
        var strokeColor = ChooseStrokeColor(renderLayout.TextColor);

        canvas.Mutate(ctx => DrawOutlinedText(ctx, displayTitle, font, renderLayout.TextColor, strokeColor, strokeWidth, origin));

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

        var shadowOffset = Math.Max(2f, strokeWidth * 1.25f);
        ctx.DrawText(title, font, new Rgba32(0, 0, 0, 150), new PointF(origin.X + shadowOffset, origin.Y + shadowOffset));

        foreach (var (ox, oy) in offsets)
        {
            ctx.DrawText(title, font, strokeColor, new PointF(origin.X + ox, origin.Y + oy));
        }

        ctx.DrawText(title, font, fillColor, origin);
    }

    internal static string FormatTitleLines(string? title)
    {
        var normalized = string.Concat((title ?? string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Where(character => character != ' ' && character != '\t'));
        if (normalized.Contains('\n'))
            return string.Join('\n', normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        var length = normalized.Length;
        if (length <= 6)
            return normalized;

        var lineCount = length <= 14 ? 2 : 3;
        if (lineCount == 2)
        {
            var midpoint = length / 2;
            var semanticBreak = normalized
                .Select((character, index) => (character, index))
                .Where(item => item.index >= 3
                    && item.index <= length - 3
                    && item.index <= 7
                    && length - item.index <= 7
                    && "在于与和为被把从向当".Contains(item.character))
                .OrderBy(item => Math.Abs(item.index - midpoint))
                .Select(item => item.index)
                .FirstOrDefault(-1);
            if (semanticBreak > 0)
                return normalized[..semanticBreak] + '\n' + normalized[semanticBreak..];
        }

        var lines = new List<string>(lineCount);
        var offset = 0;
        for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            var remainingCharacters = length - offset;
            var remainingLines = lineCount - lineIndex;
            var take = (int)Math.Ceiling(remainingCharacters / (double)remainingLines);
            lines.Add(normalized.Substring(offset, take));
            offset += take;
        }

        return string.Join('\n', lines);
    }

    internal static PosterTitleLayout CreateFixedTemplateLayout(
        string displayTitle,
        int canvasWidth,
        int canvasHeight)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0 || string.IsNullOrWhiteSpace(displayTitle))
            return default;

        var lines = displayTitle.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lineCount = Math.Max(1, lines.Length);
        var fontScale = 64f / 858f;
        var regionHeight = lineCount * 0.10f + 0.02f;
        var bottomMargin = 100f / 858f;
        var y = Math.Max(0.05f, 1f - bottomMargin - regionHeight);

        return new PosterTitleLayout(
            X: 42f / 600f,
            Y: y,
            Width: 480f / 600f,
            Height: regionHeight,
            FontScale: fontScale,
            TextColor: new Rgba32(255, 255, 255, 255),
            BackgroundColor: new Rgba32(0, 0, 0, 255),
            BackgroundOpacity: 0,
            Align: HorizontalAlignment.Left);
    }

    private static Font FitTitleFont(
        string title,
        int maxWidth,
        int maxHeight,
        int initialSize,
        int minimumSize,
        float strokeWidth)
    {
        var size = Math.Max(minimumSize, initialSize);
        while (size >= minimumSize)
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

        return LoadPosterFont(minimumSize);
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
