using PlatformPublisher.Common.Services;
using PlatformPublisher.Kuaishou.Publishing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace PlatformPublisher.Desktop.Services;

internal static class KuaishouAccountAiSettings
{
    internal static ClientSettings Load(string accountId, KuaishouAccountAiStore store)
    {
        var defaults = ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);
        return string.IsNullOrWhiteSpace(accountId) ? defaults : Apply(defaults, store.Load(accountId));
    }

    internal static KuaishouTextPreset DefaultText(ClientSettings settings) =>
        new("", "本机默认", settings.AiTextEndpoint, settings.AiTextModel, settings.AiTextTimeoutSeconds);

    internal static KuaishouImagePreset DefaultImage(ClientSettings settings)
    {
        var provider = PosterImageConfigHelper.NormalizeImageProvider(settings.ImageProvider);
        return provider == "ofox_image2"
            ? new("", "本机默认",
                string.IsNullOrWhiteSpace(settings.OfoxImage2Endpoint) ? "https://api.ofox.ai/v1" : settings.OfoxImage2Endpoint,
                settings.OfoxImage2ModelId, provider, settings.OfoxImage2Size, settings.OfoxImage2Quality)
            : new("", "本机默认", settings.ImageModelEndpoint, settings.ImageModelId, provider,
                settings.DoubaoImageRatio, settings.DoubaoImageResolution);
    }

    internal static string ResolveKey(Dictionary<string, string> keys, string endpoint, string defaultEndpoint, string defaultKey)
    {
        var address = KuaishouAccountAiOptions.EndpointKey(endpoint);
        if (address.Length > 0 && keys.TryGetValue(address, out var key))
            return key;
        return address.Length > 0 && address == KuaishouAccountAiOptions.EndpointKey(defaultEndpoint) ? defaultKey : "";
    }

    internal static ClientSettings Apply(ClientSettings defaults, KuaishouAccountAiOptions options)
    {
        var settings = defaults.Clone();
        var defaultText = DefaultText(defaults);
        var text = options.TextPresets.FirstOrDefault(preset => preset.Id == options.TextPresetId) ?? defaultText;
        settings.AiTextEndpoint = text.Endpoint;
        settings.AiTextModel = text.Model;
        settings.AiTextTimeoutSeconds = text.Timeout;
        settings.AiTextApiKey = ResolveKey(options.TextKeys, text.Endpoint, defaultText.Endpoint, defaults.AiTextApiKey);

        var defaultImage = DefaultImage(defaults);
        var image = options.ImagePresets.FirstOrDefault(preset => preset.Id == options.ImagePresetId) ?? defaultImage;
        settings.ImageProvider = image.Provider;
        var defaultImageKey = defaultImage.Provider == "ofox_image2" ? defaults.OfoxImage2ApiKey : defaults.ImageModelApiKey;
        var imageKey = ResolveKey(options.ImageKeys, image.Endpoint,
            image.Provider == defaultImage.Provider ? defaultImage.Endpoint : "", defaultImageKey);
        if (image.Provider == "ofox_image2")
        {
            settings.OfoxImage2Endpoint = image.Endpoint;
            settings.OfoxImage2ModelId = image.Model;
            settings.OfoxImage2Size = image.Size;
            settings.OfoxImage2Quality = image.Quality;
            settings.OfoxImage2ApiKey = imageKey;
        }
        else
        {
            settings.ImageModelEndpoint = image.Endpoint;
            settings.ImageModelId = image.Model;
            settings.DoubaoImageRatio = image.Size;
            settings.DoubaoImageResolution = image.Quality;
            settings.ImageModelApiKey = imageKey;
        }

        if (options.PublishPrompt.Length > 0) settings.KuaishouPublishInfoPrompt = options.PublishPrompt;
        if (options.PosterMode.Length > 0) settings.PosterMode = options.PosterMode;
        if (options.VerticalDirectPrompt.Length > 0) settings.KuaishouPosterVerticalDirectPrompt = options.VerticalDirectPrompt;
        if (options.HorizontalDirectPrompt.Length > 0) settings.KuaishouPosterHorizontalDirectPrompt = options.HorizontalDirectPrompt;
        if (options.VerticalErasePrompt.Length > 0) settings.KuaishouPosterVerticalErasePrompt = options.VerticalErasePrompt;
        if (options.HorizontalErasePrompt.Length > 0) settings.KuaishouPosterHorizontalErasePrompt = options.HorizontalErasePrompt;
        return settings;
    }
}
