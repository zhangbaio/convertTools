namespace TikTokPublisher.Core.Models;

public sealed record TikTokAiTextPreset(string Id, string Name, string Endpoint, string Model, int Timeout);
public sealed record TikTokAiImagePreset(string Id, string Name, string Endpoint, string Model, string Resolution, string Ratio, string Provider = "doubao");
public sealed record TikTokAccountAiOptions(
    string TextPresetId, string ImagePresetId, string PublishPrompt,
    string RoleSelectionMode, bool RoleFallback, string RoleViewMode,
    string PosterMode, bool VerifyTitle, string VerifyMode, int VerifyRetries)
{
    public static TikTokAccountAiOptions FromSettings(ClientSettings settings) => new(
        "", "", Services.ClientSettingsStore.ResolvePublishInfoPrompt(settings), settings.TiktokRoleReferenceSelectionMode,
        settings.TiktokRoleReferenceAiFallbackEnabled, settings.TiktokRoleVectorViewMode,
        settings.PosterMode, settings.PosterTitleVerifyEnabled, settings.PosterTitleVerifyMode,
        settings.PosterTitleVerifyAiRetryCount);
}
