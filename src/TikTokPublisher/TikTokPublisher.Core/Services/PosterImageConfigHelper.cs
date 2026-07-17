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
                ["3:4"] = "1728x2304",
                ["4:3"] = "2304x1728",
                ["16:9"] = "2560x1440",
                ["9:16"] = "1440x2560",
                ["2:3"] = "1664x2496",
                ["3:2"] = "2496x1664",
                ["21:9"] = "3024x1296",
            },
            ["4K"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["1:1"] = "4096x4096",
                ["3:4"] = "3520x4688",
                ["4:3"] = "4688x3520",
                ["16:9"] = "5408x3040",
                ["9:16"] = "3040x5408",
                ["2:3"] = "3328x4992",
                ["3:2"] = "4992x3328",
                ["21:9"] = "6192x2656",
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

    public static string NormalizeDoubaoImageResolution(string? value)
    {
        var text = (value ?? "2K").Trim().ToUpperInvariant();
        return text is "2K" or "4K" ? text : "2K";
    }

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
            return size;
        }

        return "1728x2304";
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
