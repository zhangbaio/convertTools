using System.Text.Json;
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
        PosterImageConfigHelper.IsOpenAiImageProvider(settings.ImageProvider)
            ? new("", "本机默认", settings.OfoxImage2Endpoint, settings.OfoxImage2ModelId,
                settings.OfoxImage2Size, settings.OfoxImage2Quality, "ofox_image2")
            : new("", "本机默认", settings.ImageModelEndpoint, settings.ImageModelId,
                settings.DoubaoImageResolution, settings.DoubaoImageRatio);

    public static string DefaultImageKey(ClientSettings settings) =>
        PosterImageConfigHelper.IsOpenAiImageProvider(settings.ImageProvider)
            ? settings.OfoxImage2ApiKey : settings.ImageModelApiKey;

    public static string ResolveKey(BoundKey? local, string endpoint, string defaultEndpoint, string defaultKey)
    {
        if (local is not null && SameEndpoint(local.Endpoint, endpoint))
            return local.Value;
        return SameEndpoint(endpoint, defaultEndpoint) ? defaultKey : "";
    }

    public static ClientSettings Apply(ClientSettings defaults, TikTokAccountProfile? account)
    {
        var settings = defaults.Clone();
        settings.PosterAgentDirectPrompt = ClientSettingsDefaults.PosterAgentDirectPrompt;
        settings.PosterAgentErasePrompt = ClientSettingsDefaults.PosterAgentErasePrompt;
        if (account?.AiOptions is not { } options)
            return settings;

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
        if (image is null)
        {
            settings.ImageModelApiKey = settings.OfoxImage2ApiKey = "";
        }
        else
        {
            var key = ResolveKey(keys.Image, image.Endpoint, imageDefault.Endpoint, DefaultImageKey(defaults));
            settings.ImageProvider = image.Provider;
            if (PosterImageConfigHelper.IsOpenAiImageProvider(image.Provider))
            {
                settings.OfoxImage2Endpoint = image.Endpoint;
                settings.OfoxImage2ModelId = image.Model;
                settings.OfoxImage2Size = image.Resolution;
                settings.OfoxImage2Quality = image.Ratio;
                settings.OfoxImage2ApiKey = key;
            }
            else
            {
                settings.ImageModelEndpoint = image.Endpoint;
                settings.ImageModelId = image.Model;
                settings.DoubaoImageResolution = image.Resolution;
                settings.DoubaoImageRatio = image.Ratio;
                settings.ImageModelApiKey = key;
            }
        }

        settings.AiPublishInfoPrompt = string.IsNullOrWhiteSpace(options.PublishPrompt)
            ? ClientSettingsDefaults.AiPublishInfoPrompt : options.PublishPrompt;
        settings.TiktokRoleReferenceSelectionMode = options.RoleSelectionMode;
        settings.TiktokRoleReferenceAiFallbackEnabled = options.RoleFallback;
        settings.TiktokRoleVectorViewMode = options.RoleViewMode;
        settings.PosterMode = options.PosterMode;
        settings.PosterTitleVerifyEnabled = options.VerifyTitle;
        settings.PosterTitleVerifyMode = options.VerifyMode;
        settings.PosterTitleVerifyAiRetryCount = options.VerifyRetries;
        return settings;
    }

    public static bool SameEndpoint(string left, string right) =>
        string.Equals(left.Trim().TrimEnd('/'), right.Trim().TrimEnd('/'), StringComparison.Ordinal);
}
