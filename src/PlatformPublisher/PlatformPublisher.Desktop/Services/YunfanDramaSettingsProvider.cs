using PlatformPublisher.Common.Services;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Services;

namespace PlatformPublisher.Desktop.Services;

/// <summary>
/// 共享流水线每次下载都读取云帆自己的系统设置，避免沿用启动时的旧快照。
/// </summary>
public sealed class YunfanDramaSettingsProvider : IDramaSettingsProvider
{
    public DramaSourceSettings Get() =>
        DramaSourceSettingsMapping.FromClientSettings(
            ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath));

    public void SavePikachuDeviceId(string deviceId)
    {
        var settings = ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);
        settings.PikachuDeviceId = deviceId.Trim();
        ClientSettingsStore.PatchPikachuRuntimeFields(
            settings.PikachuFanqieCookie,
            settings.PikachuDeviceId,
            PlatformPublisherPaths.SettingsDatabasePath);
    }
}
