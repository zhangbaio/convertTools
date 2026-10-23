using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

/// <summary>对齐 Python <c>poster_image_client_service.build_poster_runtime_config</c>。</summary>
public static class PosterImageConfigHelper
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> DoubaoSizeTable =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["2K"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["1:1"] = "2048x2048",
                ["3:4"] = "1776x2368",
                ["4:3"] = "2368x1776",
                ["16:9"] = "2816x1584",
                ["9:16"] = "1584x2816",
                ["2:3"] = "1664x2496",
                ["3:2"] = "2496x1664",
                ["21:9"] = "3136x1344",
            },
        };

    public static void ApplyPosterRuntimeConfig(IDictionary<string, object?> payload, ClientSettings settings)
    {
        var provider = NormalizeImageProvider(settings.ImageProvider);
        payload["ImageProvider"] = provider;

        SetIfEmpty(payload, "ImageEditModelId", settings.ImageModelId);
        SetIfEmpty(payload, "ImageEditApiKey", settings.ImageModelApiKey);
        SetIfEmpty(payload, "ImageEditEndpoint", settings.ImageModelEndpoint);

        if (!payload.TryGetValue("ImageEditPath", out var pathValue)
            || string.IsNullOrWhiteSpace(pathValue?.ToString()))
        {
            var endpoint = payload.TryGetValue("ImageEditEndpoint", out var endpointValue)
                ? endpointValue?.ToString()
                : null;
            endpoint = FirstNonEmpty(endpoint, settings.ImageModelEndpoint);
            payload["ImageEditPath"] = DefaultImageEditPath(endpoint);
        }

        if (provider == "ofox_image2")
        {
            payload["ImageEditModelId"] = settings.OfoxImage2ModelId;
            payload["ImageEditApiKey"] = settings.OfoxImage2ApiKey;
            payload["ImageEditEndpoint"] = string.IsNullOrWhiteSpace(settings.OfoxImage2Endpoint)
                ? "https://api.ofox.ai/v1"
                : settings.OfoxImage2Endpoint;
            payload["ImageEditPath"] = "/images/edits";
            payload["ImageQuality"] = string.IsNullOrWhiteSpace(settings.OfoxImage2Quality)
                ? "medium"
                : settings.OfoxImage2Quality;
            payload["ImageSize"] = string.IsNullOrWhiteSpace(settings.OfoxImage2Size)
                ? "auto"
                : settings.OfoxImage2Size;
            return;
        }

        payload["ImageQuality"] = NormalizeDoubaoImageResolution(settings.DoubaoImageResolution);
        payload["ImageSize"] = DoubaoImageSizeForRatio(
            settings.DoubaoImageResolution,
            settings.DoubaoImageRatio);
    }

    public static string NormalizeImageProvider(string? value)
    {
        var provider = (value ?? "doubao").Trim().ToLowerInvariant();
        return provider switch
        {
            "openai_image2" => "ofox_image2",
            "gemini" => "doubao",
            "ofox_image2" => "ofox_image2",
            _ => "doubao",
        };
    }

    public static bool IsOpenAiImageProvider(string? provider) =>
        NormalizeImageProvider(provider) == "ofox_image2";

    public static string NormalizeDoubaoImageResolution(string? value) => "2K";

    public static string NormalizeDoubaoImageRatio(string? value)
    {
        var text = (value ?? "3:4").Trim().ToLowerInvariant();
        if (text is "" or "smart" or "智能")
            return "3:4";
        if (text == "auto")
            return "auto";
        if (DoubaoSizeTable["2K"].ContainsKey(text))
            return text;
        return "3:4";
    }

    public static string DoubaoImageSizeForRatio(string? resolution, string? ratio)
    {
        var normalizedResolution = NormalizeDoubaoImageResolution(resolution);
        var normalizedRatio = NormalizeDoubaoImageRatio(ratio);
        if (string.Equals(normalizedRatio, "auto", StringComparison.OrdinalIgnoreCase))
            return normalizedResolution;

        if (DoubaoSizeTable.TryGetValue(normalizedResolution, out var table)
            && table.TryGetValue(normalizedRatio, out var size))
        {
            return ClampDoubaoPixelSize(size);
        }

        return ClampDoubaoPixelSize("1776x2368");
    }

    /// <summary>Seedream 5.0 Flash 允许的总像素上限是 2048×2048×1.1025。</summary>
    internal static string ClampDoubaoPixelSize(string size)
    {
        const long maxPixels = 4_624_220;
        var parts = size.Split('x');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var width) ||
            !int.TryParse(parts[1], out var height) ||
            width <= 0 || height <= 0)
        {
            return size;
        }

        var pixels = (long)width * height;
        if (pixels <= maxPixels)
            return size;

        var scale = Math.Sqrt(maxPixels / (double)pixels);
        var clampedWidth = Math.Max(1, (int)Math.Floor(width * scale));
        var clampedHeight = Math.Max(1, (int)Math.Floor(height * scale));
        while ((long)clampedWidth * clampedHeight > maxPixels)
        {
            if (clampedWidth >= clampedHeight && clampedWidth > 1)
                clampedWidth--;
            else if (clampedHeight > 1)
                clampedHeight--;
            else
                break;
        }

        return $"{clampedWidth}x{clampedHeight}";
    }

    public static string DefaultImageEditPath(string? endpoint)
    {
        var value = endpoint ?? "";
        return value.Contains("volces.com", StringComparison.OrdinalIgnoreCase)
            ? "/images/generations"
            : "/images/edits";
    }

    private static void SetIfEmpty(IDictionary<string, object?> payload, string key, string? value)
    {
        if (payload.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing?.ToString()))
            return;
        if (!string.IsNullOrWhiteSpace(value))
            payload[key] = value.Trim();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }
}
