using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Drama;

public static class DramaSourceSettingsMapping
{
    public static DramaSourceSettings FromClientSettings(ClientSettings settings) => new()
    {
        DramaSourceChain = NormalizeChain(settings.DramaSourceChain),
        DramaServiceOrderSearch = "",
        DramaServiceOrderDownload = "",
        DramaServiceOrderNewRelease = "",
        DramaServiceOrderRanking = "",
        HgnewAccount = settings.HgnewAccount ?? "",
        HgnewPassword = settings.HgnewPassword ?? "",
        HgnewUdid = settings.HgnewUdid ?? "",
        HgnewClientVersion = settings.HgnewClientVersion ?? ClientSettings.DefaultHongguoClientVersion,
        HongguoDownloadTimeoutSeconds = settings.HongguoDownloadTimeoutSeconds.ToString(),
        HongguoEpisodeDownloadAttempts = settings.HongguoEpisodeDownloadAttempts.ToString(),
        HongguoLocalBaseUrl = settings.HongguoLocalBaseUrl ?? "",
        HongguoLocalApiKey = settings.HongguoLocalApiKey ?? "",
        PikachuServerUrl = settings.PikachuServerUrl ?? "",
        PikachuFanqieCookie = settings.PikachuFanqieCookie ?? "",
        PikachuDramaType = settings.PikachuDramaType ?? "short",
        PikachuDeviceId = settings.PikachuDeviceId ?? "",
        PikachuClientVersion = settings.PikachuClientVersion ?? "1.4.2",
    };

    private static string NormalizeChain(string? chain)
    {
        var normalized = (chain ?? "hgnew").Trim().ToLowerInvariant();
        return normalized is "hgnew" or "pikachu" or "hg52api" or "hglocal" ? normalized : "hgnew";
    }
}

public sealed class ClientSettingsDramaSettingsProvider : IDramaSettingsProvider
{
    private readonly object _gate = new();
    private ClientSettings _settings;
    private readonly string? _databasePath;

    public ClientSettingsDramaSettingsProvider(ClientSettings? initial = null, string? databasePath = null)
    {
        _settings = initial ?? ClientSettingsStore.Load(databasePath);
        _databasePath = databasePath;
    }

    public void Replace(ClientSettings settings)
    {
        lock (_gate)
        {
            _settings = settings.Clone();
        }
    }

    public DramaSourceSettings Get()
    {
        lock (_gate)
        {
            return DramaSourceSettingsMapping.FromClientSettings(_settings);
        }
    }

    public void SavePikachuDeviceId(string deviceId)
    {
        lock (_gate)
        {
            _settings.PikachuDeviceId = deviceId.Trim();
            ClientSettingsStore.PatchPikachuRuntimeFields(_settings.PikachuFanqieCookie, _settings.PikachuDeviceId, _databasePath);
        }
    }
}
