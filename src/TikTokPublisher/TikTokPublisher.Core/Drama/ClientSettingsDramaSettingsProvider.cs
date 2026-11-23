using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Drama;

public static class DramaSourceSettingsMapping
{
    public static DramaSourceSettings FromClientSettings(ClientSettings settings) => new()
    {
        DramaSourceChain = NormalizeChain(settings.DramaSourceChain),
        HgnewAccount = settings.HgnewAccount ?? "",
        HgnewPassword = settings.HgnewPassword ?? "",
        HgnewUdid = settings.HgnewUdid ?? "",
        HgnewClientVersion = settings.HgnewClientVersion ?? ClientSettings.DefaultHongguoClientVersion,
        HghighAccount = settings.HghighAccount ?? "",
        HghighPassword = settings.HghighPassword ?? "",
        HghighEdition = HongguoClientProfile.StandardEdition,
        HghighDeviceId = settings.HghighDeviceId ?? "",
        HghighClientExe = settings.HghighClientExe ?? "",
        HghighClientVersion = settings.HghighClientVersion ?? "",
        HghighStandardDeviceId = settings.HghighStandardDeviceId ?? "",
        HghighStandardClientExe = settings.HghighStandardClientExe ?? "",
        HghighStandardClientVersion = settings.HghighStandardClientVersion ?? "",
        HghighStandardPlaybackMode = HongguoClientProfile.NormalizeStandardPlaybackMode(settings.HghighStandardPlaybackMode),
        HongguoAppDeviceId = settings.HongguoAppDeviceId ?? "",
        HongguoAppInstallId = settings.HongguoAppInstallId ?? "",
        HongguoAppPlan = ClientSettings.NormalizeHongguoAppPlan(settings.HongguoAppPlan),
        HongguoAppStreamLink1 = settings.HongguoAppStreamLink1,
        HongguoAppStreamLink2 = settings.HongguoAppStreamLink2,
        HongguoDownloadTimeoutSeconds = settings.HongguoDownloadTimeoutSeconds.ToString(),
        HongguoEpisodeDownloadAttempts = settings.HongguoEpisodeDownloadAttempts.ToString(),
        DownloadFileSegments = settings.DownloadFileSegments.ToString(),
        DownloaderApiBaseUrl = settings.DownloaderApiBaseUrl ?? "http://127.0.0.1:17891",
        DownloaderApiKey = settings.DownloaderApiKey ?? "",
        PikachuServerUrl = settings.PikachuServerUrl ?? "",
        PikachuFanqieCookie = settings.PikachuFanqieCookie ?? "",
        PikachuDramaType = string.Equals(settings.PikachuDramaType, "manga", StringComparison.OrdinalIgnoreCase)
            ? "manga"
            : "short",
        PikachuDeviceId = settings.PikachuDeviceId ?? "",
        PikachuClientVersion = settings.PikachuClientVersion ?? "1.4.4",
        PlayletServerUrl = string.IsNullOrWhiteSpace(settings.PlayletServerUrl)
            ? PlayletDramaClient.DefaultServerUrl
            : settings.PlayletServerUrl.Trim(),
        PlayletDeviceId = settings.PlayletDeviceId ?? "",
        ForeignServerUrl = string.IsNullOrWhiteSpace(settings.ForeignServerUrl)
            ? ForeignPlayletClient.DefaultServerUrl
            : settings.ForeignServerUrl.Trim(),
        ForeignDeviceId = settings.ForeignDeviceId ?? "",
        ForeignSearchSource = ForeignPlayletClient.NormalizeSource(settings.ForeignSearchSource),
        ForeignLanguage = ForeignPlayletClient.NormalizeLanguage(settings.ForeignLanguage),
        DramaBoxApiBaseUrl = string.IsNullOrWhiteSpace(settings.DramaBoxApiBaseUrl)
            ? DramaBoxApiService.DefaultBaseUrl
            : settings.DramaBoxApiBaseUrl.Trim().TrimEnd('/'),
        DramaBoxDeviceId = string.IsNullOrWhiteSpace(settings.DramaBoxDeviceId)
            ? DramaBoxApiService.DefaultDeviceId
            : settings.DramaBoxDeviceId.Trim(),
    };

    private static string NormalizeChain(string? chain) => VisibleDramaSource.Normalize(chain);

}

public sealed class ClientSettingsDramaSettingsProvider : IDramaSettingsProvider
{
    private readonly object _gate = new();
    private ClientSettings _settings;
    private string? _databasePath;

    public ClientSettingsDramaSettingsProvider(ClientSettings? initial = null, string? databasePath = null)
    {
        _databasePath = NormalizeDatabasePath(databasePath);
        _settings = initial ?? ClientSettingsStore.Load(_databasePath);
    }

    public void UseDatabase(string? databasePath)
    {
        lock (_gate)
        {
            _databasePath = NormalizeDatabasePath(databasePath);
            _settings = ClientSettingsStore.Load(_databasePath);
        }
    }

    public void Reload()
    {
        lock (_gate)
        {
            _settings = ClientSettingsStore.Load(_databasePath);
        }
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
        if (ShortDramaDramaServices.OperationSettings is { } operation)
            return DramaSourceSettingsMapping.FromClientSettings(operation);

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
            if (ShortDramaDramaServices.OperationSettings is { } operation)
                operation.PikachuDeviceId = _settings.PikachuDeviceId;
            ClientSettingsStore.PatchPikachuRuntimeFields(_settings.PikachuFanqieCookie, _settings.PikachuDeviceId, _databasePath);
        }
    }

    public void SaveHongguoAppPlaybackIdentity(string deviceId, string installId)
    {
        lock (_gate)
        {
            _settings.HongguoAppDeviceId = deviceId.Trim();
            _settings.HongguoAppInstallId = installId.Trim();
            if (ShortDramaDramaServices.OperationSettings is { } operation)
            {
                operation.HongguoAppDeviceId = _settings.HongguoAppDeviceId;
                operation.HongguoAppInstallId = _settings.HongguoAppInstallId;
            }
            ClientSettingsStore.PatchHongguoAppPlaybackIdentity(
                _settings.HongguoAppDeviceId,
                _settings.HongguoAppInstallId,
                _databasePath);
        }
    }

    private static string? NormalizeDatabasePath(string? databasePath) =>
        string.IsNullOrWhiteSpace(databasePath) ? null : Path.GetFullPath(databasePath);
}
