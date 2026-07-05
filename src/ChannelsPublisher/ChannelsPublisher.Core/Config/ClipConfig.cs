using System.Text.Json;
using ChannelsPublisher.Core.Services;

namespace ChannelsPublisher.Core.Config;

/// <summary>One material-clip mode and its output count.</summary>
public sealed class ClipModeSetting
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool Force { get; set; }
    public int Count { get; set; } = 2;
}

public sealed class ClipDurationRange
{
    public int MinSeconds { get; set; } = 300;
    public int MaxSeconds { get; set; } = 360;
}

/// <summary>Global material-clip configuration migrated from the Python tool.</summary>
public sealed class ClipConfig
{
    public List<ClipModeSetting> Modes { get; set; } = new()
    {
        new ClipModeSetting { Key = "highlight", Label = "高光", Enabled = true, Count = 2 },
        new ClipModeSetting { Key = "mashup", Label = "混剪", Enabled = false, Count = 2 },
        new ClipModeSetting { Key = "slice", Label = "切片", Enabled = false, Count = 2 },
        new ClipModeSetting { Key = "commentary", Label = "解说", Enabled = false, Count = 1 },
    };

    public Dictionary<string, List<ClipDurationRange>> RangesByMode { get; set; } = new()
    {
        ["highlight"] = [new ClipDurationRange(), new ClipDurationRange()],
        ["mashup"] = [new ClipDurationRange(), new ClipDurationRange()],
        ["slice"] = [new ClipDurationRange(), new ClipDurationRange()],
        ["commentary"] = [new ClipDurationRange()],
    };

    public Dictionary<string, double> SimilarityCapByMode { get; set; } = new()
    {
        ["highlight"] = 0.5,
        ["mashup"] = 0.5,
        ["slice"] = 0.5,
        ["commentary"] = 0.5,
    };

    public bool EpisodeQuota { get; set; } = true;
    public bool EnableLlmScore { get; set; }
    public bool AudioEnergy { get; set; } = true;
    public bool ShotDensity { get; set; } = true;
    public bool LlmArrange { get; set; } = true;
    public bool SmoothSelection { get; set; } = true;
    public bool PublishMeta { get; set; } = true;

    public int PipelineConcurrent { get; set; } = 4;
    public int NetConcurrent { get; set; }
    public string OutputQuality { get; set; } = "720p";
    public double VideoBitrate { get; set; } = 2.5;
    public string EncodeMode { get; set; } = "bitrate";
    public string VideoCodec { get; set; } = "h264";
    public string RenderSpeed { get; set; } = "fast";
    public bool HardwareEncode { get; set; } = true;

    public bool TitleCard { get; set; } = true;
    public int TitleCardSeconds { get; set; } = 4;

    public bool CommentaryBurnSubtitles { get; set; } = true;
    public string CommentaryStyleStrength { get; set; } = "standard";
    public double CommentaryNarrationRatio { get; set; } = 70.0;
    public string TtsEngine { get; set; } = "volcengine";
    public string TtsVoiceType { get; set; } = string.Empty;
    public string TtsCluster { get; set; } = string.Empty;
    public double TtsSpeedRatio { get; set; } = 1.0;
    public string TtsEdgeVoice { get; set; } = "zh-CN-YunjianNeural";

    public bool OrigEnabled { get; set; }
    public bool OrigZoom { get; set; } = true;
    public bool OrigColor { get; set; }
    public bool OrigSpeed { get; set; }
    public bool OrigFade { get; set; }
    public string OrigStickerDir { get; set; } = string.Empty;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static string FilePath => Path.Combine(AppPaths.DataRoot, "clip-config.json");

    public static ClipConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return Normalize(JsonSerializer.Deserialize<ClipConfig>(File.ReadAllText(FilePath), Options) ?? new ClipConfig());
            }
        }
        catch
        {
        }

        return Normalize(ImportLegacySettings(new ClipConfig()));
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        Normalize(this);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }

    public ClipModeSetting Mode(string key)
    {
        var mode = Modes.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        if (mode is not null)
        {
            return mode;
        }

        mode = new ClipModeSetting
        {
            Key = key,
            Label = key switch
            {
                "highlight" => "高光",
                "mashup" => "混剪",
                "slice" => "切片",
                "commentary" => "解说",
                _ => key
            },
            Enabled = string.Equals(key, "highlight", StringComparison.OrdinalIgnoreCase),
            Count = string.Equals(key, "commentary", StringComparison.OrdinalIgnoreCase) ? 1 : 2
        };
        Modes.Add(mode);
        return mode;
    }

    public IReadOnlyList<ClipDurationRange> RangesFor(string key)
    {
        if (!RangesByMode.TryGetValue(key, out var ranges) || ranges.Count == 0)
        {
            ranges = Enumerable.Range(0, Math.Max(1, Mode(key).Count))
                .Select(_ => new ClipDurationRange())
                .ToList();
            RangesByMode[key] = ranges;
        }

        return ranges;
    }

    public ClipDurationRange PrimaryRange()
    {
        var mode = Modes.FirstOrDefault(item => item.Enabled) ?? Mode("highlight");
        return RangesFor(mode.Key).FirstOrDefault() ?? new ClipDurationRange();
    }

    private static ClipConfig Normalize(ClipConfig config)
    {
        foreach (var key in new[] { "highlight", "mashup", "slice", "commentary" })
        {
            var mode = config.Mode(key);
            mode.Count = Math.Clamp(mode.Count <= 0 ? (key == "commentary" ? 1 : 2) : mode.Count, 1, 10);
            if (!config.RangesByMode.TryGetValue(key, out var ranges) || ranges.Count == 0)
            {
                ranges = Enumerable.Range(0, mode.Count)
                    .Select(_ => new ClipDurationRange())
                    .ToList();
                config.RangesByMode[key] = ranges;
            }

            foreach (var range in ranges)
            {
                range.MinSeconds = Math.Max(30, range.MinSeconds);
                range.MaxSeconds = Math.Max(range.MinSeconds, range.MaxSeconds);
            }

            if (!config.SimilarityCapByMode.ContainsKey(key))
            {
                config.SimilarityCapByMode[key] = 0.5;
            }
        }

        config.PipelineConcurrent = Math.Clamp(config.PipelineConcurrent <= 0 ? 4 : config.PipelineConcurrent, 1, 8);
        config.NetConcurrent = Math.Clamp(config.NetConcurrent, 0, 16);
        config.OutputQuality = NormalizeChoice(config.OutputQuality, "720p", "720p", "1080p", "720P", "1080P").ToLowerInvariant();
        config.VideoBitrate = Math.Clamp(config.VideoBitrate <= 0 ? 2.5 : config.VideoBitrate, 0.8, 12.0);
        config.EncodeMode = NormalizeChoice(config.EncodeMode, "bitrate", "bitrate", "quality");
        config.VideoCodec = NormalizeChoice(config.VideoCodec, "h264", "h264", "h265");
        config.RenderSpeed = NormalizeChoice(config.RenderSpeed, "fast", "fast", "balanced", "quality");
        config.TitleCardSeconds = Math.Clamp(config.TitleCardSeconds <= 0 ? 4 : config.TitleCardSeconds, 1, 15);
        config.CommentaryStyleStrength = NormalizeChoice(config.CommentaryStyleStrength, "standard", "subtle", "standard", "strong");
        config.CommentaryNarrationRatio = Math.Clamp(config.CommentaryNarrationRatio <= 0 ? 70.0 : config.CommentaryNarrationRatio, 40.0, 100.0);
        config.TtsEngine = NormalizeChoice(config.TtsEngine, "volcengine", "volcengine", "edge");
        config.TtsSpeedRatio = Math.Clamp(config.TtsSpeedRatio <= 0 ? 1.0 : config.TtsSpeedRatio, 0.5, 2.0);
        return config;
    }

    private static string NormalizeChoice(string? value, string fallback, params string[] choices)
    {
        var normalized = (value ?? string.Empty).Trim();
        return choices.FirstOrDefault(choice => string.Equals(choice, normalized, StringComparison.OrdinalIgnoreCase)) ?? fallback;
    }

    private static ClipConfig ImportLegacySettings(ClipConfig config)
    {
        var legacyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".weixin_channel_tool",
            "settings.json");
        if (!File.Exists(legacyPath))
        {
            return config;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(legacyPath));
            var root = document.RootElement;
            config.EpisodeQuota = GetBool(root, "material_clip_episode_length_quota", config.EpisodeQuota);
            config.EnableLlmScore = GetBool(root, "material_clip_enable_llm", config.EnableLlmScore);
            config.AudioEnergy = GetBool(root, "material_clip_audio_energy", config.AudioEnergy);
            config.ShotDensity = GetBool(root, "material_clip_shot_density", config.ShotDensity);
            config.LlmArrange = GetBool(root, "material_clip_llm_arrange", config.LlmArrange);
            config.SmoothSelection = GetBool(root, "material_clip_smooth_selection", config.SmoothSelection);
            config.PublishMeta = GetBool(root, "material_clip_publish_meta", config.PublishMeta);
            config.PipelineConcurrent = GetInt(root, "material_clip_pipeline_concurrent", config.PipelineConcurrent);
            config.NetConcurrent = GetInt(root, "material_clip_net_concurrent", config.NetConcurrent);
            config.OutputQuality = GetString(root, "material_clip_output_quality", config.OutputQuality);
            config.VideoBitrate = GetDouble(root, "material_clip_video_bitrate_mbps", config.VideoBitrate);
            config.EncodeMode = GetString(root, "material_clip_encode_mode", config.EncodeMode);
            config.VideoCodec = GetString(root, "material_clip_video_codec", config.VideoCodec);
            config.RenderSpeed = GetString(root, "material_clip_render_speed", config.RenderSpeed);
            config.HardwareEncode = GetBool(root, "material_clip_hardware_encode", config.HardwareEncode);
            config.TitleCard = GetBool(root, "material_clip_title_card", config.TitleCard);
            config.TitleCardSeconds = GetInt(root, "material_clip_title_card_seconds", config.TitleCardSeconds);
            config.CommentaryBurnSubtitles = GetBool(root, "material_clip_commentary_burn_subtitles", config.CommentaryBurnSubtitles);
            config.CommentaryStyleStrength = GetString(root, "material_clip_commentary_style_strength", config.CommentaryStyleStrength);
            config.CommentaryNarrationRatio = GetDouble(root, "material_clip_commentary_narration_ratio", config.CommentaryNarrationRatio);
            config.TtsEngine = GetString(root, "material_clip_tts_engine", config.TtsEngine);
            config.TtsVoiceType = GetString(root, "material_clip_tts_voice_type", config.TtsVoiceType);
            config.TtsCluster = GetString(root, "material_clip_tts_cluster", config.TtsCluster);
            config.TtsSpeedRatio = GetDouble(root, "material_clip_tts_speed_ratio", config.TtsSpeedRatio);
            config.TtsEdgeVoice = GetString(root, "material_clip_tts_edge_voice", config.TtsEdgeVoice);
            config.OrigEnabled = GetBool(root, "material_clip_originality_enabled", config.OrigEnabled);
            config.OrigZoom = GetBool(root, "material_clip_originality_zoom", config.OrigZoom);
            config.OrigColor = GetBool(root, "material_clip_originality_color", config.OrigColor);
            config.OrigSpeed = GetBool(root, "material_clip_originality_speed", config.OrigSpeed);
            config.OrigFade = GetBool(root, "material_clip_originality_fade", config.OrigFade);
            config.OrigStickerDir = GetString(root, "material_clip_originality_sticker_dir", config.OrigStickerDir);
            ImportModeSettings(config, root);
        }
        catch
        {
        }

        return config;
    }

    private static void ImportModeSettings(ClipConfig config, JsonElement root)
    {
        if (root.TryGetProperty("material_clip_mode_enabled", out var enabledMap) &&
            enabledMap.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in enabledMap.EnumerateObject())
            {
                config.Mode(property.Name).Enabled = ReadBool(property.Value, config.Mode(property.Name).Enabled);
            }
        }

        if (root.TryGetProperty("material_clip_force_regenerate_by_mode", out var forceMap) &&
            forceMap.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in forceMap.EnumerateObject())
            {
                config.Mode(property.Name).Force = ReadBool(property.Value, config.Mode(property.Name).Force);
            }
        }

        if (root.TryGetProperty("material_clip_similarity_cap_by_mode", out var capMap) &&
            capMap.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in capMap.EnumerateObject())
            {
                config.SimilarityCapByMode[property.Name] = ReadDouble(property.Value, 0.5);
            }
        }

        if (root.TryGetProperty("material_clip_ranges_by_mode", out var rangeMap) &&
            rangeMap.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in rangeMap.EnumerateObject())
            {
                var ranges = ReadRanges(property.Value);
                if (ranges.Count > 0)
                {
                    config.RangesByMode[property.Name] = ranges;
                    config.Mode(property.Name).Count = ranges.Count;
                }
            }
        }
    }

    private static List<ClipDurationRange> ReadRanges(JsonElement element)
    {
        var result = new List<ClipDurationRange>();
        if (element.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 2)
            {
                continue;
            }

            var min = ReadInt(item[0], 300);
            var max = Math.Max(min, ReadInt(item[1], 360));
            result.Add(new ClipDurationRange { MinSeconds = min, MaxSeconds = max });
        }

        return result;
    }

    private static string GetString(JsonElement root, string key, string fallback) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int GetInt(JsonElement root, string key, int fallback) =>
        root.TryGetProperty(key, out var value) ? ReadInt(value, fallback) : fallback;

    private static double GetDouble(JsonElement root, string key, double fallback) =>
        root.TryGetProperty(key, out var value) ? ReadDouble(value, fallback) : fallback;

    private static bool GetBool(JsonElement root, string key, bool fallback) =>
        root.TryGetProperty(key, out var value) ? ReadBool(value, fallback) : fallback;

    private static int ReadInt(JsonElement value, int fallback)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        return int.TryParse(value.GetString(), out parsed) ? parsed : fallback;
    }

    private static double ReadDouble(JsonElement value, double fallback)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed))
        {
            return parsed;
        }

        return double.TryParse(value.GetString(), out parsed) ? parsed : fallback;
    }

    private static bool ReadBool(JsonElement value, bool fallback)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        return bool.TryParse(value.GetString(), out var parsed) ? parsed : fallback;
    }
}
