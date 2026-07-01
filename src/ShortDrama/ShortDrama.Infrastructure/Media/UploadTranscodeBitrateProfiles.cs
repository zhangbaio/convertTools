using System.Text.Json;

namespace ShortDrama.Infrastructure.Media;

public sealed record UploadTranscodeBitrateProfile(
    string Name,
    int MinShortEdge,
    int MaxShortEdge,
    double BitrateMbps,
    int AudioKbps,
    bool Enabled,
    string VideoEncoder,
    string Preset);

public static class UploadTranscodeBitrateProfiles
{
    public static IReadOnlyList<UploadTranscodeBitrateProfile> DefaultProfiles { get; } =
    [
        new("720p及以下", 1, 959, 4.8d, 128, true, "auto", "veryfast"),
        new("1080p", 960, 1439, 6.0d, 128, true, "auto", "veryfast"),
        new("2k+", 1440, 0, 7.0d, 160, true, "auto", "fast")
    ];

    public static IReadOnlyList<UploadTranscodeBitrateProfile> Parse(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return DefaultProfiles.Select(Clone).ToArray();
        }

        try
        {
            using var document = JsonDocument.Parse(rawValue);
            if (!document.RootElement.TryGetProperty("profiles", out var profilesElement) ||
                profilesElement.ValueKind != JsonValueKind.Array)
            {
                return DefaultProfiles.Select(Clone).ToArray();
            }

            var profiles = new List<UploadTranscodeBitrateProfile>();
            var index = 0;
            foreach (var item in profilesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                index++;
                var bitrateValue = GetDouble(item, "bitrate_mbps") ?? GetDouble(item, "target_mbps") ?? 4.8d;
                profiles.Add(new UploadTranscodeBitrateProfile(
                    Name: GetString(item, "name") ?? $"档位{index}",
                    MinShortEdge: Math.Max(1, GetInt(item, "min_short_edge") ?? 1),
                    MaxShortEdge: Math.Max(0, GetInt(item, "max_short_edge") ?? 0),
                    BitrateMbps: Math.Max(1.0d, bitrateValue),
                    AudioKbps: Math.Max(64, GetInt(item, "audio_kbps") ?? 128),
                    Enabled: GetBool(item, "enabled") ?? true,
                    VideoEncoder: NormalizeEncoder(GetString(item, "video_encoder") ?? "auto"),
                    Preset: NormalizePreset(GetString(item, "preset") ?? "veryfast")));
            }

            return profiles.Count == 0
                ? DefaultProfiles.Select(Clone).ToArray()
                : profiles.ToArray();
        }
        catch
        {
            return DefaultProfiles.Select(Clone).ToArray();
        }
    }

    public static string Serialize(IEnumerable<UploadTranscodeBitrateProfile> profiles)
    {
        var payload = new
        {
            profiles = profiles.Select(item => new
            {
                name = item.Name,
                min_short_edge = item.MinShortEdge,
                max_short_edge = item.MaxShortEdge,
                bitrate_mbps = Math.Round(item.BitrateMbps, 3),
                audio_kbps = item.AudioKbps,
                video_encoder = NormalizeEncoder(item.VideoEncoder),
                preset = NormalizePreset(item.Preset),
                enabled = item.Enabled
            }).ToArray()
        };

        return JsonSerializer.Serialize(payload);
    }

    public static UploadTranscodeBitrateProfile Select(IReadOnlyList<UploadTranscodeBitrateProfile> profiles, int shortEdge)
    {
        var selected = profiles.FirstOrDefault(item =>
            item.Enabled &&
            shortEdge >= item.MinShortEdge &&
            (item.MaxShortEdge <= 0 || shortEdge <= item.MaxShortEdge));

        return selected ?? DefaultProfiles.First(item =>
            shortEdge >= item.MinShortEdge &&
            (item.MaxShortEdge <= 0 || shortEdge <= item.MaxShortEdge));
    }

    public static UploadTranscodeBitrateProfile Clone(UploadTranscodeBitrateProfile source)
    {
        return source with { };
    }

    private static string NormalizeEncoder(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "auto" or "libx264" or "h264_nvenc" or "h264_videotoolbox" => normalized,
            _ => "auto"
        };
    }

    private static string NormalizePreset(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "veryfast" : value.Trim();
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)
            ? number
            : null;
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out number)
            ? number
            : null;
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }
}
