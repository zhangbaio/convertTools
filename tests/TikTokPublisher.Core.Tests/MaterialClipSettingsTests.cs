using ChannelsPublisher.Clip;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public sealed class MaterialClipSettingsTests
{
    [Fact]
    public void LegacyImportMapsIndependentRangesAndServiceChoicesToEngine()
    {
        const string json = """
            {
              "material_clip_mode_enabled": {"highlight":true,"mashup":true,"commentary":true,"slice":true},
              "material_clip_force_regenerate_by_mode": {"slice":true},
              "material_clip_ranges_by_mode": {
                "highlight":[[35,45],[50,60]], "mashup":[[55,65]], "commentary":[[70,80],[90,100]], "slice":[[110,130]]
              },
              "material_clip_similarity_cap_by_mode": {"highlight":0,"commentary":0.25},
              "material_clip_video_bitrate_mbps":1.2,
              "material_clip_video_codec":"h265",
              "material_clip_pipeline_concurrent":5,
              "material_clip_publish_meta":false,
              "material_clip_originality_foreground_zoom":true,
              "material_clip_originality_sticker_dir":"builtin:stickers_v2",
              "material_clip_asr_provider":"doubao_subtitle",
              "material_clip_doubao_app_id":"doubao-id",
              "material_clip_doubao_access_token":"doubao-token",
              "material_clip_tts_engine":"edge",
              "material_clip_tts_edge_voice":"zh-CN-YunxiNeural",
              "ai_text_endpoint":"https://legacy.example/v1"
            }
            """;
        var client = new ClientSettings { AiTextEndpoint = "https://target.example/v1", AiTextApiKey = "target-key" };
        var settings = Import(json, client);
        var result = MaterialClipSettingsStore.BuildOptions(settings, client);

        Assert.Collection(result.ModeSettings!,
            mode => AssertMode(mode, "highlight", false, 0, new(35, 45), new(50, 60)),
            mode => AssertMode(mode, "mashup", false, 0.5, new ClipTimeRange(55, 65)),
            mode => AssertMode(mode, "commentary", false, 0.25, new(70, 80), new(90, 100)),
            mode => AssertMode(mode, "slice", true, 0.5, new ClipTimeRange(110, 130)));
        Assert.Equal("https://target.example/v1", result.AiEndpoint);
        Assert.Equal("target-key", result.AiApiKey);
        Assert.Equal("doubao_subtitle", result.AsrProvider);
        Assert.Equal("doubao-id", result.DoubaoAppId);
        Assert.Equal("doubao-token", result.DoubaoAccessToken);
        Assert.Equal("edge", result.TtsEngine);
        Assert.Equal("zh-CN-YunxiNeural", result.EdgeVoice);
        Assert.Equal("h265", result.VideoCodec);
        Assert.Equal(1.2, result.VideoBitrateMbps);
        Assert.Equal(5, result.PipelineConcurrency);
        Assert.False(result.PublishMetadata);
        Assert.False(result.StoryHighlights);
        Assert.True(result.OrigForegroundZoom);
        Assert.True(Directory.Exists(result.OrigStickerDir));
    }

    [Fact]
    public void LegacySingleModeAndSharedRangeUseTheSelectedMode()
    {
        var client = new ClientSettings();
        var settings = Import("""
            {"material_clip_edit_mode":"slice","material_clip_split_count":2,
             "material_clip_clip_min_sec":45,"material_clip_clip_max_sec":57}
            """, client);
        var options = MaterialClipSettingsStore.BuildOptions(settings, client);

        AssertMode(Assert.Single(options.ModeSettings!), "slice", false, 0.5, new(45, 57), new(45, 57));
    }

    [Fact]
    public void StoryHighlightSwitchReachesTheConfiguredEngine()
    {
        var settings = new MaterialClipSettings();
        settings.Clip.StoryHighlights = true;
        var saved = System.Text.Json.JsonSerializer.Serialize(settings);
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<MaterialClipSettings>(saved)!;

        Assert.True(MaterialClipSettingsStore.BuildOptions(reloaded, new ClientSettings()).StoryHighlights);
    }

    [Fact]
    public void SeparateVisionEndpointDoesNotReceiveTheTextServiceKey()
    {
        var settings = new MaterialClipSettings();
        settings.Clip.StoryHardSubtitles = true;
        settings.Clip.StoryVisualReview = true;
        settings.Services.StoryVisionEndpoint = "http://localhost:8000/v1";
        settings.Services.StoryVisionModel = "qwen3-vl";

        var options = MaterialClipSettingsStore.BuildOptions(settings, new ClientSettings { AiTextApiKey = "text-service-key" });

        Assert.True(options.StoryHardSubtitles);
        Assert.True(options.StoryVisualReview);
        Assert.Equal("http://localhost:8000/v1", options.StoryVisionEndpoint);
        Assert.Equal("qwen3-vl", options.StoryVisionModel);
        Assert.Empty(options.StoryVisionApiKey);
    }

    private static MaterialClipSettings Import(string json, ClientSettings client)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            return MaterialClipSettingsStore.ImportLegacy(path, client);
        }
        finally { File.Delete(path); }
    }

    private static void AssertMode(ClipModeOptions mode, string name, bool force, double cap, params ClipTimeRange[] ranges)
    {
        Assert.Equal(name, mode.Mode);
        Assert.Equal(force, mode.ForceRegenerate);
        Assert.Equal(cap, mode.SimilarityCap);
        Assert.Equal(ranges, mode.Ranges.ToArray());
    }
}
