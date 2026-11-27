namespace TikTokPublisher.Core.Models;

public sealed record TikTokProjectImageOptions(string TemplateId, string SubtitleAiMode, int Count, int EpisodeLimit)
{
    public static TikTokProjectImageOptions FromSettings(ClientSettings settings) => new(
        settings.TiktokProjectImageTemplateId,
        settings.TiktokProjectImageSubtitleAiMode,
        settings.TiktokProjectImageCount,
        settings.TiktokProjectImageRenderEpisodeLimit);

    public static ClientSettings Apply(ClientSettings settings, TikTokAccountProfile? account)
    {
        if (account?.ProjectImageOptions is not { } options)
            return settings;

        var effective = settings.Clone();
        effective.TiktokProjectImageTemplateId = options.TemplateId;
        effective.TiktokProjectImageSubtitleAiMode = options.SubtitleAiMode is "fast" or "accurate" or "off"
            ? options.SubtitleAiMode
            : settings.TiktokProjectImageSubtitleAiMode;
        effective.TiktokProjectImageCount = Math.Clamp(options.Count, 1, 20);
        effective.TiktokProjectImageRenderEpisodeLimit = Math.Clamp(options.EpisodeLimit, 1, 200);
        return effective;
    }
}
