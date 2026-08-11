using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

/// <summary>将 <see cref="ClientSettings"/> 写入临时 JSON，供 ShortDrama AI/海报步骤读取。</summary>
public static class ClientSettingsWorkflowConfigWriter
{
    public static string WriteTempConfig(ClientSettings settings, TikTokAccountProfile? account = null)
    {
        var dir = Path.Combine(AppPaths.DataRoot, "queue-workflow");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"workflow-config-{Guid.NewGuid():N}.json");

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
            ["AiRewriteSynopsis"] = account?.TiktokAiRewriteSynopsis == true,
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
            ["PosterTitleVerifyAiRetryCount"] = settings.PosterTitleVerifyAiRetryCount,
            ["FrameExtractEpisodeIndex"] = settings.FrameExtractEpisodeIndex,
            ["FrameExtractTime"] = settings.FrameExtractTime,
            ["FrameExtractNeighborOffsetsSeconds"] = settings.FrameExtractNeighborOffsetsSeconds,
            ["FrameExtractFallbackPercents"] = settings.FrameExtractFallbackPercents,
            ["FrameCoverPrompt"] = settings.FrameCoverPrompt,
            ["ProjectImageGenerationMode"] = settings.TiktokProjectImageGenerationMode,
            ["ProjectImageTemplateRoot"] = settings.TiktokProjectImageTemplateRoot,
            ["ProjectImageTemplateId"] = settings.TiktokProjectImageTemplateId,
            ["ProjectImageTemplateName"] = TikTokProjectImageTemplateCatalog.ResolveName(
                settings.TiktokProjectImageTemplateId),
            ["ProjectImageCount"] = settings.TiktokProjectImageCount,
            ["ProjectImageRenderEpisodeLimit"] = settings.TiktokProjectImageRenderEpisodeLimit,
            ["ProjectImageSubtitleAiMode"] = settings.TiktokProjectImageSubtitleAiMode,
            ["ProjectImageFableCutRoot"] = settings.TiktokProjectImageFableCutRoot,
            ["ProjectImageFableCutClipCount"] = settings.TiktokProjectImageFableCutClipCount,
            ["ProjectImageFableCutScreenshotStyle"] = ClientSettingsDefaults.TiktokProjectImageFableCutScreenshotStyle,
        };
        PosterImageConfigHelper.ApplyPosterRuntimeConfig(payload, settings);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
        };
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            JsonSerializer.Serialize(stream, payload, options);
        }

        return path;
    }
}
