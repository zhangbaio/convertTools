using PlatformPublisher.Common.Services;
using TikTokPublisher.Core.Services;

namespace PlatformPublisher.Desktop.Services;

public sealed class PlatformAiRuntimeSettingsProvider : IAiRuntimeSettingsProvider
{
    public AiRuntimeSettings Load()
    {
        var settings = ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);
        return new AiRuntimeSettings(
            settings.AiTextEndpoint,
            settings.AiTextApiKey,
            settings.AiTextModel,
            settings.AiTextTimeoutSeconds,
            settings.TiktokAsrLocalModelDir,
            settings.TiktokAsrLocalVadPath,
            settings.TiktokAsrAppId,
            settings.TiktokAsrAccessToken,
            settings.TiktokAsrLanguage);
    }
}
