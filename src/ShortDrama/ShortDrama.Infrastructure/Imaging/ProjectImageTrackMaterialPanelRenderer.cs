using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ShortDrama.Infrastructure.Imaging;

/// <summary>
/// Renders the four-column track-frame material grid used by project-image
/// template 7. The supplied region is the manifest's unexpanded material_panel.
/// </summary>
public static class ProjectImageTrackMaterialPanelRenderer
{
    private static readonly Rgba32 ItemBackground = new(55, 55, 58, 255);

    /// <returns>
    /// <see langword="true"/> when the region selected track frames and the
    /// panel was rendered; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryRender(
        Image<Rgba32> canvas,
        ProjectImageTemplateRegion manifestRect,
        IReadOnlyList<Image<Rgba32>> trackFrames,
        double episodeDurationSeconds)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(manifestRect);
        ArgumentNullException.ThrowIfNull(trackFrames);

        var source = (NoteValue(manifestRect, "source") ?? string.Empty).Trim().ToLowerInvariant();
        if (source is not ("track" or "track_frames" or "current_episode") || trackFrames.Count == 0)
        {
            return false;
        }

        var requestedCount = NoteInt(
            manifestRect,
            "item_count",
            Math.Min(16, trackFrames.Count),
            1,
            16);
        var itemCount = Math.Min(Math.Min(16, requestedCount), trackFrames.Count);
        if (itemCount <= 0)
        {
            return false;
        }

        var panelRect = ResolvePanelRectangle(canvas, manifestRect);
        if (panelRect.Width <= 0 || panelRect.Height <= 0)
        {
            return false;
        }

        var panelBackground = SamplePanelBackground(canvas, panelRect);
        var eraseRect = Intersect(
            new Rectangle(panelRect.X, panelRect.Y - 2, panelRect.Width, panelRect.Height + 2),
            new Rectangle(0, 0, canvas.Width, canvas.Height));
        Fill(canvas, eraseRect, panelBackground);

        using var panel = new Image<Rgba32>(panelRect.Width, panelRect.Height, panelBackground);
        RenderItems(panel, trackFrames, itemCount, episodeDurationSeconds);
        canvas.Mutate(context => context.DrawImage(panel, panelRect.Location, 1f));
        return true;
    }

    private static void RenderItems(
        Image<Rgba32> panel,
        IReadOnlyList<Image<Rgba32>> frames,
        int itemCount,
        double episodeDurationSeconds)
    {
        const int columns = 4;
        const int rows = 4;
        const int itemGap = 8;
        const int labelHeight = 14;
        const int labelGap = 4;
        const int cardRadius = 8;

        var itemWidth = Math.Max(60, (panel.Width - itemGap * (columns + 1)) / columns);
        var slotHeight = Math.Max(76, (panel.Height - itemGap * (rows + 1)) / rows);
        var itemHeight = Math.Max(56, slotHeight - labelHeight - labelGap);
        var nameFont = GetFont(10, bold: false);
        var durationFont = GetFont(9, bold: true);
        var statusFont = GetFont(8, bold: true);

        for (var index = 0; index < itemCount; index++)
        {
            var column = index % columns;
            var row = index / columns;
            var x = itemGap + column * (itemWidth + itemGap);
            var y = itemGap + row * (slotHeight + itemGap) + (row == 0 ? 2 : 0);
            if (x >= panel.Width || y >= panel.Height)
            {
                break;
            }

            var actualWidth = Math.Min(itemWidth, panel.Width - x);
            var actualHeight = Math.Min(itemHeight, panel.Height - y);
            if (actualWidth <= 0 || actualHeight <= 0)
            {
                continue;
            }

            using var card = ResizeBoxPad(frames[index], actualWidth, actualHeight, ItemBackground);
            ApplyRoundedCorners(card, Math.Min(cardRadius, Math.Min(actualWidth, actualHeight) / 2));
            panel.Mutate(context => context.DrawImage(card, new Point(x, y), 1f));

            DrawStatusBadge(panel, x, y, actualWidth, statusFont);
            var duration = Math.Max(0d, Math.Min(10d, episodeDurationSeconds - index * 10d));
            DrawDurationBadge(panel, x, y, actualWidth, FormatDuration(duration), durationFont);

            var title = FitTextMiddle($"素材{index + 1}.mp4", nameFont, actualWidth);
            var nameBounds = TextMeasurer.MeasureBounds(title, new TextOptions(nameFont));
            var nameY = y + actualHeight + labelGap + Math.Max(0f, (labelHeight - nameBounds.Height) / 2f) - 1f;
            panel.Mutate(context => context.DrawText(
                title,
                nameFont,
                new Rgba32(218, 218, 218, 255),
                new PointF(x, nameY)));
        }
    }

    private static void DrawStatusBadge(
        Image<Rgba32> panel,
        int x,
        int y,
        int itemWidth,
        Font font)
    {
        const string status = "已添加";
        var bounds = TextMeasurer.MeasureBounds(status, new TextOptions(font));
        var width = Math.Min(itemWidth - 6, Math.Max(29, (int)Math.Ceiling(bounds.Width) + 6));
        if (width <= 0)
        {
            return;
        }

        DrawRoundedBadge(panel, new Rectangle(x + 3, y + 3, width, 15), 3, new Rgba32(8, 12, 14, 210));
        panel.Mutate(context => context.DrawText(
            status,
            font,
            new Rgba32(238, 242, 244, 255),
            new PointF(x + 6, y + 4)));
    }

    private static void DrawDurationBadge(
        Image<Rgba32> panel,
        int x,
        int y,
        int itemWidth,
        string duration,
        Font font)
    {
        var bounds = TextMeasurer.MeasureBounds(duration, new TextOptions(font));
        var width = Math.Max(12, (int)Math.Ceiling(bounds.Width) + 8);
        width = Math.Min(itemWidth - 8, width);
        if (width <= 0)
        {
            return;
        }

        var left = x + itemWidth - width - 4;
        DrawRoundedBadge(panel, new Rectangle(left, y + 4, width, 16), 3, new Rgba32(0, 0, 0, 185));
        panel.Mutate(context => context.DrawText(
            duration,
            font,
            new Rgba32(245, 245, 245, 255),
            new PointF(x + itemWidth - width, y + 5)));
    }

    private static void DrawRoundedBadge(
        Image<Rgba32> panel,
        Rectangle rectangle,
        int radius,
        Rgba32 color)
    {
        var clipped = Intersect(rectangle, new Rectangle(0, 0, panel.Width, panel.Height));
        if (clipped.Width <= 0 || clipped.Height <= 0)
        {
            return;
        }

        using var badge = new Image<Rgba32>(clipped.Width, clipped.Height, color);
        ApplyRoundedCorners(badge, Math.Min(radius, Math.Min(clipped.Width, clipped.Height) / 2));
        panel.Mutate(context => context.DrawImage(badge, clipped.Location, 1f));
    }

    private static Rectangle ResolvePanelRectangle(
        Image<Rgba32> canvas,
        ProjectImageTemplateRegion rect)
    {
        // Python composition expands top=20, bottom=18 and left=8, then
        // insets top/bottom by 18 before rendering the track-frame grid.
        var left = Math.Max(0, rect.X - 8);
        var top = Math.Max(0, rect.Y - 20 + 18);
        var right = Math.Min(canvas.Width, rect.X + rect.Width);
        var expandedBottom = Math.Min(canvas.Height, rect.Y + rect.Height + 18);
        var bottom = Math.Max(top, expandedBottom - 18);
        return new Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static Rgba32 SamplePanelBackground(Image<Rgba32> canvas, Rectangle rect)
    {
        var samples = new List<Rgba32>();
        var band = Math.Max(2, Math.Min(8, rect.Height / 40));
        var gutterX = Math.Max(4, Math.Min(14, rect.Width / 40));
        var gutterY = Math.Max(4, Math.Min(14, rect.Height / 32));
        Rectangle[] sampleRectangles =
        [
            new(rect.X, rect.Y, rect.Width, band),
            new(rect.X, rect.Bottom - band, rect.Width, band),
            new(rect.X, rect.Y, band, rect.Height),
            new(rect.Right - band, rect.Y, band, rect.Height),
            new(rect.X + gutterX, rect.Y + gutterY, gutterX, Math.Max(0, rect.Height - gutterY * 2))
        ];

        canvas.ProcessPixelRows(accessor =>
        {
            foreach (var sampleRect in sampleRectangles)
            {
                var clipped = Intersect(sampleRect, new Rectangle(0, 0, canvas.Width, canvas.Height));
                for (var y = clipped.Y; y < clipped.Bottom; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = clipped.X; x < clipped.Right; x++)
                    {
                        if (row[x].A > 0)
                        {
                            samples.Add(row[x]);
                        }
                    }
                }
            }
        });

        if (samples.Count == 0)
        {
            return new Rgba32(31, 31, 34, 255);
        }

        return new Rgba32(
            (byte)Median(samples.Select(static pixel => pixel.R)),
            (byte)Median(samples.Select(static pixel => pixel.G)),
            (byte)Median(samples.Select(static pixel => pixel.B)),
            (byte)Median(samples.Select(static pixel => pixel.A)));
    }

    private static Image<Rgba32> ResizeBoxPad(
        Image<Rgba32> source,
        int width,
        int height,
        Rgba32 padColor)
    {
        var result = new Image<Rgba32>(width, height, padColor);
        var scale = Math.Min(width / (double)source.Width, height / (double)source.Height);
        var scaledWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        var scaledHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var resized = source.Clone(context => context.Resize(new ResizeOptions
        {
            Size = new Size(scaledWidth, scaledHeight),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));
        result.Mutate(context => context.DrawImage(
            resized,
            new Point((width - scaledWidth) / 2, (height - scaledHeight) / 2),
            1f));
        return result;
    }

    private static void ApplyRoundedCorners(Image<Rgba32> image, int radius)
    {
        if (radius <= 0)
        {
            return;
        }

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < image.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < image.Width; x++)
                {
                    var cornerX = x < radius ? radius - x - 0.5 : x >= image.Width - radius ? x - (image.Width - radius) + 0.5 : 0;
                    var cornerY = y < radius ? radius - y - 0.5 : y >= image.Height - radius ? y - (image.Height - radius) + 0.5 : 0;
                    if (cornerX > 0 && cornerY > 0 && cornerX * cornerX + cornerY * cornerY > radius * radius)
                    {
                        row[x] = new Rgba32(0, 0, 0, 0);
                    }
                }
            }
        });
    }

    private static string FitTextMiddle(string text, Font font, int maxWidth)
    {
        if (TextMeasurer.MeasureBounds(text, new TextOptions(font)).Width <= maxWidth)
        {
            return text;
        }

        const string ellipsis = "...";
        var ellipsisWidth = TextMeasurer.MeasureBounds(ellipsis, new TextOptions(font)).Width;
        if (ellipsisWidth >= maxWidth)
        {
            return ellipsis;
        }

        var budget = maxWidth - ellipsisWidth;
        var headBudget = budget * 0.55f;
        var head = new List<char>();
        var tail = new LinkedList<char>();
        var usedHead = 0f;
        var usedTail = 0f;
        foreach (var character in text)
        {
            var width = TextMeasurer.MeasureBounds(character.ToString(), new TextOptions(font)).Width;
            if (usedHead + width > headBudget)
            {
                break;
            }

            head.Add(character);
            usedHead += width;
        }

        foreach (var character in text.Skip(head.Count).Reverse())
        {
            var width = TextMeasurer.MeasureBounds(character.ToString(), new TextOptions(font)).Width;
            if (usedTail + width > budget - usedHead)
            {
                break;
            }

            tail.AddFirst(character);
            usedTail += width;
        }

        return string.Concat(head) + ellipsis + string.Concat(tail);
    }

    private static Font GetFont(float size, bool bold)
    {
        string[] candidates =
        [
            "Microsoft YaHei", "Noto Sans CJK SC", "Noto Sans SC",
            "WenQuanYi Micro Hei", "PingFang SC", "Arial Unicode MS", "Arial"
        ];
        foreach (var name in candidates)
        {
            if (SystemFonts.TryGet(name, out var family))
            {
                return CreateFont(family, size, bold);
            }
        }

        var fallback = SystemFonts.Families.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(fallback.Name))
        {
            throw new InvalidOperationException("未找到可用于工程图素材面板的字体。");
        }

        return CreateFont(fallback, size, bold);
    }

    private static Font CreateFont(FontFamily family, float size, bool bold)
    {
        try
        {
            return family.CreateFont(size, bold ? FontStyle.Bold : FontStyle.Regular);
        }
        catch (FontException) when (bold)
        {
            return family.CreateFont(size, FontStyle.Regular);
        }
    }

    private static string FormatDuration(double seconds)
    {
        var totalSeconds = Math.Max(0, (int)Math.Floor(seconds));
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds / 60 % 60;
        var remainingSeconds = totalSeconds % 60;
        return hours > 0
            ? $"{hours:00}:{minutes:00}:{remainingSeconds:00}"
            : $"{minutes:00}:{remainingSeconds:00}";
    }

    private static void Fill(Image<Rgba32> image, Rectangle rectangle, Rgba32 color)
    {
        if (rectangle.Width > 0 && rectangle.Height > 0)
        {
            image.Mutate(context => context.Fill(color, rectangle));
        }
    }

    private static Rectangle Intersect(Rectangle first, Rectangle second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        return right <= left || bottom <= top
            ? Rectangle.Empty
            : new Rectangle(left, top, right - left, bottom - top);
    }

    private static string? NoteValue(ProjectImageTemplateRegion rect, string key)
    {
        foreach (var item in (rect.Note ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = item.IndexOf('=');
            if (separator > 0 && item[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return item[(separator + 1)..].Trim();
            }
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
