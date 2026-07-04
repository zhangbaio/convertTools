namespace ShortDrama.Infrastructure.Files;

internal static class PosterCoverFrameSizeHelper
{
    private const int MinPixels = 3_686_400;
    private const int ApiSizeMultiple = 16;

    public static string ComputeApiSize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var currentPixels = (long)width * height;
        int newW;
        int newH;
        if (currentPixels >= MinPixels)
        {
            newW = width;
            newH = height;
        }
        else
        {
            var scale = Math.Ceiling(Math.Sqrt(MinPixels / (double)currentPixels) * 100) / 100.0;
            newW = (int)Math.Ceiling(width * scale);
            newH = (int)Math.Ceiling(height * scale);
        }

        newW = RoundUpToMultiple(newW, ApiSizeMultiple);
        newH = RoundUpToMultiple(newH, ApiSizeMultiple);
        while ((long)newW * newH < MinPixels)
        {
            newW = RoundUpToMultiple(newW + ApiSizeMultiple, ApiSizeMultiple);
            newH = RoundUpToMultiple((int)Math.Ceiling(newW * height / (double)width), ApiSizeMultiple);
        }

        return $"{newW}x{newH}";
    }

    public static string ResolveFrameApiSize(int width, int height, IReadOnlyDictionary<string, string> config)
    {
        var computed = ComputeApiSize(width, height);
        var provider = NormalizeImageProvider(config.GetValueOrDefault("ImageProvider"));
        if (provider == "doubao")
        {
            var configured = (config.GetValueOrDefault("ImageSize") ?? "").Trim();
            return string.IsNullOrWhiteSpace(configured) ? computed : configured;
        }

        return computed;
    }

    private static string NormalizeImageProvider(string? value)
    {
        var provider = (value ?? "doubao").Trim().ToLowerInvariant();
        return provider switch
        {
            "openai_image2" => "ofox_image2",
            "gemini" => "doubao",
            _ => provider is "ofox_image2" ? "ofox_image2" : "doubao",
        };
    }

    private static int RoundUpToMultiple(int value, int multiple) =>
        (int)Math.Ceiling(Math.Max(1, value) / (double)multiple) * multiple;
}
