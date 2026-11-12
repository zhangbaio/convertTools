using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ShortDrama.Infrastructure.Files;

internal static partial class PosterTitleProgrammaticRenderer
{
    private static readonly FontCollection SharedFontCollection = new();
    private static readonly object FontLoadLock = new();
    private static readonly Dictionary<string, FontFamily> FamiliesByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<PosterTitleScript, FontFamily> FamiliesByScript = [];

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
        var placed = PlaceFixedTitle(title, width, height);
        var displayTitle = placed.Text;
        var renderLayout = placed.Layout;
        var rx = placed.Rect.X;
        var ry = placed.Rect.Y;
        var rw = placed.Rect.Width;
        var rh = placed.Rect.Height;

        if (renderLayout.BackgroundOpacity > 0)
        {
            var alpha = (byte)Math.Clamp((int)Math.Round(renderLayout.BackgroundOpacity * 255), 0, 255);
            var overlay = new Rgba32(renderLayout.BackgroundColor.R, renderLayout.BackgroundColor.G, renderLayout.BackgroundColor.B, alpha);
            canvas.Mutate(ctx => ctx.Fill(overlay, new Rectangle(rx, ry, rw, rh)));
        }

        var font = placed.Font;
        var strokeWidth = placed.StrokeWidth;
        var textBounds = MeasureText(displayTitle, font, placed.Script);
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

        canvas.Mutate(ctx => DrawOutlinedText(ctx, displayTitle, font, renderLayout.TextColor, strokeColor, strokeWidth, origin, placed.Script));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        SaveCanvas(canvas, outputPath);
    }

    public static void RenderLowerHalf(string inputPath, string outputPath, string title)
    {
        using var source = Image.Load<Rgba32>(inputPath);
        using var canvas = source.CloneAs<Rgba32>();
        var width = canvas.Width;
        var height = canvas.Height;
        var placed = PlaceLowerHalfTitle(title, width, height);
        var displayTitle = placed.Text;
        var renderLayout = placed.Layout;
        var rx = placed.Rect.X;
        var ry = placed.Rect.Y;
        var rw = placed.Rect.Width;
        var rh = placed.Rect.Height;
        var font = placed.Font;
        var strokeWidth = placed.StrokeWidth;
        var textBounds = MeasureText(displayTitle, font, placed.Script);
        var origin = new PointF(
            rx + (rw - textBounds.Width) / 2f - textBounds.Left,
            ry + (rh - textBounds.Height) / 2f - textBounds.Top);
        canvas.Mutate(ctx => DrawOutlinedText(
            ctx,
            displayTitle,
            font,
            renderLayout.TextColor,
            new Rgba32(0, 0, 0, 255),
            strokeWidth,
            origin,
            placed.Script));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        SaveCanvas(canvas, outputPath);
    }

    private static void SaveCanvas(Image<Rgba32> canvas, string outputPath)
    {
        if (Path.GetExtension(outputPath).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(outputPath).Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            canvas.SaveAsJpeg(outputPath);
            return;
        }

        canvas.SaveAsPng(outputPath);
    }

    private static void DrawOutlinedText(
        IImageProcessingContext ctx,
        string title,
        Font font,
        Rgba32 fillColor,
        Rgba32 strokeColor,
        float strokeWidth,
        PointF origin,
        PosterTitleScript script)
    {
        var offsets = new (float X, float Y)[]
        {
            (-strokeWidth, 0), (strokeWidth, 0), (0, -strokeWidth), (0, strokeWidth),
            (-strokeWidth, -strokeWidth), (strokeWidth, -strokeWidth),
            (-strokeWidth, strokeWidth), (strokeWidth, strokeWidth),
        };

        var shadowOffset = Math.Max(2f, strokeWidth * 1.25f);
        DrawTextRun(ctx, title, font, new Rgba32(0, 0, 0, 150), new PointF(origin.X + shadowOffset, origin.Y + shadowOffset), script);

        foreach (var (ox, oy) in offsets)
        {
            DrawTextRun(ctx, title, font, strokeColor, new PointF(origin.X + ox, origin.Y + oy), script);
        }

        DrawTextRun(ctx, title, font, fillColor, origin, script);
    }

    private static void DrawTextRun(
        IImageProcessingContext ctx,
        string title,
        Font font,
        Rgba32 color,
        PointF origin,
        PosterTitleScript script)
    {
        if (script != PosterTitleScript.Arabic)
        {
            ctx.DrawText(title, font, color, origin);
            return;
        }

        ctx.DrawText(CreateTextOptions(font, origin, script), title, color);
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

    internal static string FormatLowerHalfLines(string? title) => FormatBalancedPosterLines(title);

    internal static PosterTitleLayout CreateLowerHalfLayout(
        string displayTitle,
        int canvasWidth,
        int canvasHeight)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0 || string.IsNullOrWhiteSpace(displayTitle))
            return default;

        var lineCount = Math.Max(1, displayTitle.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        var regionHeight = Math.Min(0.42f, lineCount * 0.11f + 0.06f);
        var bottomMargin = 0.06f;
        var y = Math.Max(0.50f, 1f - bottomMargin - regionHeight);
        return new PosterTitleLayout(
            X: 0.07f,
            Y: y,
            Width: 0.86f,
            Height: regionHeight,
            FontScale: 0.078f,
            TextColor: new Rgba32(255, 214, 0, 255),
            BackgroundColor: new Rgba32(0, 0, 0, 255),
            BackgroundOpacity: 0,
            Align: HorizontalAlignment.Center);
    }

    private readonly record struct PixelRect(int X, int Y, int Width, int Height);

    private readonly record struct PlacedTitle(
        string Text,
        Font Font,
        float StrokeWidth,
        PosterTitleScript Script,
        PosterTitleLayout Layout,
        PixelRect Rect);

    private static PlacedTitle PlaceFixedTitle(string title, int canvasWidth, int canvasHeight)
    {
        var script = ClassifyTitleScript(title);
        if (script == PosterTitleScript.Chinese)
        {
            var text = FormatTitleLines(title);
            var layout = CreateFixedTemplateLayout(text, canvasWidth, canvasHeight);
            var rect = ToPixelRect(layout, canvasWidth, canvasHeight);
            var fontSize = Math.Max(24, (int)Math.Round(canvasHeight * layout.FontScale));
            var minimumFontSize = Math.Max(18, (int)Math.Round(canvasHeight * (56f / 858f)));
            var strokeWidth = Math.Max(2f, fontSize / 18f);
            var font = FitTitleFont(text, rect.Width, rect.Height, fontSize, minimumFontSize, strokeWidth);
            return new PlacedTitle(text, font, strokeWidth, script, layout, rect);
        }

        return PlaceMeasuredTitle(
            title,
            script,
            canvasWidth,
            canvasHeight,
            Math.Max(1, (int)Math.Round(canvasWidth * (480f / 600f))),
            Math.Max(24, (int)Math.Round(canvasHeight * (64f / 858f))),
            Math.Max(18, (int)Math.Round(canvasHeight * (56f / 858f))),
            lowerHalf: false);
    }

    private static PlacedTitle PlaceLowerHalfTitle(string title, int canvasWidth, int canvasHeight)
    {
        var script = ClassifyTitleScript(title);
        if (script == PosterTitleScript.Chinese)
        {
            var text = FormatTitleLines(title);
            var layout = CreateLowerHalfLayout(text, canvasWidth, canvasHeight);
            var rect = ToPixelRect(layout, canvasWidth, canvasHeight);
            var fontSize = Math.Max(28, (int)Math.Round(canvasHeight * layout.FontScale));
            var minimumFontSize = Math.Max(18, (int)Math.Round(canvasHeight * (40f / 858f)));
            var strokeWidth = Math.Max(3f, fontSize / 12f);
            var font = FitTitleFont(text, rect.Width, rect.Height, fontSize, minimumFontSize, strokeWidth);
            return new PlacedTitle(text, font, strokeWidth, script, layout, rect);
        }

        return PlaceMeasuredTitle(
            title,
            script,
            canvasWidth,
            canvasHeight,
            Math.Max(1, (int)Math.Round(canvasWidth * 0.86f)),
            Math.Max(28, (int)Math.Round(canvasHeight * 0.078f)),
            Math.Max(18, (int)Math.Round(canvasHeight * (40f / 858f))),
            lowerHalf: true);
    }

    private static PlacedTitle PlaceMeasuredTitle(
        string title,
        PosterTitleScript script,
        int canvasWidth,
        int canvasHeight,
        int boxWidth,
        int initialSize,
        int minimumSize,
        bool lowerHalf)
    {
        var prepared = PreparePosterTitle(title, script);
        var size = Math.Max(minimumSize, initialSize);
        Font? chosenFont = null;
        var chosenText = prepared;
        var chosenStroke = lowerHalf ? Math.Max(3f, size / 12f) : Math.Max(2f, size / 18f);
        var chosenLayout = lowerHalf
            ? CreateLowerHalfLayout(prepared, canvasWidth, canvasHeight)
            : CreateFixedTemplateLayout(prepared, canvasWidth, canvasHeight);
        while (size >= minimumSize)
        {
            var font = LoadPosterFont(script, size);
            var stroke = lowerHalf ? Math.Max(3f, size / 12f) : Math.Max(2f, size / 18f);
            var widthLimit = Math.Max(1f, boxWidth * 0.96f - stroke * 2f);
            var text = FormatMeasuredPosterLines(prepared, widthLimit, line => MeasureLineWidth(line, font, script));
            var layout = lowerHalf
                ? CreateLowerHalfLayout(text, canvasWidth, canvasHeight)
                : CreateFixedTemplateLayout(text, canvasWidth, canvasHeight);
            var regionHeight = Math.Max(1f, canvasHeight * layout.Height);
            var bounds = MeasureText(text, font, script);
            chosenFont = font;
            chosenText = text;
            chosenStroke = stroke;
            chosenLayout = layout;
            if (bounds.Width + stroke * 2f <= boxWidth * 0.96f
                && bounds.Height + stroke * 2f <= regionHeight * 0.9f)
            {
                break;
            }

            size -= 2;
        }

        return new PlacedTitle(
            chosenText,
            chosenFont ?? LoadPosterFont(script, minimumSize),
            chosenStroke,
            script,
            chosenLayout,
            ToPixelRect(chosenLayout, canvasWidth, canvasHeight));
    }

    private static PixelRect ToPixelRect(PosterTitleLayout layout, int canvasWidth, int canvasHeight)
    {
        var rx = Math.Max(0, (int)Math.Round(canvasWidth * layout.X));
        var ry = Math.Max(0, (int)Math.Round(canvasHeight * layout.Y));
        var rw = Math.Min(canvasWidth - rx, (int)Math.Round(canvasWidth * layout.Width));
        var rh = Math.Min(canvasHeight - ry, (int)Math.Round(canvasHeight * layout.Height));
        return new PixelRect(rx, ry, rw, rh);
    }

    private static FontRectangle MeasureText(string text, Font font, PosterTitleScript script)
    {
        var options = script == PosterTitleScript.Arabic
            ? CreateTextOptions(font, PointF.Empty, script)
            : new TextOptions(font) { Dpi = 72 };
        return TextMeasurer.MeasureBounds(text, options);
    }

    private static float MeasureLineWidth(string text, Font font, PosterTitleScript script) =>
        text.Length == 0 ? 0 : MeasureText(text, font, script).Width;

    private static RichTextOptions CreateTextOptions(Font font, PointF origin, PosterTitleScript script)
    {
        var options = new RichTextOptions(font)
        {
            Dpi = 72,
            Origin = origin,
            WrappingLength = float.MaxValue,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        if (script == PosterTitleScript.Arabic)
            options.TextDirection = TextDirection.RightToLeft;
        return options;
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
            var font = LoadPosterFont(PosterTitleScript.Chinese, size);
            var bounds = TextMeasurer.MeasureBounds(title, new TextOptions(font) { Dpi = 72 });
            if (bounds.Width + strokeWidth * 2 <= maxWidth * 0.96f
                && bounds.Height + strokeWidth * 2 <= maxHeight * 0.9f)
            {
                return font;
            }

            size -= 2;
        }

        return LoadPosterFont(PosterTitleScript.Chinese, minimumSize);
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

    private static Font LoadPosterFont(PosterTitleScript script, int size)
    {
        var family = ResolvePosterFontFamily(script);
        try
        {
            return family.CreateFont(size, FontStyle.Bold);
        }
        catch (Exception)
        {
            return family.CreateFont(size, FontStyle.Regular);
        }
    }

    private static FontFamily ResolvePosterFontFamily(PosterTitleScript script)
    {
        lock (FontLoadLock)
        {
            if (FamiliesByScript.TryGetValue(script, out var cached))
                return cached;

            foreach (var candidate in FontFilesFor(script))
            {
                if (!File.Exists(candidate))
                    continue;

                try
                {
                    var family = AddFontFile(candidate);
                    FamiliesByScript[script] = family;
                    return family;
                }
                catch
                {
                    // try next
                }
            }

            foreach (var familyName in FontFamilyNamesFor(script))
            {
                if (SystemFonts.TryGet(familyName, out var family))
                {
                    FamiliesByScript[script] = family;
                    return family;
                }
            }

            if (script != PosterTitleScript.Chinese && FamiliesByScript.TryGetValue(PosterTitleScript.Chinese, out var chinese))
            {
                FamiliesByScript[script] = chinese;
                return chinese;
            }

            var fallback = SystemFonts.Families.First();
            FamiliesByScript[script] = fallback;
            return fallback;
        }
    }

    private static FontFamily AddFontFile(string path)
    {
        if (FamiliesByPath.TryGetValue(path, out var existing))
            return existing;

        var family = SharedFontCollection.Add(path);
        FamiliesByPath[path] = family;
        return family;
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
