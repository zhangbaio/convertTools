using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 超长剧入队：开关打开时，下载和本地导入都把超过设定集数的剧截成前 N 集。
/// </summary>
public static class OverLimitEpisodePolicy
{
    public static int ResolveKeepCount(ClientSettings settings)
    {
        var configured = settings.TiktokOverLimitDownloadEpisodeCount <= 0
            ? ClientSettingsDefaults.TiktokOverLimitDownloadEpisodeCount
            : settings.TiktokOverLimitDownloadEpisodeCount;
        return Math.Clamp(configured, 1, ClientSettingsDefaults.TiktokOverLimitDownloadEpisodeCount);
    }

    public static bool ShouldTruncate(int episodeCount, ClientSettings? settings)
    {
        if (settings is null || !settings.TiktokAllowOverLimitUploadImport || episodeCount <= 0)
            return false;
        return episodeCount > ResolveKeepCount(settings);
    }
}
