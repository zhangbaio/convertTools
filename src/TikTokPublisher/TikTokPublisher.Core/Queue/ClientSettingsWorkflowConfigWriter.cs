using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

/// <summary>将 <see cref="ClientSettings"/> 写入临时 JSON，供 ShortDrama AI/海报步骤读取。</summary>
public static class ClientSettingsWorkflowConfigWriter
{
    public static string WriteTempConfig(ClientSettings settings)
    {
        var dir = Path.Combine(AppPaths.DataRoot, "queue-workflow");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"workflow-config-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.json");

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["AiTextEndpoint"] = settings.AiTextEndpoint,
            ["AiTextApiKey"] = settings.AiTextApiKey,
            ["AiTextModel"] = settings.AiTextModel,
            ["AiTextSystemPrompt"] = settings.AiFullInfoSystemPrompt,
            ["ChatModelEndpoint"] = settings.AiTextEndpoint,
            ["ChatModelApiKey"] = settings.AiTextApiKey,
            ["ChatModelId"] = settings.AiTextModel,
            ["AiTextBatchPrompt"] = settings.AiFullInfoBatchPrompt,
            ["AiTextRetryPrompt"] = settings.AiFullInfoRetryPrompt,
            ["AiTextTimeoutSeconds"] = settings.AiTextTimeoutSeconds,
            ["ImageModelId"] = settings.ImageModelId,
            ["ImageModelApiKey"] = settings.ImageModelApiKey,
            ["ImageModelEndpoint"] = settings.ImageModelEndpoint,
            ["ImageProvider"] = settings.ImageProvider,
            ["PosterMode"] = settings.PosterMode,
            ["DoubaoImageResolution"] = settings.DoubaoImageResolution,
            ["DoubaoImageRatio"] = settings.DoubaoImageRatio,
            ["OfoxImage2ModelId"] = settings.OfoxImage2ModelId,
            ["OfoxImage2ApiKey"] = settings.OfoxImage2ApiKey,
            ["OfoxImage2Endpoint"] = settings.OfoxImage2Endpoint,
            ["OfoxImage2Quality"] = settings.OfoxImage2Quality,
            ["OfoxImage2Size"] = settings.OfoxImage2Size,
            ["PosterLayoutDetectPrompt"] = settings.PosterLayoutDetectPrompt,
            ["PosterInpaintPrompt"] = settings.PosterInpaintPrompt,
            ["PosterInpaintSafeRetryPrompt"] = settings.PosterInpaintSafeRetryPrompt,
            ["PosterGenerationPrompt"] = settings.PosterGenerationPrompt,
            ["PosterGenerationSafeRetryPrompt"] = settings.PosterGenerationSafeRetryPrompt,
            ["PosterNameSystemPrompt"] = settings.PosterNameSystemPrompt,
            ["PosterNameUserPrompt"] = settings.PosterNameUserPrompt,
            ["PosterTitleVerifyEnabled"] = settings.PosterTitleVerifyEnabled,
            ["PosterTitleVerifyMode"] = settings.PosterTitleVerifyMode,
            ["FrameCoverPrompt"] = settings.FrameCoverPrompt,
            ["ProjectImageGenerationMode"] = settings.TiktokProjectImageGenerationMode,
            ["ProjectImageTemplateRoot"] = settings.TiktokProjectImageTemplateRoot,
            ["ProjectImageTemplateId"] = settings.TiktokProjectImageTemplateId,
            ["ProjectImageTemplateName"] = ClientSettingsDefaults.TiktokProjectImageTemplateName,
            ["ProjectImageCount"] = settings.TiktokProjectImageCount,
            ["ProjectImageRenderEpisodeLimit"] = settings.TiktokProjectImageRenderEpisodeLimit,
            ["ProjectImageSubtitleAiMode"] = settings.TiktokProjectImageSubtitleAiMode,
        };
        PosterImageConfigHelper.ApplyPosterRuntimeConfig(payload, settings);

        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
        }));
        return path;
    }
}
