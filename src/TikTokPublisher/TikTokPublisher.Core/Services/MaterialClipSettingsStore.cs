using System.Text.Json;
using ChannelsPublisher.Clip;
using ChannelsPublisher.Core.Config;
using ShortDrama.Infrastructure;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.Asr;

namespace TikTokPublisher.Core.Services;

public sealed class MaterialClipSettings
{
    public ClipConfig Clip { get; set; } = new();
    public MaterialClipConnections Services { get; set; } = new();
}

public sealed class MaterialClipConnections
{
    public string AiEndpoint { get; set; } = "";
    public string AiApiKey { get; set; } = "";
    public string AiModel { get; set; } = "";
    public string AsrEngine { get; set; } = "volcengine";
    public string AsrProvider { get; set; } = "volcengine_stt";
    public string AsrLanguage { get; set; } = "";
    public string VolcAppId { get; set; } = "";
    public string VolcAccessToken { get; set; } = "";
    public string DoubaoAppId { get; set; } = "";
    public string DoubaoAccessToken { get; set; } = "";
    public string LocalModel { get; set; } = "paraformer";
    public string LocalModelDir { get; set; } = "";
    public string LocalVadPath { get; set; } = "";
    public bool LocalUseItn { get; set; }
    public double HybridMinCharsPerSec { get; set; } = 1.0;
    public string TtsAppId { get; set; } = "";
    public string TtsAccessToken { get; set; } = "";
    public string EdgeTtsExecutable { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
}

public static class MaterialClipSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object Gate = new();

    public static string LegacyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weixin_channel_tool", "settings.json");

    private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "material-clip-settings.json");

    public static MaterialClipSettings Load()
    {
        lock (Gate)
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<MaterialClipSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                    ?? throw new InvalidDataException("全局剪辑配置为空。");

            if (!File.Exists(LegacyPath))
                return new MaterialClipSettings();

            var imported = ImportLegacy(LegacyPath, ClientSettingsStore.Load());
            Save(imported);
            return imported;
        }
    }

    public static void Save(MaterialClipSettings settings)
    {
        lock (Gate)
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    public static MaterialClipSettings ImportLegacy(string path, ClientSettings? existing = null)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject()
                .Any(p => p.Name.StartsWith("material_clip_", StringComparison.Ordinal)))
            throw new InvalidDataException("所选 JSON 没有旧工具的素材剪辑配置。");

        string Read(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? "" : "";
        var targetHasAi = !string.IsNullOrWhiteSpace(existing?.AiTextApiKey);
        var targetHasVolc = !string.IsNullOrWhiteSpace(existing?.TiktokAsrAppId) &&
            !string.IsNullOrWhiteSpace(existing?.TiktokAsrAccessToken);
        return new MaterialClipSettings
        {
            Clip = ClipConfig.FromLegacyJson(root),
            Services = new MaterialClipConnections
            {
                AiEndpoint = targetHasAi ? "" : Read("ai_text_endpoint"),
                AiApiKey = targetHasAi ? "" : Read("ai_text_api_key"),
                AiModel = targetHasAi ? "" : Read("ai_text_model"),
                AsrEngine = First(Read("material_clip_asr_engine"), "volcengine"),
                AsrProvider = First(Read("material_clip_asr_provider"), "volcengine_stt"),
                AsrLanguage = Read("material_clip_asr_language"),
                VolcAppId = targetHasVolc ? "" : Read("material_clip_volcengine_app_id"),
                VolcAccessToken = targetHasVolc ? "" : Read("material_clip_volcengine_access_token"),
                DoubaoAppId = Read("material_clip_doubao_app_id"),
                DoubaoAccessToken = Read("material_clip_doubao_access_token"),
                LocalModel = First(Read("material_clip_asr_local_model"), "sensevoice"),
                LocalModelDir = Read("material_clip_asr_local_model_dir"),
                LocalVadPath = Read("material_clip_asr_local_vad_path"),
                LocalUseItn = root.TryGetProperty("material_clip_asr_local_use_itn", out var itn) &&
                    itn.ValueKind == JsonValueKind.True,
                HybridMinCharsPerSec = root.TryGetProperty("material_clip_asr_hybrid_min_chars_per_sec", out var threshold) &&
                    threshold.ValueKind == JsonValueKind.Number && threshold.TryGetDouble(out var density) ? density : 1.0,
                TtsAppId = Read("material_clip_tts_app_id"),
                TtsAccessToken = Read("material_clip_tts_access_token"),
            },
        };
    }

    public static ClipEngineOptions BuildOptions(MaterialClipSettings settings, ClientSettings client)
    {
        var clip = settings.Clip;
        var services = settings.Services;
        var needsLocalAsr = services.AsrEngine is "local" or "hybrid";
        var useParaformer = services.LocalModel == "paraformer";
        var models = needsLocalAsr && useParaformer && string.IsNullOrWhiteSpace(services.LocalModelDir)
            ? SherpaOnnxModelResolver.TryResolve(client)
            : null;
        var senseVoiceDir = needsLocalAsr && !useParaformer && string.IsNullOrWhiteSpace(services.LocalModelDir) ? FindSenseVoice() : "";
        var width = string.Equals(clip.OutputQuality, "1080p", StringComparison.OrdinalIgnoreCase) ? 1080 : 720;
        var modes = new[] { "highlight", "mashup", "commentary", "slice" }
            .Select(clip.Mode).Where(mode => mode.Enabled)
            .Select(mode => new ClipModeOptions(
                mode.Key,
                clip.RangesFor(mode.Key).Take(10).Select(range =>
                {
                    var min = Math.Clamp(range.MinSeconds, 1, 1800);
                    return new ClipTimeRange(min, Math.Clamp(range.MaxSeconds, min, 1800));
                }).ToArray(),
                clip.SimilarityCapByMode.TryGetValue(mode.Key, out var cap) ? Math.Clamp(cap, 0, 1) : 0.5,
                mode.Force))
            .ToArray();

        return new ClipEngineOptions
        {
            ModeSettings = modes,
            Width = width,
            Height = width * 16 / 9,
            EpisodeQuota = clip.EpisodeQuota,
            AudioEnergy = clip.AudioEnergy,
            ShotDensity = clip.ShotDensity,
            EnableLlmScore = clip.EnableLlmScore,
            ArrangeNarrative = clip.LlmArrange,
            SmoothSelection = clip.SmoothSelection,
            MinimumSegmentMs = Math.Clamp(clip.MinimumSegmentMs, 100, 10000),
            PublishMetadata = clip.PublishMeta,
            TitleCard = clip.TitleCard,
            TitleCardSeconds = Math.Clamp(clip.TitleCardSeconds, 1, 15),
            VideoCodec = clip.VideoCodec.Trim().ToLowerInvariant().Replace(".", "") is "h265" or "hevc" or "x265" ? "h265" : "h264",
            EncodeMode = clip.EncodeMode.Trim().ToLowerInvariant() is "quality" or "crf" or "cq" or "vbr" ? "quality" : "bitrate",
            VideoBitrateMbps = Math.Clamp(clip.VideoBitrate, 0.8, 12),
            RenderSpeed = clip.RenderSpeed.Trim().ToLowerInvariant(),
            HardwareEncode = clip.HardwareEncode,
            PipelineConcurrency = Math.Clamp(clip.PipelineConcurrent, 1, 16),
            NetworkConcurrency = Math.Clamp(clip.NetConcurrent, 0, 32),
            AiEndpoint = First(services.AiEndpoint, client.AiTextEndpoint),
            AiApiKey = First(services.AiApiKey, client.AiTextApiKey),
            AiModel = First(services.AiModel, client.AiTextModel),
            AsrEngine = services.AsrEngine,
            AsrProvider = services.AsrProvider,
            AsrLanguage = First(services.AsrLanguage, client.TiktokAsrLanguage, "zh-CN"),
            VolcAppId = First(services.VolcAppId, client.TiktokAsrAppId),
            VolcAccessToken = First(services.VolcAccessToken, client.TiktokAsrAccessToken),
            DoubaoAppId = services.DoubaoAppId,
            DoubaoAccessToken = services.DoubaoAccessToken,
            LocalModel = services.LocalModel,
            LocalModelDir = First(services.LocalModelDir, models is null ? "" : Path.GetDirectoryName(models.ModelPath), senseVoiceDir),
            LocalVadPath = First(services.LocalVadPath, models?.VadPath, client.TiktokAsrLocalVadPath),
            LocalUseItn = services.LocalUseItn,
            HybridMinCharsPerSec = Math.Clamp(services.HybridMinCharsPerSec, 0, 20),
            TtsEngine = clip.TtsEngine,
            TtsAppId = services.TtsAppId,
            TtsAccessToken = services.TtsAccessToken,
            TtsVoiceType = clip.TtsVoiceType,
            TtsCluster = clip.TtsCluster,
            TtsSpeedRatio = Math.Clamp(clip.TtsSpeedRatio, 0.5, 2),
            EdgeVoice = First(clip.TtsEdgeVoice, "zh-CN-YunjianNeural"),
            EdgeTtsExecutable = First(services.EdgeTtsExecutable, BundledToolResolver.TryResolveBinary("edge-tts")),
            CommentaryNarrationRatio = Math.Clamp(clip.CommentaryNarrationRatio, 40, 100),
            CommentaryStyleStrength = clip.CommentaryStyleStrength,
            BurnSubtitles = clip.CommentaryBurnSubtitles,
            SubtitleBlurHeight = Math.Clamp(clip.CommentarySubtitleBlurHeight, 0.05, 0.35),
            OrigEnabled = clip.OrigEnabled,
            OrigZoom = clip.OrigZoom,
            OrigColor = clip.OrigColor,
            OrigSpeed = clip.OrigSpeed,
            OrigFade = clip.OrigFade,
            OrigForegroundZoom = clip.OrigForegroundZoom,
            OrigBlurBackground = clip.OrigBlurBackground,
            OrigStickerStrip = clip.OrigStickerStrip,
            OrigNoise = clip.OrigNoise,
            OrigStickerDir = ResolveStickerDirectory(clip.OrigStickerDir),
            FfmpegPath = First(services.FfmpegPath, MediaBinaryResolver.ResolveFfmpeg()),
            FfprobePath = First(services.FfprobePath, MediaBinaryResolver.ResolveFfprobe()),
        };
    }

    private static string FindSenseVoice()
    {
        const string model = "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17";
        return BundledToolResolver.EnumerateSearchRoots()
            .SelectMany(root => new[]
            {
                Path.Combine(root, "models", model),
                Path.Combine(root, "weixin-channel-tool", "models", model),
            })
            .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "tokens.txt")) &&
                (File.Exists(Path.Combine(dir, "model.int8.onnx")) || File.Exists(Path.Combine(dir, "model.onnx")))) ?? "";
    }

    private static string ResolveStickerDirectory(string value) => value switch
    {
        "builtin:stickers" => Path.Combine(AppContext.BaseDirectory, "Assets", "MaterialClip", "stickers"),
        "builtin:stickers_v2" => Path.Combine(AppContext.BaseDirectory, "Assets", "MaterialClip", "stickers_v2"),
        _ => Environment.ExpandEnvironmentVariables(value),
    };

    private static string First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}
