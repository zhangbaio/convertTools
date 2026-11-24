using System.Text.Json;
using ShortDrama.Core.Posters;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public static class TikTokAccountAiSettings
{
    private static readonly object Gate = new();

    public sealed record BoundKey(string Endpoint, string Value);
    public sealed record Keys(BoundKey? Text = null, BoundKey? Image = null);

    public sealed class Library
    {
        public Dictionary<string, TikTokAiTextPreset> Text { get; init; } = new();
        public Dictionary<string, TikTokAiImagePreset> Image { get; init; } = new();
        public Dictionary<string, Keys> AccountKeys { get; init; } = new();
    }

    private static string LibraryPath => Path.Combine(AppPaths.DataRoot, "account-ai-local.json");

    public static Library Load()
    {
        lock (Gate)
        {
            var path = LibraryPath;
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Library>(File.ReadAllText(path)) ?? new()
                : new();
        }
    }

    public static void Save(string accountId, Keys keys,
        IEnumerable<TikTokAiTextPreset> text, IEnumerable<TikTokAiImagePreset> image)
    {
        lock (Gate)
        {
            var library = Load();
            foreach (var preset in text) library.Text[preset.Id] = preset;
            foreach (var preset in image) library.Image[preset.Id] = preset;
            if (keys == new Keys())
                library.AccountKeys.Remove(accountId);
            else
                library.AccountKeys[accountId] = keys;

            var path = LibraryPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(library));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    public static TikTokAiTextPreset DefaultText(ClientSettings settings) =>
        new("", "本机默认", settings.AiTextEndpoint, settings.AiTextModel, settings.AiTextTimeoutSeconds);

    public static TikTokAiImagePreset DefaultImage(ClientSettings settings) =>
        new("", "本机默认", settings.ImageModelEndpoint, settings.ImageModelId,
            settings.DoubaoImageResolution, settings.DoubaoImageRatio);

    public static bool IsVolcanoPreset(TikTokAiImagePreset preset) =>
        !PosterImageConfigHelper.IsOpenAiImageProvider(preset.Provider);

    public static string ResolveKey(BoundKey? local, string endpoint, string defaultEndpoint, string defaultKey)
    {
        if (local is not null && SameEndpoint(local.Endpoint, endpoint))
            return local.Value;
        return SameEndpoint(endpoint, defaultEndpoint) ? defaultKey : "";
    }

    public static ClientSettings Apply(ClientSettings defaults, TikTokAccountProfile? account)
    {
        var settings = defaults.Clone();
        settings.ImageProvider = "doubao";
        settings.TiktokRoleVectorViewMode = ClientSettingsDefaults.TiktokRoleVectorViewMode;
        settings.PosterTitleVerifyAiRetryCount = ClientSettingsDefaults.PosterTitleVerifyAiRetryCount;
        settings.PosterAgentDirectPrompt = ClientSettingsDefaults.PosterAgentDirectPrompt;
        settings.PosterAgentErasePrompt = ClientSettingsDefaults.PosterAgentErasePrompt;
        settings.PosterMode = ResolvePosterMode(account?.AiOptions is { } saved ? saved.PosterMode : defaults.PosterMode);
        if (PosterGenerationTemplates.SupportsPlainText(settings.PosterMode))
        {
            settings.PosterAgentDirectPrompt = PosterGenerationTemplates.ResolvePlainText(
                PosterGenerationTemplates.DirectMode, account?.AiOptions?.DirectPosterPrompt);
            settings.PosterAgentErasePrompt = PosterGenerationTemplates.ResolvePlainText(
                PosterGenerationTemplates.EraseMode, account?.AiOptions?.ErasePosterPrompt);
        }
        if (account?.AiOptions is not { } options)
        {
            settings.AiPublishInfoPrompt = ResolvePublishPrompt(settings.AiPublishInfoPrompt);
            return settings;
        }

        var library = Load();
        var keys = library.AccountKeys.GetValueOrDefault(account.Id) ?? new Keys();
        var textId = options.TextPresetId ?? "";
        var text = textId.Length == 0 ? DefaultText(defaults) : library.Text.GetValueOrDefault(textId);
        if (text is not null)
        {
            settings.AiTextEndpoint = text.Endpoint;
            settings.AiTextModel = text.Model;
            settings.AiTextTimeoutSeconds = text.Timeout;
        }
        settings.AiTextApiKey = text is null ? "" :
            ResolveKey(keys.Text, text.Endpoint, defaults.AiTextEndpoint, defaults.AiTextApiKey);

        var imageId = options.ImagePresetId ?? "";
        var imageDefault = DefaultImage(defaults);
        var image = imageId.Length == 0 ? imageDefault : library.Image.GetValueOrDefault(imageId);
        if (image is not null && !IsVolcanoPreset(image))
            image = imageDefault;

        if (image is not null)
        {
            settings.ImageModelEndpoint = image.Endpoint;
            settings.ImageModelId = image.Model;
            settings.DoubaoImageResolution = image.Resolution;
            settings.DoubaoImageRatio = image.Ratio;
        }
        settings.ImageModelApiKey = image is null ? "" :
            ResolveKey(keys.Image, image.Endpoint, imageDefault.Endpoint, defaults.ImageModelApiKey);

        settings.AiPublishInfoPrompt = ResolvePublishPrompt(options.PublishPrompt);
        settings.TiktokRoleReferenceSelectionMode = options.RoleSelectionMode;
        settings.TiktokRoleReferenceAiFallbackEnabled = options.RoleFallback;
        settings.PosterTitleVerifyEnabled = options.VerifyTitle;
        settings.PosterTitleVerifyMode = options.VerifyMode;
        return settings;
    }

    internal static string? GetPlainTextPosterPrompt(ClientSettings settings, string mode) => mode switch
    {
        PosterGenerationTemplates.DirectMode => settings.PosterAgentDirectPrompt ?? "",
        PosterGenerationTemplates.EraseMode => settings.PosterAgentErasePrompt ?? "",
        _ => null,
    };

    public static string ResolvePosterMode(string? value)
    {
        var mode = value?.Trim().ToLowerInvariant();
        return mode is null or "" or ClientSettingsDefaults.PosterModeReferenceRegenerate
            ? ClientSettingsDefaults.PosterModeDirect
            : mode;
    }

    public static string ResolvePublishPrompt(string? prompt) =>
        string.IsNullOrWhiteSpace(prompt) ? ClientSettingsDefaults.TikTokUnifiedPublishInfoPrompt : prompt;

    public static bool SameEndpoint(string left, string right) =>
        string.Equals(left.Trim().TrimEnd('/'), right.Trim().TrimEnd('/'), StringComparison.Ordinal);
}
