using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace PlatformPublisher.Desktop.Services;

/// <summary>
/// 从所运行客户端的系统设置读取本土剧链路。
/// </summary>
public sealed class YunfanDramaSettingsProvider(string databasePath) : IDramaSettingsProvider
{
    public ClientSettings LoadClientSettings() => ClientSettingsStore.Load(databasePath);

    public DramaSourceSettings Get() =>
        DramaSourceSettingsMapping.FromClientSettings(LoadClientSettings());

    public void SavePikachuDeviceId(string deviceId)
    {
        var settings = LoadClientSettings();
        ClientSettingsStore.PatchPikachuRuntimeFields(
            settings.PikachuFanqieCookie,
            deviceId.Trim(),
            databasePath);
    }

    public void SaveHongguoAppPlaybackIdentity(string deviceId, string installId) =>
        ClientSettingsStore.PatchHongguoAppPlaybackIdentity(
            deviceId,
            installId,
            databasePath);
}
