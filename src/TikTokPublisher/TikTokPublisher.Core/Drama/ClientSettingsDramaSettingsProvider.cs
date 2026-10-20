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
        HghighEdition = HongguoClientProfile.NormalizeEdition(settings.HghighEdition),
        HghighDeviceId = settings.HghighDeviceId ?? "",
        HghighClientExe = settings.HghighClientExe ?? "",
        HghighClientVersion = settings.HghighClientVersion ?? "",
        HghighStandardDeviceId = settings.HghighStandardDeviceId ?? "",
        HghighStandardClientExe = settings.HghighStandardClientExe ?? "",
        HghighStandardClientVersion = settings.HghighStandardClientVersion ?? "",
        HghighStandardPlaybackMode = HongguoClientProfile.NormalizeStandardPlaybackMode(settings.HghighStandardPlaybackMode),
        MapleleafAccount = settings.MapleleafAccount ?? "",
        MapleleafPassword = settings.MapleleafPassword ?? "",
        MapleleafUdid = settings.MapleleafUdid ?? "",
        HongguoDownloadTimeoutSeconds = settings.HongguoDownloadTimeoutSeconds.ToString(),
        HongguoEpisodeDownloadAttempts = settings.HongguoEpisodeDownloadAttempts.ToString(),
        DownloadFileSegments = settings.DownloadFileSegments.ToString(),
        DownloaderApiBaseUrl = settings.DownloaderApiBaseUrl ?? "http://127.0.0.1:17891",
        DownloaderApiKey = settings.DownloaderApiKey ?? "",
        HongguoLocalBaseUrl = settings.HongguoLocalBaseUrl ?? "",
        HongguoLocalApiKey = settings.HongguoLocalApiKey ?? "",
        HongguoLocalDownloadMode = NormalizeHongguoLocalDownloadMode(settings.HongguoLocalDownloadMode),
        HongguoLocalTranscodeEngine = NormalizeHongguoLocalTranscodeEngine(settings.HongguoLocalTranscodeEngine),
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
    };

    private static string NormalizeChain(string? chain) => VisibleDramaSource.Normalize(chain);

    private static string NormalizeHongguoLocalDownloadMode(string? mode)
    {
        var normalized = (mode ?? "fast").Trim().ToLowerInvariant();
        return normalized is "compatible" ? "compatible" : "fast";
    }

    private static string NormalizeHongguoLocalTranscodeEngine(string? value)
    {
        var normalized = (value ?? "auto").Trim().ToLowerInvariant();
        return normalized is "auto" or "nvenc" or "cpu" ? normalized : "auto";
    }
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

    private static string? NormalizeDatabasePath(string? databasePath) =>
        string.IsNullOrWhiteSpace(databasePath) ? null : Path.GetFullPath(databasePath);
}
