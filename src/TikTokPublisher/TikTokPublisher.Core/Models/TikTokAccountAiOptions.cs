namespace TikTokPublisher.Core.Models;

public sealed record TikTokAiTextPreset(string Id, string Name, string Endpoint, string Model, int Timeout);
public sealed record TikTokAiImagePreset(string Id, string Name, string Endpoint, string Model, string Resolution, string Ratio, string Provider = "doubao");
public sealed record TikTokAccountAiOptions(
    string TextPresetId, string ImagePresetId, string PublishPrompt,
    string RoleSelectionMode, bool RoleFallback,
    string PosterMode, bool VerifyTitle, string VerifyMode,
    string? DirectPosterPrompt = null, string? ErasePosterPrompt = null)
{
    public static TikTokAccountAiOptions FromSettings(ClientSettings settings) => new(
        "", "", Services.TikTokAccountAiSettings.ResolvePublishPrompt(settings.AiPublishInfoPrompt),
        settings.TiktokRoleReferenceSelectionMode,
        settings.TiktokRoleReferenceAiFallbackEnabled,
        settings.PosterMode, settings.PosterTitleVerifyEnabled, settings.PosterTitleVerifyMode);
}
