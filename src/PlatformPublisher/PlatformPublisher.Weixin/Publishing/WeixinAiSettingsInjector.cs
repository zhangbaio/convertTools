using System.Text.Json.Nodes;
using PlatformPublisher.Common.Services;

namespace PlatformPublisher.Weixin.Publishing;

internal static class WeixinAiSettingsInjector
{
    public static void Apply(JsonObject videoPublish, IAiRuntimeSettingsProvider provider)
    {
        var settings = provider.Load();
        videoPublish["ai_description_timeout_seconds"] = Math.Clamp(settings.TimeoutSeconds, 5, 600);
        videoPublish["ai_text_endpoint"] = settings.Endpoint;
        videoPublish["ai_text_api_key"] = settings.ApiKey;
        videoPublish["ai_text_model"] = settings.Model;
        videoPublish["ai_description_asr_engine"] = string.IsNullOrWhiteSpace(settings.AsrLocalModelDirectory)
            ? "volcengine"
            : "local";
        videoPublish["ai_description_asr_language"] = string.IsNullOrWhiteSpace(settings.AsrLanguage)
            ? "zh-CN"
            : settings.AsrLanguage;
        videoPublish["ai_description_volcengine_app_id"] = settings.AsrAppId;
        videoPublish["ai_description_volcengine_access_token"] = settings.AsrAccessToken;
        videoPublish["ai_description_local_model_dir"] = settings.AsrLocalModelDirectory;
        videoPublish["ai_description_local_vad_path"] = settings.AsrVadPath;
        videoPublish["ai_description_fallback_to_original"] = true;
        videoPublish["ai_description_cache_enabled"] = true;
        videoPublish["ai_description_retry_attempts"] = 3;
    }
}
