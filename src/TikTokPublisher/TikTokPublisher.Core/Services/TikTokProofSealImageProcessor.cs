using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace TikTokPublisher.Core.Services;

internal sealed record TikTokProofSealImagePayload(
    byte[] Bytes,
    string Extension,
    bool BackgroundWasMadeTransparent);

internal static class TikTokProofSealImageProcessor
{
    private const byte TransparentAlphaThreshold = 16;
    private const byte ForegroundAlphaThreshold = 32;
    private const double BorderBackgroundDistance = 24d;
    private const double MinimumBorderBackgroundConfidence = 0.65d;
    private const double MinimumBackgroundLuminance = 210d;
    private const int MaximumBackgroundChannelRange = 30;
    private static readonly IReadOnlySet<string> RasterExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".gif",
            ".bmp",
            ".tif",
            ".tiff",
        };

    public static TikTokProofSealImagePayload Prepare(string sourcePath, double? targetAspectRatio = null)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        var sourceBytes = File.ReadAllBytes(fullPath);
        if (!RasterExtensions.Contains(extension))
        {
            return new TikTokProofSealImagePayload(sourceBytes, extension, false);
        }

        try
        {
            var detectedFormat = Image.DetectFormat(sourceBytes);
            var detectedExtension = ResolveDetectedRasterExtension(detectedFormat, fullPath);
            using var image = Image.Load<Rgba32>(sourceBytes);
            if (image.Frames.Count != 1)
            {
                throw new InvalidDataException(
                    "印章图片包含多个画面，无法确定应使用哪一帧；请转换为单张透明 PNG 后重试。");
            }

            var backgroundWasMadeTransparent = false;
            if (HasMeaningfulTransparency(image) && !NeedsAspectPadding(image, targetAspectRatio))
            {
                return new TikTokProofSealImagePayload(sourceBytes, detectedExtension, false);
            }

            if (!HasMeaningfulTransparency(image))
            {
            var backgroundSample = SampleDominantBorderColor(image);
            if (!CanSafelyRemoveBackground(backgroundSample))
            {
                throw new InvalidDataException(
                    "印章图片没有透明背景，且边缘不是均匀的浅色背景，无法安全自动去底；请提供白底印章图或透明 PNG。");
            }

            var background = NormalizeBackgroundColor(backgroundSample.Color);
            var noiseCutoff = ResolveBorderNoiseCutoff(image, background);
            var result = MakeBackgroundTransparent(image, background, noiseCutoff);
            var pixelCount = (long)image.Width * image.Height;
            var minimumForegroundPixels = Math.Max(32L, pixelCount / 1000L);
            var minimumTransparentPixels = Math.Max(32L, pixelCount / 200L);
            if (result.ForegroundPixels < minimumForegroundPixels ||
                result.TransparentPixels < minimumTransparentPixels ||
                result.TransparentBorderPixels < Math.Max(4L, result.BorderPixels / 20L))
            {
                throw new InvalidDataException(
                    "印章图片没有透明背景，且自动处理后未识别到清晰的印章前景和透明背景；请提供白底印章图或透明 PNG。");
            }

                backgroundWasMadeTransparent = true;
            }

            using var paddedImage = PadToAspectRatio(image, targetAspectRatio);
            using var output = new MemoryStream();
            (paddedImage ?? image).Save(output, new PngEncoder
            {
                ColorType = PngColorType.RgbWithAlpha,
            });
            return new TikTokProofSealImagePayload(
                output.ToArray(),
                ".png",
                backgroundWasMadeTransparent);
        }
        catch (UnknownImageFormatException ex)
        {
            throw new InvalidDataException($"无法识别印章图片格式：{fullPath}。", ex);
        }
        catch (InvalidImageContentException ex)
        {
            throw new InvalidDataException($"印章图片内容已损坏：{fullPath}。", ex);
        }
    }

    private static bool NeedsAspectPadding(Image<Rgba32> image, double? targetAspectRatio)
    {
        if (targetAspectRatio is not > 0d ||
            double.IsNaN(targetAspectRatio.Value) ||
            double.IsInfinity(targetAspectRatio.Value))
        {
            return false;
        }

        var sourceAspectRatio = image.Width / (double)image.Height;
        return Math.Abs(sourceAspectRatio - targetAspectRatio.Value) > 0.0001d;
    }

    private static Image<Rgba32>? PadToAspectRatio(
        Image<Rgba32> image,
        double? targetAspectRatio)
    {
        if (!NeedsAspectPadding(image, targetAspectRatio))
            return null;

        var target = targetAspectRatio!.Value;
        var source = image.Width / (double)image.Height;
        var canvasWidth = image.Width;
        var canvasHeight = image.Height;
        if (source < target)
            canvasWidth = Math.Max(image.Width, (int)Math.Ceiling(image.Height * target));
        else
            canvasHeight = Math.Max(image.Height, (int)Math.Ceiling(image.Width / target));

        var canvas = new Image<Rgba32>(canvasWidth, canvasHeight, new Rgba32(0, 0, 0, 0));
        var offset = new Point(
            (canvasWidth - image.Width) / 2,
            (canvasHeight - image.Height) / 2);
        canvas.Mutate(context => context.DrawImage(image, offset, 1f));
        return canvas;
    }

    private static string ResolveDetectedRasterExtension(IImageFormat format, string fullPath) =>
        format.DefaultMimeType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tiff",
            _ => throw new InvalidDataException(
                $"印章图片的实际格式 {format.Name} 不受支持：{fullPath}。请转换为 PNG 或 JPG 后重试。"),
        };

    private static bool HasMeaningfulTransparency(Image<Rgba32> image)
    {
        long transparentPixels = 0;
        long transparentBorderPixels = 0;
        long foregroundPixels = 0;
        long borderPixels = 0;
        var borderWidth = ResolveBorderWidth(image);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    if (pixel.A <= TransparentAlphaThreshold)
                    {
                        transparentPixels++;
                    }
                    else if (pixel.A >= ForegroundAlphaThreshold)
                    {
                        foregroundPixels++;
                    }

                    if (!IsBorderPixel(x, y, row.Length, accessor.Height, borderWidth))
                    {
                        continue;
                    }

                    borderPixels++;
                    if (pixel.A <= TransparentAlphaThreshold)
                    {
                        transparentBorderPixels++;
                    }
                }
            }
        });

        var pixelCount = (long)image.Width * image.Height;
        return transparentPixels >= Math.Max(32L, pixelCount / 200L)
            && transparentBorderPixels >= Math.Max(4L, borderPixels / 20L)
            && foregroundPixels >= Math.Max(32L, pixelCount / 1000L);
    }

    private static BorderBackgroundSample SampleDominantBorderColor(Image<Rgba32> image)
    {
        var borderWidth = ResolveBorderWidth(image);
        var buckets = new Dictionary<int, (long Count, long Red, long Green, long Blue)>();

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (!IsBorderPixel(x, y, row.Length, accessor.Height, borderWidth))
                    {
                        continue;
                    }

                    var pixel = row[x];
                    if (pixel.A <= TransparentAlphaThreshold)
                    {
                        continue;
                    }

                    var bucketKey = ((pixel.R >> 4) << 8) | ((pixel.G >> 4) << 4) | (pixel.B >> 4);
                    var bucket = buckets.GetValueOrDefault(bucketKey);
                    buckets[bucketKey] = (
                        bucket.Count + 1,
                        bucket.Red + pixel.R,
                        bucket.Green + pixel.G,
                        bucket.Blue + pixel.B);
                }
            }
        });

        if (buckets.Count == 0)
        {
            return new BorderBackgroundSample(new Rgba32(255, 255, 255, 255), 0d);
        }

        var dominant = buckets.Values.MaxBy(bucket => bucket.Count);
        var color = new Rgba32(
            (byte)(dominant.Red / dominant.Count),
            (byte)(dominant.Green / dominant.Count),
            (byte)(dominant.Blue / dominant.Count),
            255);
        long sampleCount = 0;
        long matchingCount = 0;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (!IsBorderPixel(x, y, row.Length, accessor.Height, borderWidth) ||
                        row[x].A <= TransparentAlphaThreshold)
                    {
                        continue;
                    }

                    sampleCount++;
                    if (ColorDistance(row[x], color) <= BorderBackgroundDistance)
                    {
                        matchingCount++;
                    }
                }
            }
        });

        return new BorderBackgroundSample(
            color,
            sampleCount == 0 ? 0d : matchingCount / (double)sampleCount);
    }

    private static bool CanSafelyRemoveBackground(BorderBackgroundSample sample)
    {
        var color = sample.Color;
        var luminance = 0.2126d * color.R + 0.7152d * color.G + 0.0722d * color.B;
        var channelRange = Math.Max(color.R, Math.Max(color.G, color.B))
            - Math.Min(color.R, Math.Min(color.G, color.B));
        return sample.Confidence >= MinimumBorderBackgroundConfidence
            && luminance >= MinimumBackgroundLuminance
            && channelRange <= MaximumBackgroundChannelRange;
    }

    private static Rgba32 NormalizeBackgroundColor(Rgba32 color)
    {
        var minimumChannel = Math.Min(color.R, Math.Min(color.G, color.B));
        var maximumChannel = Math.Max(color.R, Math.Max(color.G, color.B));
        return minimumChannel >= 238 && maximumChannel - minimumChannel <= 12
            ? new Rgba32(255, 255, 255, 255)
            : color;
    }

    private static double ResolveBorderNoiseCutoff(Image<Rgba32> image, Rgba32 background)
    {
        var borderWidth = ResolveBorderWidth(image);
        var samples = new List<double>();
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (IsBorderPixel(x, y, row.Length, accessor.Height, borderWidth))
                    {
                        samples.Add(ResolveColorToAlpha(row[x], background));
                    }
                }
            }
        });

        if (samples.Count == 0)
        {
            return 3d / 255d;
        }

        samples.Sort();
        var percentileIndex = Math.Clamp(
            (int)Math.Ceiling((samples.Count - 1) * 0.995d),
            0,
            samples.Count - 1);
        return Math.Clamp(samples[percentileIndex] + 2d / 255d, 3d / 255d, 0.08d);
    }

    private static TransparencyResult MakeBackgroundTransparent(
        Image<Rgba32> image,
        Rgba32 background,
        double noiseCutoff)
    {
        long foregroundPixels = 0;
        long transparentPixels = 0;
        long transparentBorderPixels = 0;
        long borderPixels = 0;
        var borderWidth = ResolveBorderWidth(image);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];
                    var sourceAlpha = pixel.A / 255d;
                    var colorAlpha = ResolveColorToAlpha(pixel, background);
                    var isBorder = IsBorderPixel(x, y, row.Length, accessor.Height, borderWidth);
                    if (isBorder)
                    {
                        borderPixels++;
                    }

                    if (colorAlpha <= noiseCutoff || sourceAlpha <= TransparentAlphaThreshold / 255d)
                    {
                        pixel = new Rgba32(0, 0, 0, 0);
                        transparentPixels++;
                        if (isBorder)
                        {
                            transparentBorderPixels++;
                        }

                        continue;
                    }

                    var outputAlpha = sourceAlpha * colorAlpha;
                    pixel = new Rgba32(
                        RemoveMatte(pixel.R, background.R, colorAlpha),
                        RemoveMatte(pixel.G, background.G, colorAlpha),
                        RemoveMatte(pixel.B, background.B, colorAlpha),
                        (byte)Math.Clamp((int)Math.Round(outputAlpha * 255d), 0, 255));
                    if (pixel.A >= ForegroundAlphaThreshold)
                    {
                        foregroundPixels++;
                    }
                    else if (pixel.A <= TransparentAlphaThreshold)
                    {
                        transparentPixels++;
                        if (isBorder)
                        {
                            transparentBorderPixels++;
                        }
                    }
                }
            }
        });

        return new TransparencyResult(
            foregroundPixels,
            transparentPixels,
            transparentBorderPixels,
            borderPixels);
    }

    private static double ResolveColorToAlpha(Rgba32 color, Rgba32 background) =>
        Math.Max(
            ResolveChannelAlpha(color.R, background.R),
            Math.Max(
                ResolveChannelAlpha(color.G, background.G),
                ResolveChannelAlpha(color.B, background.B)));

    private static double ResolveChannelAlpha(byte composite, byte background)
    {
        if (composite < background)
        {
            return background == 0 ? 0d : (background - composite) / (double)background;
        }

        if (composite > background)
        {
            return background == 255 ? 0d : (composite - background) / (255d - background);
        }

        return 0d;
    }

    private static byte RemoveMatte(byte composite, byte background, double opacity)
    {
        if (opacity <= 0d)
        {
            return 0;
        }

        var foreground = (composite - (1d - opacity) * background) / opacity;
        return (byte)Math.Clamp((int)Math.Round(foreground), 0, 255);
    }

    private static double ColorDistance(Rgba32 first, Rgba32 second)
    {
        var red = first.R - second.R;
        var green = first.G - second.G;
        var blue = first.B - second.B;
        return Math.Sqrt(red * red + green * green + blue * blue);
    }

    private static int ResolveBorderWidth(Image<Rgba32> image) =>
        Math.Clamp(Math.Min(image.Width, image.Height) / 50, 1, 16);

    private static bool IsBorderPixel(int x, int y, int width, int height, int borderWidth) =>
        x < borderWidth || x >= width - borderWidth || y < borderWidth || y >= height - borderWidth;

    private sealed record BorderBackgroundSample(Rgba32 Color, double Confidence);

    private sealed record TransparencyResult(
        long ForegroundPixels,
        long TransparentPixels,
        long TransparentBorderPixels,
        long BorderPixels);
}
