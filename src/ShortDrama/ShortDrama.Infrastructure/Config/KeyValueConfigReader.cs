using System.Globalization;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Config;

internal static class KeyValueConfigReader
{
    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"未找到配置文件: {path}", path);
        }

        var content = File.ReadAllText(path);
        var trimmed = content.TrimStart();
        if (!trimmed.StartsWith('{'))
        {
            throw new InvalidDataException($"配置文件必须是 JSON 格式: {path}");
        }

        return ReadJsonMap(content);
    }

    private static IReadOnlyDictionary<string, string> ReadJsonMap(string content)
    {
        using var document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddTopLevelValues(document.RootElement, map);
        AddStructuredAliases(document.RootElement, map);
        return map;
    }

    private static void AddTopLevelValues(JsonElement root, IDictionary<string, string> map)
    {
        foreach (var property in root.EnumerateObject())
        {
            map[property.Name] = ToConfigString(property.Value);
        }
    }

    private static void AddStructuredAliases(JsonElement root, IDictionary<string, string> map)
    {
        CopySectionValue(root, map, "video", "res", "VideoRes");
        CopySectionValue(root, map, "video", "bitrateBps", "VideoBitrateBps");
        CopySectionValue(root, map, "video", "bitrateMode", "VideoBitrateMode");
        CopySectionValue(root, map, "video", "audioBitrateBps", "VideoAudioBitrateBps");
        CopySectionValue(root, map, "video", "fps", "VideoFps");
        CopySectionValue(root, map, "video", "concurrentCount", "VideoConcurrentCount");
        CopySectionValue(root, map, "video", "useHardwareEncoder", "VideoUseHardwareEncoder");
        CopySectionValue(root, map, "video", "nameTemplate", "VideoNameTemplate");

        CopySectionValue(root, map, "materialTranscode", "enabled", "MaterialConvertEnabled");
        CopySectionValue(root, map, "materialTranscode", "trimHeadSeconds", "MaterialTrimHeadSeconds");
        CopySectionValue(root, map, "materialTranscode", "trimTailSeconds", "MaterialTrimTailSeconds");
        CopySectionValue(root, map, "materialTranscode", "speedPercent", "MaterialSpeedPercent");
        CopySectionValue(root, map, "materialTranscode", "dynamicSpeedEnabled", "MaterialDynamicSpeedEnabled");
        CopySectionValue(root, map, "materialTranscode", "dynamicSpeedPresetName", "MaterialDynamicSpeedPresetName");
        CopySectionValue(root, map, "materialTranscode", "dynamicSpeedHeadSeconds", "MaterialDynamicSpeedHeadSeconds");
        CopySectionValue(root, map, "materialTranscode", "dynamicSpeedHeadPercent", "MaterialDynamicSpeedHeadPercent");
        CopySectionValue(root, map, "materialTranscode", "dynamicSpeedMiddlePercent", "MaterialDynamicSpeedMiddlePercent");
        CopySectionValue(root, map, "materialTranscode", "dynamicSpeedTailSeconds", "MaterialDynamicSpeedTailSeconds");
        CopySectionValue(root, map, "materialTranscode", "dynamicSpeedTailPercent", "MaterialDynamicSpeedTailPercent");
        CopySectionValue(root, map, "materialTranscode", "frameSamplingEnabled", "MaterialFrameSamplingEnabled");
        CopySectionValue(root, map, "materialTranscode", "frameSamplingMode", "MaterialFrameSamplingMode");
        CopySectionValue(root, map, "materialTranscode", "frameSamplingInterval", "MaterialFrameSamplingInterval");
        CopySectionValue(root, map, "materialTranscode", "dropEveryNFrames", "MaterialDropEveryNFrames");
        CopySectionValue(root, map, "materialTranscode", "dropCount", "MaterialDropCount");
        CopySectionValue(root, map, "materialTranscode", "cropWidthPercent", "MaterialCropWidthPercent");
        CopySectionValue(root, map, "materialTranscode", "cropHeightPercent", "MaterialCropHeightPercent");
        CopySectionValue(root, map, "materialTranscode", "foregroundZoomPercent", "MaterialForegroundZoomPercent");
        CopySectionValue(root, map, "materialTranscode", "dedupEnabled", "MaterialDedupEnabled");
        CopySectionValue(root, map, "materialTranscode", "dedupColorEnabled", "MaterialDedupColorEnabled");
        CopySectionValue(root, map, "materialTranscode", "dedupNoiseEnabled", "MaterialDedupNoiseEnabled");
        CopySectionValue(root, map, "materialTranscode", "dedupAudioEnabled", "MaterialDedupAudioEnabled");
        CopySectionValue(root, map, "materialTranscode", "dedupMetadataEnabled", "MaterialDedupMetadataEnabled");
        CopySectionValue(root, map, "materialTranscode", "dedupRotateEnabled", "MaterialDedupRotateEnabled");
        CopySectionValue(root, map, "materialTranscode", "dedupVignetteEnabled", "MaterialDedupVignetteEnabled");
        CopySectionValue(root, map, "materialTranscode", "dedupFadeInEnabled", "MaterialDedupFadeInEnabled");
        CopySectionValue(root, map, "materialTranscode", "watermarkEnabled", "MaterialWatermarkEnabled");
        CopySectionValue(root, map, "materialTranscode", "watermarkText", "MaterialWatermarkText");
        CopySectionValue(root, map, "materialTranscode", "watermarkFontSize", "MaterialWatermarkFontSize");
        CopySectionValue(root, map, "materialTranscode", "watermarkPosition", "MaterialWatermarkPosition");
        CopySectionValue(root, map, "materialTranscode", "watermarkMarginX", "MaterialWatermarkMarginX");
        CopySectionValue(root, map, "materialTranscode", "watermarkMarginY", "MaterialWatermarkMarginY");
        CopySectionValue(root, map, "materialTranscode", "outputWidth", "MaterialOutputWidth");
        CopySectionValue(root, map, "materialTranscode", "outputHeight", "MaterialOutputHeight");
        CopySectionValue(root, map, "materialTranscode", "pipWidthPercent", "MaterialPipWidthPercent");
        CopySectionValue(root, map, "materialTranscode", "pipHeightPercent", "MaterialPipHeightPercent");

        CopySectionValue(root, map, "uploadTranscode", "videoEncoder", "VideoEncoder");
        CopySectionValue(root, map, "uploadTranscode", "preset", "VideoPreset");
        CopySectionValue(root, map, "uploadTranscode", "targetVideoBitrateMbps", "UploadTargetVideoBitrateMbps");
        CopySectionValue(root, map, "uploadTranscode", "maxVideoBitrateMbps", "UploadMaxVideoBitrateMbps");
        CopySectionValue(root, map, "uploadTranscode", "minVideoBitrateMbps", "UploadMinVideoBitrateMbps");
        CopySectionValue(root, map, "uploadTranscode", "audioBitrateKbps", "UploadAudioBitrateKbps");
        CopySectionValue(root, map, "uploadTranscode", "bitrateFallbackEnabled", "UploadBitrateFallbackEnabled");
        CopySectionValue(root, map, "uploadTranscode", "bitrateFallbackVideoBitrateMbps", "UploadBitrateFallbackVideoBitrateMbps");
        CopyProfiles(root, map);

        CopySectionValue(root, map, "nvenc", "cq", "NvencCq");
        CopySectionValue(root, map, "nvenc", "maxParallel", "NvencMaxParallel");
    }

    private static void CopySectionValue(
        JsonElement root,
        IDictionary<string, string> map,
        string sectionName,
        string propertyName,
        string targetKey)
    {
        if (root.TryGetProperty(sectionName, out var section) &&
            section.ValueKind == JsonValueKind.Object &&
            section.TryGetProperty(propertyName, out var value))
        {
            map[targetKey] = ToConfigString(value);
        }
    }

    private static void CopyProfiles(JsonElement root, IDictionary<string, string> map)
    {
        if (!root.TryGetProperty("uploadTranscode", out var section) ||
            section.ValueKind != JsonValueKind.Object ||
            !section.TryGetProperty("profiles", out var profiles) ||
            profiles.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        map["UploadBitrateProfilesJson"] = JsonSerializer.Serialize(new
        {
            profiles = JsonSerializer.Deserialize<object[]>(profiles.GetRawText()) ?? []
        });
    }

    private static string ToConfigString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            JsonValueKind.Array or JsonValueKind.Object => value.GetRawText(),
            _ => string.Empty
        };
    }

    public static string SerializeJson(IDictionary<string, object?> values)
    {
        return JsonSerializer.Serialize(values, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    public static object? NormalizeValue(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (bool.TryParse(value, out var boolValue))
        {
            return boolValue;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            return intValue;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return doubleValue;
        }

        return value;
    }
}
