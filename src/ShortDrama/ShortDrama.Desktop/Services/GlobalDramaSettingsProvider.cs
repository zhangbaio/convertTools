using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Desktop.Models;
using ShortDrama.Desktop.Services;
using ShortDrama.Infrastructure.Automation;

namespace ShortDrama.Desktop.Services;

public sealed class GlobalDramaSettingsProvider : IDramaSettingsProvider
{
    private readonly GlobalSettingsService _globalSettingsService;

    public GlobalDramaSettingsProvider(GlobalSettingsService globalSettingsService)
    {
        _globalSettingsService = globalSettingsService;
    }

    public DramaSourceSettings Get() => FromGlobal(_globalSettingsService.Load());

    public void SavePikachuDeviceId(string deviceId)
    {
        var settings = _globalSettingsService.Load();
        _globalSettingsService.Save(settings with { PikachuDeviceId = deviceId.Trim() });
    }

    internal static DramaSourceSettings FromGlobal(GlobalConfigSnapshot settings) => new()
    {
        DramaSourceChain = settings.DramaSourceChain,
        DownloadFileSegments = settings.DownloadFileSegments,
        DownloaderApiBaseUrl = settings.DownloaderApiBaseUrl,
        DownloaderApiKey = settings.DownloaderApiKey,
        HgnewAccount = settings.HgnewAccount,
        HgnewPassword = settings.HgnewPassword,
        HgnewUdid = settings.HgnewUdid,
        HgnewClientVersion = settings.HgnewClientVersion,
        HghighAccount = settings.HghighAccount,
        HghighPassword = settings.HghighPassword,
        HghighEdition = HongguoClientProfile.NormalizeEdition(settings.HghighEdition),
        HghighDeviceId = settings.HghighDeviceId,
        HghighClientExe = settings.HghighClientExe,
        HghighStandardDeviceId = settings.HghighStandardDeviceId,
        HghighStandardClientExe = settings.HghighStandardClientExe,
        MapleleafAccount = settings.MapleleafAccount,
        MapleleafPassword = settings.MapleleafPassword,
        MapleleafUdid = settings.MapleleafUdid,
        HongguoDownloadTimeoutSeconds = settings.HongguoDownloadTimeoutSeconds,
        HongguoEpisodeDownloadAttempts = settings.HongguoEpisodeDownloadAttempts,
        HongguoLocalBaseUrl = settings.HongguoLocalBaseUrl,
        HongguoLocalApiKey = settings.HongguoLocalApiKey,
        HongguoLocalDownloadMode = settings.HongguoLocalDownloadMode,
        HongguoLocalTranscodeEngine = settings.HongguoLocalTranscodeEngine,
        PikachuServerUrl = settings.PikachuServerUrl,
        PikachuFanqieCookie = settings.PikachuFanqieCookie,
        PikachuDramaType = "manga",
        PikachuDeviceId = settings.PikachuDeviceId,
        PikachuClientVersion = settings.PikachuClientVersion,
    };
}
