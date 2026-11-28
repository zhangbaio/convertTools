namespace TikTokPublisher.Core.Models;

public sealed record TikTokProjectImageOptions(string TemplateId, string SubtitleAiMode, int Count, int EpisodeLimit)
{
    public static TikTokProjectImageOptions FromSettings(ClientSettings settings) => new(
        settings.TiktokProjectImageTemplateId,
        ClientSettingsDefaults.TiktokProjectImageSubtitleAiMode,
        ClientSettingsDefaults.TiktokProjectImageCount,
        ClientSettingsDefaults.TiktokProjectImageRenderEpisodeLimit);

    public static ClientSettings Apply(ClientSettings settings, TikTokAccountProfile? account)
    {
        var templateId = account?.ProjectImageOptions?.TemplateId ?? settings.TiktokProjectImageTemplateId;
        if (templateId == settings.TiktokProjectImageTemplateId &&
            settings.TiktokProjectImageSubtitleAiMode == ClientSettingsDefaults.TiktokProjectImageSubtitleAiMode &&
            settings.TiktokProjectImageCount == ClientSettingsDefaults.TiktokProjectImageCount &&
            settings.TiktokProjectImageRenderEpisodeLimit == ClientSettingsDefaults.TiktokProjectImageRenderEpisodeLimit)
            return settings;

        var effective = settings.Clone();
        effective.TiktokProjectImageTemplateId = templateId;
        ApplyGenerationDefaults(effective);
        return effective;
    }

    internal static void ApplyGenerationDefaults(ClientSettings settings)
    {
        settings.TiktokProjectImageSubtitleAiMode = ClientSettingsDefaults.TiktokProjectImageSubtitleAiMode;
        settings.TiktokProjectImageCount = ClientSettingsDefaults.TiktokProjectImageCount;
        settings.TiktokProjectImageRenderEpisodeLimit = ClientSettingsDefaults.TiktokProjectImageRenderEpisodeLimit;
    }
}
