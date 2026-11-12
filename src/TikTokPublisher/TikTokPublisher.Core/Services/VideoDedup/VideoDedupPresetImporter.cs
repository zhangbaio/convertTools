using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services.VideoDedup;

public sealed record VideoDedupPresetImport(string Name, VideoDedupPreset Preset);

public static class VideoDedupPresetImporter
{
    public static string DefaultProcessorDatabasePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WH.ShortDramaProcessor.Desktop",
            "global.db");

    public static IReadOnlyList<VideoDedupPresetImport> ReadProcessorDatabase(string? databasePath = null)
    {
        var path = string.IsNullOrWhiteSpace(databasePath) ? DefaultProcessorDatabasePath : databasePath;
        if (!File.Exists(path))
            throw new FileNotFoundException("未找到短剧处理预设库。", path);

        var results = new List<VideoDedupPresetImport>();
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, json FROM video_presets ORDER BY name";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var json = reader.GetString(1);
            var preset = FromProcessorJson(json);
            if (string.IsNullOrWhiteSpace(preset.Name))
                preset.Name = name;
            preset.Normalize();
            results.Add(new VideoDedupPresetImport(name, preset));
        }

        return results;
    }

    public static VideoDedupPreset FromProcessorJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var preset = VideoDedupPreset.CreateDefault();
        preset.Name = ReadString(root, "name") ?? preset.Name;
        var encoding = ReadString(root, "encodingPreference");
        if (!string.IsNullOrWhiteSpace(encoding))
            preset.EncodingPreference = encoding;
        if (TryReadDouble(root, "parallel", out var parallel))
            preset.Parallel = (int)Math.Round(parallel);
        preset.MinimumDurationSeconds = ReadOptionalSeconds(root, "minimumDurationSeconds");
        preset.MaximumDurationSeconds = ReadOptionalSeconds(root, "maximumDurationSeconds");
        ReadTransitions(root, preset);
        ReadEffects(root, preset);
        if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in steps.EnumerateArray())
                ReadStep(step, preset);
        }

        preset.Normalize();
        return preset;
    }

    private static void ReadTransitions(JsonElement root, VideoDedupPreset preset)
    {
        if (!root.TryGetProperty("transitions", out var transitions) ||
            transitions.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (transitions.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in transitions.EnumerateArray())
            {
                var name = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : ReadString(item, "type") ?? ReadString(item, "kind");
                ApplyTransitionName(preset, name);
            }

            preset.TransitionsEnabled = preset.HasAnyTransition;
            return;
        }

        if (transitions.ValueKind != JsonValueKind.Object)
            return;

        if (transitions.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in candidates.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    ApplyTransitionName(preset, item.GetString());
            }
        }

        preset.TransitionsEnabled = ReadBool(transitions, "enabled") ?? preset.HasAnyTransition;
        preset.TransitionFadeBlack = ReadBool(transitions, "fadeBlack") ?? ReadBool(transitions, "fadeToBlack") ?? preset.TransitionFadeBlack;
        preset.TransitionDissolve = ReadBool(transitions, "dissolve") ?? ReadBool(transitions, "crossDissolve") ?? preset.TransitionDissolve;
        preset.TransitionPush = ReadBool(transitions, "push") ?? preset.TransitionPush;
        preset.TransitionSlide = ReadBool(transitions, "slide") ?? preset.TransitionSlide;
        preset.TransitionZoom = ReadBool(transitions, "zoom") ?? preset.TransitionZoom;
        if (!preset.TransitionsEnabled && preset.HasAnyTransition && ReadBool(transitions, "enabled") is null)
            preset.TransitionsEnabled = true;
    }

    private static void ApplyTransitionName(VideoDedupPreset preset, string? name)
    {
        switch ((name ?? "").Trim().ToLowerInvariant())
        {
            case "fadeblack":
            case "fade_black":
            case "fadetoblack":
                preset.TransitionFadeBlack = true;
                break;
            case "fade":
            case "dissolve":
            case "crossdissolve":
                preset.TransitionDissolve = true;
                break;
            case "push":
            case "smoothleft":
                preset.TransitionPush = true;
                break;
            case "slide":
            case "slideleft":
                preset.TransitionSlide = true;
                break;
            case "zoom":
            case "zoomin":
                preset.TransitionZoom = true;
                break;
        }
    }

    private static void ReadEffects(JsonElement root, VideoDedupPreset preset)
    {
        if (!root.TryGetProperty("effects", out var effects) || effects.ValueKind != JsonValueKind.Object)
            return;
        ReadPicture(effects, preset);
        var target = preset.Effects;
        if (TryEffect(effects, "basic", "exposure", out var exposure, out var amount))
            target.Exposure = ScaledValue(exposure, amount, value => Math.Abs(value) <= 5 ? value * 50 : value);
        if (TryEffect(effects, "basic", "gamma", out var gamma, out amount))
            target.Gamma = ScaledValue(gamma, amount, value => (value - 1) * 200);
        if (TryEffect(effects, "basic", "temperature", out var temperature, out amount))
            target.Temperature = ScaledValue(temperature, amount, value => value);
        if (TryEffect(effects, "basic", "tint", out var tint, out amount))
            target.Tint = ScaledValue(tint, amount, value => value);
        if (TryEffect(effects, "basic", "vibrance", out var vibrance, out amount))
            target.Vibrance = ScaledValue(vibrance, amount, value => value);

        if (TryEffect(effects, "style", "curves", out var curves, out amount))
        {
            target.CurveAmount = amount;
            target.CurveMaster = ReadCurve(curves, "master");
            target.CurveRed = ReadCurve(curves, "red");
            target.CurveGreen = ReadCurve(curves, "green");
            target.CurveBlue = ReadCurve(curves, "blue");
        }

        if (TryEffect(effects, "style", "levels", out var levels, out amount))
        {
            target.LevelsAmount = amount;
            if (TryReadDouble(levels, "inputBlack", out var value)) target.InputBlack = value;
            if (TryReadDouble(levels, "inputWhite", out value)) target.InputWhite = value;
            if (TryReadDouble(levels, "midtone", out value)) target.LevelsGamma = value;
            if (TryReadDouble(levels, "outputBlack", out value)) target.OutputBlack = value;
            if (TryReadDouble(levels, "outputWhite", out value)) target.OutputWhite = value;
        }

        if (TryEffect(effects, "style", "splitTone", out var split, out amount))
        {
            target.SplitToneAmount = amount;
            target.ShadowColor = ReadString(split, "shadowColor") ?? target.ShadowColor;
            target.HighlightColor = ReadString(split, "highlightColor") ?? target.HighlightColor;
            if (TryReadDouble(split, "balance", out var balance)) target.SplitToneBalance = balance;
        }

        if (TryEffect(effects, "style", "lut", out var lut, out amount))
        {
            target.LutAmount = amount;
            target.LutPath = ReadString(lut, "path") ?? "";
        }

        if (TryEffect(effects, "spatial", "blur", out var blur, out amount) && TryReadDouble(blur, "value", out var sigma))
            target.Blur = sigma * amount / 100d;
        if (TryEffect(effects, "spatial", "vignette", out var vignette, out amount))
        {
            target.VignetteAmount = amount;
            if (TryReadDouble(vignette, "radius", out var radius)) target.VignetteRadius = radius;
            if (TryReadDouble(vignette, "feather", out var feather)) target.VignetteFeather = feather;
        }

        if (TryEffect(effects, "spatial", "grain", out var grain, out amount))
        {
            target.GrainAmount = amount;
            if (TryReadDouble(grain, "size", out var size)) target.GrainSize = size <= 1 ? size * 100 : size;
            if (TryReadDouble(grain, "density", out var density)) target.GrainDensity = density;
        }

        if (TryEffect(effects, "spatial", "mosaic", out var mosaic, out amount) &&
            mosaic.TryGetProperty("regions", out var regions) && regions.ValueKind == JsonValueKind.Array)
        {
            target.MosaicAmount = amount;
            foreach (var region in regions.EnumerateArray())
            {
                if (!TryReadDouble(region, "x", out var x) || !TryReadDouble(region, "y", out var y) ||
                    !TryReadDouble(region, "width", out var w) || !TryReadDouble(region, "height", out var h))
                    continue;
                var fraction = x <= 1 && y <= 1 && w <= 1 && h <= 1;
                var scale = fraction ? 100 : 1;
                var block = TryReadDouble(region, "blockSize", out var blockSize) || TryReadDouble(region, "block", out blockSize)
                    ? (int)Math.Round(blockSize)
                    : 16;
                target.MosaicRegions.Add(new VideoDedupMosaicRegion
                {
                    X = x * scale,
                    Y = y * scale,
                    Width = w * scale,
                    Height = h * scale,
                    Block = block,
                });
            }
        }
    }

    private static bool TryEffect(JsonElement effects, string group, string name, out JsonElement effect, out double amount)
    {
        effect = default;
        amount = 0;
        if (!effects.TryGetProperty(group, out var section) || section.ValueKind != JsonValueKind.Object ||
            !section.TryGetProperty(name, out effect) || effect.ValueKind != JsonValueKind.Object ||
            ReadBool(effect, "enabled") != true)
        {
            return false;
        }

        amount = TryReadDouble(effect, "amount", out var value) ? Math.Clamp(value, 0, 100) : 100;
        return amount > 0.01;
    }

    private static double ScaledValue(JsonElement effect, double amount, Func<double, double> convert) =>
        TryReadDouble(effect, "value", out var value) ? convert(value) * amount / 100d : 0;

    private static List<VideoDedupCurvePoint> ReadCurve(JsonElement curves, string channel)
    {
        if (!curves.TryGetProperty(channel, out var points) || points.ValueKind != JsonValueKind.Array)
            return VideoDedupEffects.IdentityCurve();
        var result = new List<VideoDedupCurvePoint>();
        foreach (var point in points.EnumerateArray())
        {
            if (TryReadDouble(point, "x", out var x) && TryReadDouble(point, "y", out var y))
                result.Add(new VideoDedupCurvePoint { X = x, Y = y });
        }

        return result.Count == 0 ? VideoDedupEffects.IdentityCurve() : result;
    }

    private static void ReadStep(JsonElement step, VideoDedupPreset preset)
    {
        var type = (ReadString(step, "type") ?? "").Trim();
        ReadPicture(step, preset);
        if (TryReadDouble(step, "trimStartSeconds", out var trimStart))
            preset.TrimStartSeconds = trimStart;
        else if (TryReadDouble(step, "trimStartMs", out var trimStartMs))
            preset.TrimStartSeconds = trimStartMs / 1000d;
        if (TryReadDouble(step, "trimEndSeconds", out var trimEnd))
            preset.TrimEndSeconds = trimEnd;
        else if (TryReadDouble(step, "trimEndMs", out var trimEndMs))
            preset.TrimEndSeconds = trimEndMs / 1000d;
        if (TryReadDouble(step, "topPercent", out var top)) preset.CropTopPercent = top;
        if (TryReadDouble(step, "bottomPercent", out var bottom)) preset.CropBottomPercent = bottom;
        if (TryReadDouble(step, "leftPercent", out var left)) preset.CropLeftPercent = left;
        if (TryReadDouble(step, "rightPercent", out var right)) preset.CropRightPercent = right;
        if (TryReadDouble(step, "width", out var width) && width > 0) preset.PortraitWidth = (int)Math.Round(width);
        if (TryReadDouble(step, "height", out var height) && height > 0) preset.PortraitHeight = (int)Math.Round(height);
        if (TryReadDouble(step, "landscapeWidth", out var landscapeWidth) && landscapeWidth > 0)
            preset.LandscapeWidth = (int)Math.Round(landscapeWidth);
        if (TryReadDouble(step, "landscapeHeight", out var landscapeHeight) && landscapeHeight > 0)
            preset.LandscapeHeight = (int)Math.Round(landscapeHeight);
        if (TryReadDouble(step, "factor", out var factor))
            preset.SpeedFactor = NormalizeSpeed(factor);
        if (TryReadDouble(step, "frameDropPercent", out var drop))
            preset.FrameDropPercent = drop;
        if (TryReadDouble(step, "fps", out var fps) && fps > 0)
            preset.Fps = (int)Math.Round(fps);
        if (TryReadDouble(step, "lowGainDb", out var low)) preset.AudioLowGainDb = low;
        if (TryReadDouble(step, "midGainDb", out var mid)) preset.AudioMidGainDb = mid;
        if (TryReadDouble(step, "highGainDb", out var high)) preset.AudioHighGainDb = high;

        var codec = ReadString(step, "codec");
        if (string.Equals(type, "videoEncode", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(codec))
                preset.VideoCodec = codec.Trim().ToLowerInvariant();
            var mode = ReadString(step, "bitrateMode");
            if (!string.IsNullOrWhiteSpace(mode))
                preset.BitrateMode = NormalizeBitrateMode(mode);
            if (TryReadDouble(step, "bitrateKbps", out var videoKbps) && videoKbps > 0)
                preset.VideoBitrateMbps = videoKbps / 1000d;
            if (TryReadDouble(step, "quality", out var quality) && quality > 0)
                preset.Crf = (int)Math.Round(quality);
        }
        else if (string.Equals(type, "audioEncode", StringComparison.OrdinalIgnoreCase))
        {
            preset.AudioCodec = string.IsNullOrWhiteSpace(codec) ? VideoDedupPreset.AudioAac : codec.Trim().ToLowerInvariant();
            if (TryReadDouble(step, "bitrateKbps", out var audioKbps) && audioKbps > 0)
                preset.AudioBitrateKbps = (int)Math.Round(audioKbps);
        }
    }

    private static void ReadPicture(JsonElement element, VideoDedupPreset preset)
    {
        if (TryReadDouble(element, "brightness", out var brightness)) preset.Brightness = brightness;
        if (TryReadDouble(element, "contrast", out var contrast)) preset.Contrast = contrast;
        if (TryReadDouble(element, "saturation", out var saturation)) preset.Saturation = saturation;
        if (TryReadDouble(element, "sharpness", out var sharpness)) preset.Sharpness = sharpness;
        var grayscale = ReadBool(element, "grayscale");
        if (grayscale is not null)
            preset.Grayscale = grayscale.Value;
    }

    private static double NormalizeSpeed(double factor)
    {
        if (factor is >= 0.25 and <= 4)
            return factor;
        return 1 + factor / 100d;
    }

    private static string NormalizeBitrateMode(string mode)
    {
        var text = mode.Trim();
        if (text.Equals("cbr", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("constant", StringComparison.OrdinalIgnoreCase))
            return VideoDedupPreset.BitrateCbr;
        if (text.Equals("crf", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("quality", StringComparison.OrdinalIgnoreCase))
            return VideoDedupPreset.BitrateCrf;
        return VideoDedupPreset.BitrateVbr;
    }

    private static double ReadOptionalSeconds(JsonElement element, string name) =>
        TryReadDouble(element, name, out var value) && value > 0 ? value : 0;

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return null;
        var text = property.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static bool? ReadBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool TryReadDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return false;
        if (property.ValueKind == JsonValueKind.Number)
            return property.TryGetDouble(out value);
        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
