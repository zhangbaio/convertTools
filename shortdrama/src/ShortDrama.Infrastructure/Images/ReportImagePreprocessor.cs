using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;
using System.Collections.Generic;

namespace ShortDrama.Infrastructure.Images;

internal static class ReportImagePreprocessor
{
    public static string PrepareSealImage(string sourcePath)
    {
        var outputPath = CreateTemporaryOutputPath("seal");
        using var image = Image.Load<Rgba32>(sourcePath);
        var transparentKey = SampleBackgroundKeyColor(image);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];
                    if (pixel.A == 0)
                    {
                        continue;
                    }

                    if (ShouldTreatAsTransparent(pixel, transparentKey))
                    {
                        pixel = new Rgba32(255, 255, 255, 0);
                        continue;
                    }

                    var brightness = (pixel.R + pixel.G + pixel.B) / 3.0;
                    var redGreen = pixel.R - pixel.G;
                    var redBlue = pixel.R - pixel.B;

                    var isSealInk =
                        redGreen >= 22 &&
                        redBlue >= 12 &&
                        brightness <= 238;

                    if (!isSealInk)
                    {
                        pixel = new Rgba32(255, 255, 255, 0);
                        continue;
                    }

                    // Preserve the stamp ink while softening overlap with text slightly.
                    var alpha = (byte)Math.Clamp((int)Math.Round(pixel.A * 0.72), 0, 255);
                    pixel = new Rgba32(pixel.R, pixel.G, pixel.B, alpha);
                }
            }
        });

        image.Save(outputPath, new PngEncoder
        {
            ColorType = PngColorType.RgbWithAlpha
        });
        return outputPath;
    }

    private static Rgba32 SampleBackgroundKeyColor(Image<Rgba32> image)
    {
        var points = new[]
        {
            new Point(0, 0),
            new Point(image.Width - 1, 0),
            new Point(0, image.Height - 1),
            new Point(image.Width - 1, image.Height - 1),
            new Point(Math.Max(0, image.Width / 2), 0),
            new Point(Math.Max(0, image.Width / 2), image.Height - 1)
        };

        var r = 0;
        var g = 0;
        var b = 0;
        var count = 0;

        image.ProcessPixelRows(accessor =>
        {
            foreach (var point in points)
            {
                ref var pixel = ref accessor.GetRowSpan(point.Y)[point.X];
                r += pixel.R;
                g += pixel.G;
                b += pixel.B;
                count++;
            }
        });

        return count == 0
            ? new Rgba32(255, 255, 255, 255)
            : new Rgba32((byte)(r / count), (byte)(g / count), (byte)(b / count), 255);
    }

    private static bool ShouldTreatAsTransparent(Rgba32 pixel, Rgba32 transparentKey)
    {
        var distance =
            Math.Abs(pixel.R - transparentKey.R) +
            Math.Abs(pixel.G - transparentKey.G) +
            Math.Abs(pixel.B - transparentKey.B);

        var brightness = (pixel.R + pixel.G + pixel.B) / 3.0;
        var lowSaturation =
            Math.Abs(pixel.R - pixel.G) <= 18 &&
            Math.Abs(pixel.R - pixel.B) <= 18 &&
            Math.Abs(pixel.G - pixel.B) <= 18;

        return distance <= 70 || (brightness >= 235 && lowSaturation);
    }

    public static string PrepareSignImage(string sourcePath)
    {
        var outputPath = CreateTemporaryOutputPath("sign");
        using var image = Image.Load<Rgba32>(sourcePath);
        var transparentKey = SampleBackgroundKeyColor(image);

        var width = image.Width;
        var height = image.Height;
        var mask = new bool[width, height];

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];
                    if (pixel.A == 0 || ShouldTreatAsTransparent(pixel, transparentKey))
                    {
                        mask[x, y] = false;
                        continue;
                    }

                    var brightness = (pixel.R + pixel.G + pixel.B) / 3.0;
                    var contrast =
                        Math.Abs(pixel.R - transparentKey.R) +
                        Math.Abs(pixel.G - transparentKey.G) +
                        Math.Abs(pixel.B - transparentKey.B);

                    // Keep dark signature ink and discard light background/anti-aliasing noise.
                    mask[x, y] = brightness < 205 && contrast >= 35;
                }
            }
        });

        var retainedPixels = CountRetainedPixels(mask, width, height);
        if (retainedPixels < Math.Max(80, width * height / 500))
        {
            SaveSourceAsTransparentPng(image, transparentKey, outputPath);
            return outputPath;
        }

        RemoveSmallComponents(mask, width, height, minComponentSize: 18);

        retainedPixels = CountRetainedPixels(mask, width, height);
        if (retainedPixels < Math.Max(80, width * height / 500))
        {
            SaveSourceAsTransparentPng(image, transparentKey, outputPath);
            return outputPath;
        }

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];

                    if (!mask[x, y])
                    {
                        pixel = new Rgba32(255, 255, 255, 0);
                        continue;
                    }

                    pixel.R = 0;
                    pixel.G = 0;
                    pixel.B = 0;
                    pixel.A = 242;
                }
            }
        });

        image.Save(outputPath, new PngEncoder
        {
            ColorType = PngColorType.RgbWithAlpha
        });
        return outputPath;
    }

    private static void SaveSourceAsTransparentPng(Image<Rgba32> image, Rgba32 transparentKey, string outputPath)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];
                    if (pixel.A == 0)
                    {
                        continue;
                    }

                    if (ShouldTreatAsTransparent(pixel, transparentKey))
                    {
                        pixel = new Rgba32(255, 255, 255, 0);
                    }
                }
            }
        });

        image.Save(outputPath, new PngEncoder
        {
            ColorType = PngColorType.RgbWithAlpha
        });
    }

    private static string CreateTemporaryOutputPath(string prefix)
    {
        return Path.Combine(
            Path.GetTempPath(),
            $"shortdrama-{prefix}-{Guid.NewGuid():N}.prepared.png");
    }

    private static void RemoveSmallComponents(bool[,] mask, int width, int height, int minComponentSize)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!mask[x, y])
                {
                    continue;
                }

                var component = CollectComponent(mask, width, height, x, y);
                if (component.Count < minComponentSize)
                {
                    foreach (var point in component)
                    {
                        mask[point.X, point.Y] = false;
                    }
                }
                else
                {
                    foreach (var point in component)
                    {
                        mask[point.X, point.Y] = true;
                    }
                }
            }
        }
    }

    private static List<Point> CollectComponent(bool[,] mask, int width, int height, int startX, int startY)
    {
        var result = new List<Point>();
        var queue = new Queue<Point>();
        queue.Enqueue(new Point(startX, startY));
        mask[startX, startY] = false;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            result.Add(current);

            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    var nx = current.X + dx;
                    var ny = current.Y + dy;

                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    {
                        continue;
                    }

                    if (!mask[nx, ny])
                    {
                        continue;
                    }

                    mask[nx, ny] = false;
                    queue.Enqueue(new Point(nx, ny));
                }
            }
        }

        return result;
    }

    private static int CountRetainedPixels(bool[,] mask, int width, int height)
    {
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (mask[x, y])
                {
                    count++;
                }
            }
        }

        return count;
    }
}
