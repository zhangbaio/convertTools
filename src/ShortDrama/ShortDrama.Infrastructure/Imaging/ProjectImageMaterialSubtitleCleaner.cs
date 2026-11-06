using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ShortDrama.Infrastructure.Imaging;

/// <summary>
/// Removes bright caption glyphs from material-card frames. A saved subtitle
/// profile is used when present; otherwise the lower caption band is cleaned.
/// </summary>
public static class ProjectImageMaterialSubtitleCleaner
{
    private static readonly (double X0, double Y0, double X1, double Y1) LandscapeRegion = (0.10, 0.55, 0.90, 0.95);
    private static readonly (double X0, double Y0, double X1, double Y1) PortraitRegion = (0.08, 0.48, 0.92, 0.95);

    public static Image<Rgba32> Clean(Image<Rgba32> frame, string? projectDirectory)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var result = frame.Clone();
        if (result.Width <= 0 || result.Height <= 0)
        {
            return result;
        }

        var region = LoadRegion(projectDirectory)
            ?? (result.Height > result.Width ? PortraitRegion : LandscapeRegion);
        var (left, top, right, bottom) = Absolute(region, result.Width, result.Height);
        var roiWidth = right - left;
        var roiHeight = bottom - top;
        if (roiWidth <= 1 || roiHeight <= 1)
        {
            return result;
        }

        using var roi = result.Clone(context => context.Crop(new Rectangle(left, top, roiWidth, roiHeight)));
        using var blurred = roi.Clone(context => context.GaussianBlur(3f));
        var gray = new byte[roiWidth * roiHeight];
        var background = new byte[gray.Length];
        roi.ProcessPixelRows(blurred, (sourceAccessor, blurAccessor) =>
        {
            for (var y = 0; y < roiHeight; y++)
            {
                var sourceRow = sourceAccessor.GetRowSpan(y);
                var blurRow = blurAccessor.GetRowSpan(y);
                for (var x = 0; x < roiWidth; x++)
                {
                    var offset = y * roiWidth + x;
                    gray[offset] = Luma(sourceRow[x]);
                    background[offset] = Luma(blurRow[x]);
                }
            }
        });

        var threshold = Math.Max(135, Percentile(gray, 76));
        var mask = new bool[gray.Length];
        for (var index = 0; index < gray.Length; index++)
        {
            mask[index] = gray[index] >= threshold && gray[index] - background[index] >= 6;
        }

        KeepSubtitleLikeComponents(mask, roiWidth, roiHeight);
        var fill = MedianUnmasked(roi, mask, roiWidth, roiHeight);
        for (var y = 0; y < roiHeight; y++)
        {
            for (var x = 0; x < roiWidth; x++)
            {
                if (!mask[y * roiWidth + x])
                {
                    continue;
                }

                result[left + x, top + y] = fill;
            }
        }

        return result;
    }

    private static (double X0, double Y0, double X1, double Y1)? LoadRegion(string? projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return null;
        }

        var path = Path.Combine(projectDirectory, "jianying_media", "series-subtitle-profile.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("regions", out var regions) ||
                regions.ValueKind != System.Text.Json.JsonValueKind.Array ||
                regions.GetArrayLength() == 0)
            {
                return null;
            }

            var first = regions[0];
            if (first.ValueKind != System.Text.Json.JsonValueKind.Array || first.GetArrayLength() != 4)
            {
                return null;
            }

            var values = first.EnumerateArray().Select(item => item.GetDouble()).ToArray();
            if (values.Any(value => value is < 0 or > 1) || values[0] >= values[2] || values[1] >= values[3])
            {
                return null;
            }

            return (values[0], values[1], values[2], values[3]);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static (int Left, int Top, int Right, int Bottom) Absolute(
        (double X0, double Y0, double X1, double Y1) region,
        int width,
        int height)
    {
        var marginX = Math.Max(2, (int)Math.Round(width * 0.006));
        var marginY = Math.Max(2, (int)Math.Round(height * 0.006));
        var left = Math.Clamp((int)Math.Round(region.X0 * width) - marginX, 0, width - 1);
        var top = Math.Clamp((int)Math.Round(region.Y0 * height) - marginY, 0, height - 1);
        var right = Math.Clamp((int)Math.Round(region.X1 * width) + marginX, left + 1, width);
        var bottom = Math.Clamp((int)Math.Round(region.Y1 * height) + marginY, top + 1, height);
        return (left, top, right, bottom);
    }

    private static void KeepSubtitleLikeComponents(bool[] mask, int width, int height)
    {
        var seen = new bool[mask.Length];
        var maxHeight = Math.Max(8, (int)Math.Round(height * 0.75));
        var maxWidth = Math.Max(16, (int)Math.Round(width * 0.92));
        var stack = new Stack<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start])
            {
                continue;
            }

            stack.Push(start);
            seen[start] = true;
            var pixels = new List<int>();
            var minX = width;
            var minY = height;
            var maxX = 0;
            var maxY = 0;
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                pixels.Add(current);
                var x = current % width;
                var y = current / width;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                TryPush(mask, seen, stack, x - 1, y, width, height);
                TryPush(mask, seen, stack, x + 1, y, width, height);
                TryPush(mask, seen, stack, x, y - 1, width, height);
                TryPush(mask, seen, stack, x, y + 1, width, height);
            }

            var componentWidth = maxX - minX + 1;
            var componentHeight = maxY - minY + 1;
            var keep = pixels.Count >= 8 &&
                       componentWidth >= 3 &&
                       componentHeight >= 3 &&
                       componentHeight <= maxHeight &&
                       componentWidth <= maxWidth;
            if (keep)
            {
                continue;
            }

            foreach (var pixel in pixels)
            {
                mask[pixel] = false;
            }
        }
    }

    private static void TryPush(bool[] mask, bool[] seen, Stack<int> stack, int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return;
        }

        var index = y * width + x;
        if (!mask[index] || seen[index])
        {
            return;
        }

        seen[index] = true;
        stack.Push(index);
    }

    private static Rgba32 MedianUnmasked(Image<Rgba32> roi, bool[] mask, int width, int height)
    {
        var reds = new List<byte>();
        var greens = new List<byte>();
        var blues = new List<byte>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (mask[y * width + x])
                {
                    continue;
                }

                var pixel = roi[x, y];
                reds.Add(pixel.R);
                greens.Add(pixel.G);
                blues.Add(pixel.B);
            }
        }

        if (reds.Count == 0)
        {
            return new Rgba32(24, 24, 24, 255);
        }

        reds.Sort();
        greens.Sort();
        blues.Sort();
        var middle = reds.Count / 2;
        return new Rgba32(reds[middle], greens[middle], blues[middle], 255);
    }

    private static int Percentile(byte[] values, int percentile)
    {
        var histogram = new int[256];
        foreach (var value in values)
        {
            histogram[value]++;
        }

        var target = Math.Max(1, (int)Math.Ceiling(values.Length * (percentile / 100d)));
        var seen = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            seen += histogram[value];
            if (seen >= target)
            {
                return value;
            }
        }

        return 255;
    }

    private static byte Luma(Rgba32 pixel) =>
        (byte)Math.Clamp((pixel.R * 54 + pixel.G * 183 + pixel.B * 19) / 256, 0, 255);
}
