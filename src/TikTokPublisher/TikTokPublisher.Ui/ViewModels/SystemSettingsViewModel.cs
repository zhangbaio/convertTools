using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Net.Http.Json;
using System.Text.Json;
using ShortDrama.Core.Services;
using ShortDrama.Infrastructure.Automation;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Core.Services.Asr;

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class SystemSettingsViewModel : ViewModelBase
{
    private static readonly HttpClient ProbeHttp = CreateProbeHttpClient();
    private readonly string? _settingsDatabasePath;

    public SystemSettingsViewModel(string? settingsDatabasePath = null, bool kuaishouPublishPrompts = false)
    {
        _settingsDatabasePath = string.IsNullOrWhiteSpace(settingsDatabasePath)
            ? null
            : Path.GetFullPath(settingsDatabasePath);
        MainDatabasePath = _settingsDatabasePath ?? ClientSettingsStore.MainDatabasePath;
        ShowsKuaishouPublishPrompt = kuaishouPublishPrompts;
        ShowsTikTokPublishPrompt = !kuaishouPublishPrompts;
    }

    public bool ShowsKuaishouPublishPrompt { get; }
    public bool ShowsTikTokPublishPrompt { get; }

    public event Action<ClientSettings>? SettingsSaved;
    public event Action<string>? StatusRequested;
    public event Action<HongguoLoginProbeResult>? HgnewLoginProbeSucceeded;
    public event Func<string, Task>? CopyToClipboardAsync;

    [ObservableProperty] private string _saveMessage = "";
    [ObservableProperty] private string _loginSettingsHint =
        "短剧搜索、下载和数据链路参数为全局共享配置；TikTok 登录信息请到账号管理的登录设置中维护。";
    [ObservableProperty] private string _hgnewProbeStatus = "";
    [ObservableProperty] private string _databaseStatsText = "";
    [ObservableProperty] private string _mainDatabasePath = ClientSettingsStore.MainDatabasePath;
    [ObservableProperty] private string _workspaceDatabasePath = "";

    [ObservableProperty] private string _dramaSourceChain = VisibleDramaSource.DefaultChain;
    [ObservableProperty] private string _dramaDownloadDefaultQuality = "1080P";
    [ObservableProperty] private int _dramaDownloadConcurrent = 5;
    [ObservableProperty] private int _dramaDownloadMaxParallelProjects = 1;
    [ObservableProperty] private int _downloadFileSegments = 4;
    [ObservableProperty] private string _downloaderApiBaseUrl = "http://127.0.0.1:17891";
    [ObservableProperty] private string _downloaderApiKey = "";
    [ObservableProperty] private string _downloaderProbeStatus = "";
    [ObservableProperty] private int _hongguoDownloadTimeoutSeconds = 60;
    [ObservableProperty] private int _hongguoEpisodeDownloadAttempts = 5;

    [ObservableProperty] private string _hgnewAccount = "";
    [ObservableProperty] private string _hgnewPassword = "";
    [ObservableProperty] private string _hgnewUdid = "";
    [ObservableProperty] private string _hgnewClientVersion = ClientSettings.DefaultHongguoClientVersion;

    [ObservableProperty] private string _hghighAccount = "";
    [ObservableProperty] private string _hghighPassword = "";
    [ObservableProperty] private string _hghighEdition = HongguoClientProfile.HighEdition;
    [ObservableProperty] private string _hghighDeviceId = "";
    [ObservableProperty] private string _hghighClientExe = "";
    [ObservableProperty] private string _hghighClientVersion = HongguoClientProfile.High.ClientVersion;
    [ObservableProperty] private string _hghighStandardDeviceId = "";
    [ObservableProperty] private string _hghighStandardClientExe = "";
    [ObservableProperty] private string _hghighStandardClientVersion = HongguoClientProfile.Standard.ClientVersion;
    [ObservableProperty] private string _hghighStandardPlaybackMode = HongguoClientProfile.StandardPlaybackSingle;
    [ObservableProperty] private string _hghighEncMaster = "";
    [ObservableProperty] private string _hghighSignMaster = "";
    [ObservableProperty] private string _hghighProbeStatus = "";
    [ObservableProperty] private string _hghighMastersStatus = "";
    [ObservableProperty] private bool _isHghighBusy;
    [ObservableProperty] private bool _hghighRevealEnc;
    [ObservableProperty] private bool _hghighRevealSign;

    [ObservableProperty] private string _mapleleafAccount = "";
    [ObservableProperty] private string _mapleleafPassword = "";
    [ObservableProperty] private string _mapleleafUdid = "";
    [ObservableProperty] private string _mapleleafProbeStatus = "";

    public string HghighRevealEncButtonText => HghighRevealEnc ? "隐藏密钥" : "显示密钥";
    public string HghighRevealSignButtonText => HghighRevealSign ? "隐藏密钥" : "显示密钥";
    public string HghighEditionLabel => SelectedHghighProfile.DisplayName;
    public string HghighDeviceLabel => HghighEdition == HongguoClientProfile.StandardEdition ? "标准版设备 ID" : "高码率设备 ID";
    public string HghighClientExeLabel => HghighEdition == HongguoClientProfile.StandardEdition ? "标准版客户端 exe" : "高码率客户端 exe";
    public string HghighClientVersionLabel => HghighEdition == HongguoClientProfile.StandardEdition ? "标准版协议版本" : "高码率协议版本";
    public bool IsHghighStandardEdition => HghighEdition == HongguoClientProfile.StandardEdition;
    public string HghighActiveDeviceId
    {
        get => HghighEdition == HongguoClientProfile.StandardEdition ? HghighStandardDeviceId : HghighDeviceId;
        set
        {
            if (HghighEdition == HongguoClientProfile.StandardEdition) HghighStandardDeviceId = value;
            else HghighDeviceId = value;
            OnPropertyChanged();
        }
    }
    public string HghighActiveClientExe
    {
        get => HghighEdition == HongguoClientProfile.StandardEdition ? HghighStandardClientExe : HghighClientExe;
        set
        {
            if (HghighEdition == HongguoClientProfile.StandardEdition) HghighStandardClientExe = value;
            else HghighClientExe = value;
            OnPropertyChanged();
        }
    }
    public string HghighActiveClientVersion
    {
        get => HghighEdition == HongguoClientProfile.StandardEdition ? HghighStandardClientVersion : HghighClientVersion;
        set
        {
            if (HghighEdition == HongguoClientProfile.StandardEdition) HghighStandardClientVersion = value;
            else HghighClientVersion = value;
            OnPropertyChanged();
        }
    }
    private HongguoClientProfile SelectedHghighProfile => HongguoClientProfile.Resolve(HghighEdition);

    [ObservableProperty] private string _hongguoLocalBaseUrl = "";
    [ObservableProperty] private string _hongguoLocalApiKey = "";
    [ObservableProperty] private string _hongguoLocalDownloadMode = "fast";
    [ObservableProperty] private string _hongguoLocalTranscodeEngine = "auto";

    [ObservableProperty] private string _pikachuServerUrl = "https://startvlog.cn/start-prod-api";
    [ObservableProperty] private string _pikachuFanqieCookie = "";
    [ObservableProperty] private string _pikachuDramaType = "short";
    [ObservableProperty] private string _pikachuDeviceId = "";
    [ObservableProperty] private string _pikachuClientVersion = "1.4.4";
    [ObservableProperty] private string _pikachuProbeStatus = "";
    [ObservableProperty] private bool _isPikachuBusy;
    [ObservableProperty] private string _playletServerUrl = PlayletDramaClient.DefaultServerUrl;
    [ObservableProperty] private string _playletDeviceId = "";
    [ObservableProperty] private string _hongguoAppDeviceId = "";
    [ObservableProperty] private string _hongguoAppInstallId = "";
    [ObservableProperty] private string _hongguoAppPlan = "1";
    [ObservableProperty] private bool _hongguoAppStreamLink1 = true;
    [ObservableProperty] private bool _hongguoAppStreamLink2 = true;
    private string _hongguoAppPlaybackLoadedDeviceId = "";
    private bool _hongguoAppStreamLinkGuard;

    public bool HongguoAppPlanIsCurrent
    {
        get => !string.Equals(HongguoAppPlan, "2", StringComparison.Ordinal);
        set
        {
            if (value)
                HongguoAppPlan = "1";
        }
    }

    public bool HongguoAppPlanIsDesktop
    {
        get => string.Equals(HongguoAppPlan, "2", StringComparison.Ordinal);
        set
        {
            if (value)
                HongguoAppPlan = "2";
        }
    }

    partial void OnHongguoAppPlanChanged(string value)
    {
        OnPropertyChanged(nameof(HongguoAppPlanIsCurrent));
        OnPropertyChanged(nameof(HongguoAppPlanIsDesktop));
    }

    partial void OnHongguoAppStreamLink1Changed(bool value)
    {
        if (_hongguoAppStreamLinkGuard || value || HongguoAppStreamLink2)
            return;
        _hongguoAppStreamLinkGuard = true;
        HongguoAppStreamLink1 = true;
        _hongguoAppStreamLinkGuard = false;
    }

    partial void OnHongguoAppStreamLink2Changed(bool value)
    {
        if (_hongguoAppStreamLinkGuard || value || HongguoAppStreamLink1)
            return;
        _hongguoAppStreamLinkGuard = true;
        HongguoAppStreamLink2 = true;
        _hongguoAppStreamLinkGuard = false;
    }
    [ObservableProperty] private string _dramaBoxApiBaseUrl = DramaBoxApiService.DefaultBaseUrl;
    [ObservableProperty] private string _dramaBoxDeviceId = DramaBoxApiService.DefaultDeviceId;
    [ObservableProperty] private string _dramaBoxProbeStatus = "";
    [ObservableProperty] private bool _isDramaBoxBusy;
    [ObservableProperty] private string _playletProbeStatus = "";
    [ObservableProperty] private bool _isPlayletBusy;
    [ObservableProperty] private string _foreignServerUrl = ForeignPlayletClient.DefaultServerUrl;
    [ObservableProperty] private string _foreignDeviceId = "";
    [ObservableProperty] private string _foreignSearchSource = ForeignPlayletClient.DefaultSource;
    [ObservableProperty] private string _foreignLanguage = ForeignPlayletClient.DefaultLanguage;
    [ObservableProperty] private string _foreignProbeStatus = "";
    [ObservableProperty] private bool _isForeignBusy;
    [ObservableProperty] private string _hongguoLocalProbeStatus = "";
    [ObservableProperty] private string _aiTextProbeStatus = "";

    [ObservableProperty] private string _tiktokAsrLocalModelDir = "";
    [ObservableProperty] private string _tiktokAsrLocalVadPath = "";
    [ObservableProperty] private string _tiktokAsrAppId = "";
    [ObservableProperty] private string _tiktokAsrAccessToken = "";
    [ObservableProperty] private int _tiktokMaterialValidateConcurrency = 4;
    [ObservableProperty] private int _tiktokGenerationPerProjectConcurrency = 2;
    [ObservableProperty] private int _tiktokAiTextConcurrency = 3;
    [ObservableProperty] private int _tiktokAsrConcurrency = 2;
    [ObservableProperty] private int _tiktokFfmpegConcurrency = 2;
    [ObservableProperty] private int _tiktokImageGenerationConcurrency = 2;
    [ObservableProperty] private int _tiktokVisualConcurrency = 2;
    [ObservableProperty] private int _tiktokDocumentConcurrency = 2;
    [ObservableProperty] private string _tiktokAsrLanguage = "zh-CN";
    [ObservableProperty] private bool _tiktokManualInterventionOnSingleFailure = true;
    [ObservableProperty] private string _asrProbeStatus = "";

    [ObservableProperty] private string _aiTextEndpoint = ClientSettingsDefaults.AiTextEndpoint;
    [ObservableProperty] private string _aiTextApiKey = "";
    [ObservableProperty] private string _aiTextModel = ClientSettingsDefaults.AiTextModel;
    [ObservableProperty] private int _aiTextTimeoutSeconds = ClientSettingsDefaults.AiTextTimeoutSeconds;
    [ObservableProperty] private string _tiktokRoleReferenceSelectionMode =
        ClientSettingsDefaults.TiktokRoleReferenceSelectionMode;
    [ObservableProperty] private bool _tiktokRoleReferenceAiFallbackEnabled =
        ClientSettingsDefaults.TiktokRoleReferenceAiFallbackEnabled;
    [ObservableProperty] private string _tiktokRoleVectorViewMode = ClientSettingsDefaults.TiktokRoleVectorViewMode;
    [ObservableProperty] private string _aiPublishInfoPrompt = ClientSettingsDefaults.AiPublishInfoPrompt;
    [ObservableProperty] private string _kuaishouPublishInfoPrompt = ClientSettingsDefaults.KuaishouPublishInfoPrompt;
    [ObservableProperty] private string _kuaishouPosterVerticalPrompt = ClientSettingsDefaults.KuaishouPosterVerticalDirectPrompt;
    [ObservableProperty] private string _kuaishouPosterHorizontalPrompt = ClientSettingsDefaults.KuaishouPosterHorizontalDirectPrompt;
    private string _kuaishouPosterVerticalDirectPrompt = ClientSettingsDefaults.KuaishouPosterVerticalDirectPrompt;
    private string _kuaishouPosterVerticalErasePrompt = ClientSettingsDefaults.KuaishouPosterVerticalErasePrompt;
    private string _kuaishouPosterHorizontalDirectPrompt = ClientSettingsDefaults.KuaishouPosterHorizontalDirectPrompt;
    private string _kuaishouPosterHorizontalErasePrompt = ClientSettingsDefaults.KuaishouPosterHorizontalErasePrompt;

    [ObservableProperty] private string _posterMode = ClientSettingsDefaults.PosterMode;
    [ObservableProperty] private string _posterLayoutRequestMode = ClientSettingsDefaults.PosterLayoutRequestMode;
    [ObservableProperty] private string _networkAddressPreference = ClientSettingsDefaults.NetworkAddressPreference;
    [ObservableProperty] private string _imageProvider = ClientSettingsDefaults.ImageProvider;
    [ObservableProperty] private string _imageModelId = ClientSettingsDefaults.ImageModelId;
    [ObservableProperty] private string _imageModelApiKey = "";
    [ObservableProperty] private string _imageModelEndpoint = ClientSettingsDefaults.ImageModelEndpoint;
    [ObservableProperty] private string _doubaoImageResolution = ClientSettingsDefaults.DoubaoImageResolution;
    [ObservableProperty] private string _doubaoImageRatio = ClientSettingsDefaults.DoubaoImageRatio;
    [ObservableProperty] private string _ofoxImage2ModelId = ClientSettingsDefaults.OfoxImage2ModelId;
    [ObservableProperty] private string _ofoxImage2ApiKey = "";
    [ObservableProperty] private string _ofoxImage2Endpoint = ClientSettingsDefaults.OfoxImage2Endpoint;
    [ObservableProperty] private string _ofoxImage2Quality = ClientSettingsDefaults.OfoxImage2Quality;
    [ObservableProperty] private string _ofoxImage2Size = ClientSettingsDefaults.OfoxImage2Size;
    [ObservableProperty] private bool _posterTitleVerifyEnabled = ClientSettingsDefaults.PosterTitleVerifyEnabled;
    [ObservableProperty] private string _posterTitleVerifyMode = ClientSettingsDefaults.PosterTitleVerifyMode;
    [ObservableProperty] private int _posterTitleVerifyAiRetryCount = ClientSettingsDefaults.PosterTitleVerifyAiRetryCount;
    [ObservableProperty] private int _frameExtractEpisodeIndex = ClientSettingsDefaults.FrameExtractEpisodeIndex;
    [ObservableProperty] private double _frameExtractTime = ClientSettingsDefaults.FrameExtractTime;
    [ObservableProperty] private string _frameExtractNeighborOffsetsSeconds = ClientSettingsDefaults.FrameExtractNeighborOffsetsSeconds;
    [ObservableProperty] private string _frameExtractFallbackPercents = ClientSettingsDefaults.FrameExtractFallbackPercents;
    [ObservableProperty] private string _posterAgentPrompt = ClientSettingsDefaults.PosterAgentDirectPrompt;
    private string _posterAgentDirectPrompt = ClientSettingsDefaults.PosterAgentDirectPrompt;
    private string _posterAgentErasePrompt = ClientSettingsDefaults.PosterAgentErasePrompt;
    [ObservableProperty] private string _tiktokProjectImageGenerationMode = ClientSettingsDefaults.TiktokProjectImageGenerationMode;
    [ObservableProperty] private string _tiktokProjectImageTemplateRoot = "";
    [ObservableProperty] private string _tiktokProjectImageTemplateId = ClientSettingsDefaults.TiktokProjectImageTemplateId;
    [ObservableProperty] private int _tiktokProjectImageCount = ClientSettingsDefaults.TiktokProjectImageCount;
    [ObservableProperty] private int _tiktokProjectImageRenderEpisodeLimit = ClientSettingsDefaults.TiktokProjectImageRenderEpisodeLimit;
    [ObservableProperty] private string _tiktokProjectImageSubtitleAiMode = ClientSettingsDefaults.TiktokProjectImageSubtitleAiMode;
    [ObservableProperty] private string _tiktokProjectImageFableCutRoot = ClientSettingsDefaults.TiktokProjectImageFableCutRoot;
    [ObservableProperty] private int _tiktokProjectImageFableCutClipCount = ClientSettingsDefaults.TiktokProjectImageFableCutClipCount;
    [ObservableProperty] private string _tiktokProofTemplateDocxPath = ClientSettingsDefaults.TiktokProofTemplateDocxPath;
    [ObservableProperty] private string _tiktokProofWpsPath = ClientSettingsDefaults.TiktokProofWpsPath;
    [ObservableProperty] private string _tiktokProofDeclarantCompanyName = "";
    [ObservableProperty] private string _tiktokProofSealPath = "";
    [ObservableProperty] private string _tiktokProofPdfRenderer = ClientSettingsDefaults.TiktokProofPdfRenderer;
    [ObservableProperty] private bool _tiktokProofKeepDocx = ClientSettingsDefaults.TiktokProofKeepDocx;
    [ObservableProperty] private bool _tiktokExcelAutoExportEnabled = true;
    [ObservableProperty] private bool _tiktokAccountAnalyticsEnabled = ClientSettingsDefaults.TiktokAccountAnalyticsEnabled;
    [ObservableProperty] private string _tiktokSyncWorkspaceRoot = "";
    [ObservableProperty] private bool _managementDedupEnabled;
    [ObservableProperty] private string _managementDedupScope = "tiktok_username";
    [ObservableProperty] private bool _tiktokAllowOverLimitUploadImport = ClientSettingsDefaults.TiktokAllowOverLimitUploadImport;
    [ObservableProperty] private int _tiktokMinimumEpisodeCount = ClientSettingsDefaults.TiktokMinimumEpisodeCount;
    [ObservableProperty] private int _tiktokMaximumEpisodeCount = ClientSettingsDefaults.TiktokMaximumEpisodeCount;

    public IReadOnlyList<string> DramaSourceOptions { get; } =
    [
        "hghigh",
        "hglocal",
        "pikachu",
        "playlet",
        "hongguoapp",
        "dramabox"
    ];

    public IReadOnlyList<string> PikachuDramaTypeOptions { get; } = ["short", "manga"];
    public IReadOnlyList<string> PosterModeOptions { get; } = [ClientSettingsDefaults.PosterMode];
    public IReadOnlyList<string> ImageProviderOptions { get; } = ["doubao", "ofox_image2"];
    public IReadOnlyList<string> PosterTitleVerifyModeOptions { get; } = ["fallback_repaint", "warn", "blocking"];
    public IReadOnlyList<string> ProjectImageGenerationModeOptions { get; } = ["image_template", "fablecut"];
    public IReadOnlyList<string> ProjectImageSubtitleAiModeOptions { get; } = ["fast", "accurate", "off"];
    public IReadOnlyList<string> ManagementDedupScopeOptions { get; } = ["tiktok_username", "software_user", "all_series"];

    public bool IsProjectImageTemplateMode => !IsProjectImageFableCutMode;
    public bool IsProjectImageFableCutMode => string.Equals(
        TiktokProjectImageGenerationMode,
        "fablecut",
        StringComparison.OrdinalIgnoreCase);

    partial void OnTiktokProjectImageGenerationModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsProjectImageTemplateMode));
        OnPropertyChanged(nameof(IsProjectImageFableCutMode));
    }

    public void Load(string? workspacePath = null)
    {
        ApplyFromSettings(ClientSettingsStore.Load(_settingsDatabasePath));
        MainDatabasePath = _settingsDatabasePath ?? ClientSettingsStore.MainDatabasePath;
        WorkspaceDatabasePath = ClientSettingsStore.WorkspaceDatabasePath(workspacePath);
        RefreshDatabaseStats();
    }

    private int _hongguoPlaybackIdentityStarted;

    /// <summary>
    /// 第一次打开助手时，若还没有成对的播放设备号和安装号，向红果注册一对并写入设置。
    /// </summary>
    public void BeginHongguoAppPlaybackIdentity()
    {
        if (HongguoAppPlaybackIdentity.IsComplete(HongguoAppDeviceId, HongguoAppInstallId))
            return;
        if (Interlocked.CompareExchange(ref _hongguoPlaybackIdentityStarted, 1, 0) != 0)
            return;
        _ = RegisterHongguoAppPlaybackIdentityAsync();
    }

    private async Task RegisterHongguoAppPlaybackIdentityAsync()
    {
        try
        {
            var loaded = ClientSettingsStore.Load(_settingsDatabasePath);
            if (HongguoAppPlaybackIdentity.IsComplete(loaded.HongguoAppDeviceId, loaded.HongguoAppInstallId))
            {
                PublishHongguoAppPlaybackIdentity(loaded.HongguoAppDeviceId, loaded.HongguoAppInstallId);
                return;
            }

            var created = await HongguoAppPlaybackIdentity.RegisterAsync(ProbeHttp, CancellationToken.None);
            if (!HongguoAppPlaybackIdentity.IsComplete(created.DeviceId, created.InstallId))
                return;

            ClientSettingsStore.PatchHongguoAppPlaybackIdentity(
                created.DeviceId,
                created.InstallId,
                _settingsDatabasePath);
            PublishHongguoAppPlaybackIdentity(created.DeviceId, created.InstallId);
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _hongguoPlaybackIdentityStarted, 0);
        }
    }

    private void PublishHongguoAppPlaybackIdentity(string deviceId, string installId)
    {
        void Apply()
        {
            if (!string.IsNullOrWhiteSpace(HongguoAppDeviceId) || !string.IsNullOrWhiteSpace(HongguoAppInstallId))
                return;
            HongguoAppDeviceId = deviceId;
            HongguoAppInstallId = installId;
            _hongguoAppPlaybackLoadedDeviceId = deviceId;
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    public ClientSettings ToSettings() => ApplySystemSettingsForm(new ClientSettings());

    /// <summary>
    /// 只写入系统设置页上的字段。必须落在已加载的配置上，
    /// 否则未出现在本页的 XINGE、飞书和授权字段会被默认空值覆盖。
    /// </summary>
    private ClientSettings ApplySystemSettingsForm(ClientSettings settings)
    {
        settings.DramaSourceChain = DramaSourceChain;
        settings.DramaDownloadDefaultQuality = DramaDownloadDefaultQuality;
        settings.DramaDownloadConcurrent = DramaDownloadConcurrent;
        settings.DramaDownloadMaxParallelProjects = DramaDownloadMaxParallelProjects;
        settings.DownloadFileSegments = DownloadFileSegments;
        settings.DownloaderApiBaseUrl = DownloaderApiBaseUrl.Trim();
        settings.DownloaderApiKey = DownloaderApiKey.Trim();
        settings.HongguoDownloadTimeoutSeconds = HongguoDownloadTimeoutSeconds;
        settings.HongguoEpisodeDownloadAttempts = HongguoEpisodeDownloadAttempts;
        settings.HgnewAccount = HgnewAccount.Trim();
        settings.HgnewPassword = HgnewPassword;
        settings.HgnewUdid = ClientSettingsStore.NormalizeUdid(HgnewUdid);
        settings.HgnewClientVersion = HongguoClientVersion.Normalize(HgnewClientVersion);
        settings.HghighAccount = HghighAccount.Trim();
        settings.HghighPassword = HghighPassword;
        settings.HghighEdition = HongguoClientProfile.StandardEdition;
        settings.HghighDeviceId = HghighDeviceId.Trim();
        settings.HghighClientExe = HghighClientExe.Trim();
        settings.HghighClientVersion = NormalizeHghighClientVersion(HghighClientVersion, HongguoClientProfile.High.ClientVersion);
        settings.HghighStandardDeviceId = HghighStandardDeviceId.Trim();
        settings.HghighStandardClientExe = HghighStandardClientExe.Trim();
        settings.HghighStandardClientVersion = NormalizeHghighClientVersion(HghighStandardClientVersion, HongguoClientProfile.Standard.ClientVersion);
        settings.HghighStandardPlaybackMode = HongguoClientProfile.NormalizeStandardPlaybackMode(HghighStandardPlaybackMode);
        settings.HongguoAppDeviceId = HongguoAppDeviceId.Trim();
        settings.HongguoAppInstallId = HongguoAppInstallId.Trim();
        settings.HongguoAppPlan = ClientSettings.NormalizeHongguoAppPlan(HongguoAppPlan);
        settings.HongguoAppStreamLink1 = HongguoAppStreamLink1;
        settings.HongguoAppStreamLink2 = HongguoAppStreamLink2;
        if (!settings.HongguoAppStreamLink1 && !settings.HongguoAppStreamLink2)
        {
            settings.HongguoAppStreamLink1 = true;
            settings.HongguoAppStreamLink2 = true;
        }
        settings.MapleleafAccount = MapleleafAccount.Trim();
        settings.MapleleafPassword = MapleleafPassword;
        settings.MapleleafUdid = MapleleafUdid.Trim();
        settings.HongguoLocalBaseUrl = HongguoLocalBaseUrl.Trim();
        settings.HongguoLocalApiKey = HongguoLocalApiKey.Trim();
        settings.HongguoLocalDownloadMode = NormalizeHongguoLocalDownloadMode(HongguoLocalDownloadMode);
        settings.HongguoLocalTranscodeEngine = NormalizeHongguoLocalTranscodeEngine(HongguoLocalTranscodeEngine);
        settings.PikachuServerUrl = PikachuServerUrl.Trim();
        settings.PikachuFanqieCookie = PikachuFanqieCookie.Trim();
        settings.PikachuDramaType = NormalizePikachuDramaType(PikachuDramaType);
        settings.PikachuDeviceId = PikachuDeviceId.Trim();
        settings.PikachuClientVersion = string.IsNullOrWhiteSpace(PikachuClientVersion) ? "1.4.4" : PikachuClientVersion.Trim();
        settings.PlayletServerUrl = string.IsNullOrWhiteSpace(PlayletServerUrl) ? PlayletDramaClient.DefaultServerUrl : PlayletServerUrl.Trim();
        settings.PlayletDeviceId = PlayletDeviceId.Trim().ToUpperInvariant();
        settings.ForeignServerUrl = string.IsNullOrWhiteSpace(ForeignServerUrl) ? ForeignPlayletClient.DefaultServerUrl : ForeignServerUrl.Trim();
        settings.ForeignDeviceId = ForeignDeviceId.Trim().ToUpperInvariant();
        settings.ForeignSearchSource = ForeignPlayletClient.NormalizeSource(ForeignSearchSource);
        settings.ForeignLanguage = ForeignPlayletClient.NormalizeLanguage(ForeignLanguage);
        settings.DramaBoxApiBaseUrl = string.IsNullOrWhiteSpace(DramaBoxApiBaseUrl) ? DramaBoxApiService.DefaultBaseUrl : DramaBoxApiBaseUrl.Trim().TrimEnd('/');
        settings.DramaBoxDeviceId = string.IsNullOrWhiteSpace(DramaBoxDeviceId) ? DramaBoxApiService.DefaultDeviceId : DramaBoxDeviceId.Trim();
        settings.TiktokAsrLocalModelDir = TiktokAsrLocalModelDir.Trim();
        settings.TiktokAsrLocalVadPath = TiktokAsrLocalVadPath.Trim();
        settings.TiktokAsrAppId = TiktokAsrAppId.Trim();
        settings.TiktokAsrAccessToken = TiktokAsrAccessToken;
        settings.TiktokMaterialValidateConcurrency = TiktokMaterialValidateConcurrency;
        settings.TiktokGenerationPerProjectConcurrency = TiktokGenerationPerProjectConcurrency;
        settings.TiktokAiTextConcurrency = TiktokAiTextConcurrency;
        settings.TiktokAsrConcurrency = TiktokAsrConcurrency;
        settings.TiktokFfmpegConcurrency = TiktokFfmpegConcurrency;
        settings.TiktokImageGenerationConcurrency = TiktokImageGenerationConcurrency;
        settings.TiktokVisualConcurrency = TiktokVisualConcurrency;
        settings.TiktokDocumentConcurrency = TiktokDocumentConcurrency;
        settings.TiktokAsrLanguage = TiktokAsrLanguage.Trim();
        settings.TiktokManualInterventionOnSingleFailure = TiktokManualInterventionOnSingleFailure;
        settings.AiTextEndpoint = AiTextEndpoint.Trim();
        settings.AiTextApiKey = AiTextApiKey;
        settings.AiTextModel = AiTextModel.Trim();
        settings.AiTextTimeoutSeconds = AiTextTimeoutSeconds;
        settings.TiktokRoleReferenceSelectionMode = TiktokRoleReferenceSelectionMode;
        settings.TiktokRoleReferenceAiFallbackEnabled = TiktokRoleReferenceAiFallbackEnabled;
        settings.TiktokRoleVectorViewMode = TiktokRoleVectorViewMode;
        settings.AiPublishInfoPrompt = AiPublishInfoPrompt;
        settings.KuaishouPublishInfoPrompt = KuaishouPublishInfoPrompt;
        settings.PosterMode = PosterMode;
        settings.PosterLayoutRequestMode = PosterLayoutRequestMode;
        settings.NetworkAddressPreference = NetworkAddressPreference;
        settings.ImageProvider = ImageProvider;
        settings.ImageModelId = ClientSettingsDefaults.NormalizeImageModelId(ImageModelId);
        settings.ImageModelApiKey = ImageModelApiKey;
        settings.ImageModelEndpoint = ImageModelEndpoint.Trim();
        settings.DoubaoImageResolution = PosterImageConfigHelper.NormalizeDoubaoImageResolution(DoubaoImageResolution);
        settings.DoubaoImageRatio = DoubaoImageRatio;
        settings.OfoxImage2ModelId = OfoxImage2ModelId.Trim();
        settings.OfoxImage2ApiKey = OfoxImage2ApiKey;
        settings.OfoxImage2Endpoint = OfoxImage2Endpoint.Trim();
        settings.OfoxImage2Quality = OfoxImage2Quality;
        settings.OfoxImage2Size = OfoxImage2Size.Trim();
        settings.PosterTitleVerifyEnabled = PosterTitleVerifyEnabled;
        settings.PosterTitleVerifyMode = PosterTitleVerifyMode;
        settings.PosterTitleVerifyAiRetryCount = PosterTitleVerifyAiRetryCount;
        settings.FrameExtractEpisodeIndex = FrameExtractEpisodeIndex;
        settings.FrameExtractTime = FrameExtractTime;
        settings.FrameExtractNeighborOffsetsSeconds = FrameExtractNeighborOffsetsSeconds;
        settings.FrameExtractFallbackPercents = FrameExtractFallbackPercents;
        settings.PosterAgentDirectPrompt = ClientSettingsStore.StoredPosterAgentPrompt(
            _posterAgentDirectPrompt,
            ClientSettingsDefaults.PosterModeDirect);
        settings.PosterAgentErasePrompt = ClientSettingsStore.StoredPosterAgentPrompt(
            _posterAgentErasePrompt,
            ClientSettingsDefaults.PosterModeEraseFixedTitle);
        settings.KuaishouPosterVerticalDirectPrompt = ClientSettingsStore.StoredKuaishouPosterPrompt(
            _kuaishouPosterVerticalDirectPrompt,
            horizontal: false,
            ClientSettingsDefaults.PosterModeDirect);
        settings.KuaishouPosterVerticalErasePrompt = ClientSettingsStore.StoredKuaishouPosterPrompt(
            _kuaishouPosterVerticalErasePrompt,
            horizontal: false,
            ClientSettingsDefaults.PosterModeEraseFixedTitle);
        settings.KuaishouPosterHorizontalDirectPrompt = ClientSettingsStore.StoredKuaishouPosterPrompt(
            _kuaishouPosterHorizontalDirectPrompt,
            horizontal: true,
            ClientSettingsDefaults.PosterModeDirect);
        settings.KuaishouPosterHorizontalErasePrompt = ClientSettingsStore.StoredKuaishouPosterPrompt(
            _kuaishouPosterHorizontalErasePrompt,
            horizontal: true,
            ClientSettingsDefaults.PosterModeEraseFixedTitle);
        settings.TiktokProjectImageGenerationMode = TiktokProjectImageGenerationMode;
        settings.TiktokProjectImageTemplateRoot = TiktokProjectImageTemplateRoot.Trim();
        settings.TiktokProjectImageTemplateId = TiktokProjectImageTemplateId.Trim();
        settings.TiktokProjectImageCount = TiktokProjectImageCount;
        settings.TiktokProjectImageRenderEpisodeLimit = TiktokProjectImageRenderEpisodeLimit;
        settings.TiktokProjectImageSubtitleAiMode = TiktokProjectImageSubtitleAiMode;
        settings.TiktokProjectImageFableCutRoot = TiktokProjectImageFableCutRoot.Trim();
        settings.TiktokProjectImageFableCutClipCount = Math.Clamp(TiktokProjectImageFableCutClipCount, 12, 36);
        settings.TiktokProofTemplateDocxPath = TiktokProofTemplateDocxPath.Trim();
        settings.TiktokProofWpsPath = TiktokProofWpsPath.Trim();
        settings.TiktokProofDeclarantCompanyName = TiktokProofDeclarantCompanyName.Trim();
        settings.TiktokProofSealPath = TiktokProofSealPath.Trim();
        settings.TiktokProofPdfRenderer = TiktokProofPdfRenderer;
        settings.TiktokProofKeepDocx = TiktokProofKeepDocx;
        settings.TiktokExcelAutoExportEnabled = TiktokExcelAutoExportEnabled;
        settings.TiktokAccountAnalyticsEnabled = TiktokAccountAnalyticsEnabled;
        settings.TiktokSyncWorkspaceRoot = TiktokSyncWorkspaceRoot.Trim();
        settings.ManagementDedupEnabled = ManagementDedupEnabled;
        settings.ManagementDedupScope = ManagementDedupScope;
        settings.TiktokAllowOverLimitUploadImport = TiktokAllowOverLimitUploadImport;
        settings.TiktokMinimumEpisodeCount = TiktokMinimumEpisodeCount;
        settings.TiktokMaximumEpisodeCount = TiktokMaximumEpisodeCount;
        settings.TiktokOverLimitDownloadEpisodeCount = TiktokMaximumEpisodeCount;
        return settings;
    }

    [RelayCommand]
    private async Task ReadPikachuCookieAsync()
    {
        if (IsPikachuBusy)
        {
            PikachuProbeStatus = "正在从红果读取 Cookie，请稍候。";
            return;
        }

        IsPikachuBusy = true;
        PikachuProbeStatus = "正在从红果读取...";
        try
        {
            var reader = new HongguoMemoryReaderService();
            var result = await reader.ReadRuntimeAsync(CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(result.FanqieCookie))
            {
                PikachuFanqieCookie = result.FanqieCookie;
                if (!string.IsNullOrWhiteSpace(result.DeviceId))
                {
                    PikachuDeviceId = result.DeviceId;
                }

                ClientSettingsStore.PatchPikachuRuntimeFields(
                    result.FanqieCookie,
                    result.DeviceId,
                    _settingsDatabasePath);
                PikachuProbeStatus = "已从红果客户端读取番茄 Cookie，保存设置后即可生效。";
                StatusRequested?.Invoke("已从红果读取番茄 Cookie");
                return;
            }

            PikachuProbeStatus = MapHongguoReadFailure(result.Reason);
        }
        catch (Exception ex)
        {
            PikachuProbeStatus = $"读取失败：{ex.Message}";
        }
        finally
        {
            IsPikachuBusy = false;
        }
    }

    [RelayCommand]
    private async Task ProbePikachuConnectivityAsync()
    {
        if (IsPikachuBusy)
        {
            return;
        }

        IsPikachuBusy = true;
        PikachuProbeStatus = "测试中...";
        try
        {
            var result = await PikachuDramaClient.TestConnectivityAsync(
                ProbeHttp,
                PikachuServerUrl,
                PikachuFanqieCookie,
                PikachuDramaType,
                PikachuDeviceId,
                PikachuClientVersion,
                timeoutSeconds: 15,
                cancellationToken: CancellationToken.None);

            var lines = new List<string>
            {
                $"番茄搜索：{result.SearchMessage}",
                $"皮卡丘服务：{result.DetailMessage}",
            };
            PikachuProbeStatus = string.Join(Environment.NewLine, lines);
            StatusRequested?.Invoke(result.SearchOk && result.DetailOk
                ? "皮卡丘链路测试成功"
                : "皮卡丘链路测试完成（部分失败）");
        }
        catch (Exception ex)
        {
            PikachuProbeStatus = $"测试失败：{ex.Message}";
            StatusRequested?.Invoke(PikachuProbeStatus);
        }
        finally
        {
            IsPikachuBusy = false;
        }
    }

    [RelayCommand]
    private async Task ProbeDramaBoxConnectivityAsync()
    {
        if (IsDramaBoxBusy)
            return;

        IsDramaBoxBusy = true;
        DramaBoxProbeStatus = "测试中...";
        try
        {
            var result = await new DramaBoxApiService(ProbeHttp).TestConnectivityAsync(DramaBoxDeviceId, CancellationToken.None);
            DramaBoxProbeStatus = string.Join(Environment.NewLine, new[]
            {
                $"授权：{result.LicenseMessage}",
                $"搜索：{result.SearchMessage}",
                $"下载：{result.DownloadMessage}",
            });
            StatusRequested?.Invoke(result.LicenseOk && result.SearchOk && result.DownloadOk
                ? "DramaBox 测试成功"
                : "DramaBox 测试完成（部分失败）");
        }
        catch (Exception ex)
        {
            DramaBoxProbeStatus = $"测试失败：{ex.Message}";
            StatusRequested?.Invoke(DramaBoxProbeStatus);
        }
        finally
        {
            IsDramaBoxBusy = false;
        }
    }

    [RelayCommand]
    private async Task ProbePlayletConnectivityAsync()
    {
        if (IsPlayletBusy)
        {
            return;
        }

        IsPlayletBusy = true;
        PlayletProbeStatus = "测试中...";
        try
        {
            var result = await PlayletDramaClient.TestConnectivityAsync(
                ProbeHttp,
                PlayletServerUrl,
                PikachuDramaType,
                PlayletDeviceId,
                CancellationToken.None);
            PlayletProbeStatus = $"搜索：{result.SearchMessage}{Environment.NewLine}下载：{result.VideoMessage}";
            StatusRequested?.Invoke(result.SearchOk && result.VideoOk
                ? "红果短剧下载器测试成功"
                : "红果短剧下载器测试完成（部分失败）");
        }
        catch (Exception ex)
        {
            PlayletProbeStatus = $"测试失败：{ex.Message}";
            StatusRequested?.Invoke(PlayletProbeStatus);
        }
        finally
        {
            IsPlayletBusy = false;
        }
    }

    [RelayCommand]
    private async Task ProbeForeignConnectivityAsync()
    {
        if (IsForeignBusy)
            return;

        IsForeignBusy = true;
        ForeignProbeStatus = "测试中...";
        try
        {
            var result = await ForeignPlayletClient.TestConnectivityAsync(
                ProbeHttp,
                ForeignServerUrl,
                ForeignDeviceId,
                ForeignSearchSource,
                ForeignLanguage,
                CancellationToken.None);
            ForeignProbeStatus = $"会员：{result.MemberMessage}{Environment.NewLine}搜索：{result.SearchMessage}{Environment.NewLine}下载：{result.VideoMessage}";
            StatusRequested?.Invoke(result.MemberOk && result.SearchOk && result.VideoOk
                ? "海外短剧下载器测试成功"
                : "海外短剧下载器测试完成（部分失败）");
        }
        catch (Exception ex)
        {
            ForeignProbeStatus = $"测试失败：{ex.Message}";
            StatusRequested?.Invoke(ForeignProbeStatus);
        }
        finally
        {
            IsForeignBusy = false;
        }
    }

    private static string MapHongguoReadFailure(string? reason) =>
        reason switch
        {
            "process_not_found" => "未检测到红果客户端下载器进程，请先启动并登录红果客户端。",
            "fanqie_cookie_not_found" or "runtime_values_not_found" =>
                "已找到红果客户端，但 Cookie 还未进入内存。请先在红果内执行一次搜索后重试。",
            "not_windows" => "从红果读取 Cookie 仅支持 Windows 平台。",
            _ => string.IsNullOrWhiteSpace(reason) ? "读取 Cookie 失败。" : reason,
        };

    [RelayCommand]
    private void SaveSettings()
    {
        try
        {
            var loaded = ClientSettingsStore.Load(_settingsDatabasePath);
            var storedDeviceId = loaded.HongguoAppDeviceId;
            var storedInstallId = loaded.HongguoAppInstallId;
            var settings = ApplySystemSettingsForm(loaded);
            if (string.IsNullOrWhiteSpace(HongguoAppDeviceId) &&
                string.IsNullOrWhiteSpace(HongguoAppInstallId) &&
                string.IsNullOrWhiteSpace(_hongguoAppPlaybackLoadedDeviceId) &&
                !string.IsNullOrWhiteSpace(storedDeviceId) &&
                !string.IsNullOrWhiteSpace(storedInstallId))
            {
                settings.HongguoAppDeviceId = storedDeviceId;
                settings.HongguoAppInstallId = storedInstallId;
            }
            PersistHghighMastersFromForm();
            ClientSettingsStore.Save(settings, _settingsDatabasePath);
            ApplyFromSettings(settings);
            SaveMessage = "系统设置已保存。";
            SettingsSaved?.Invoke(settings);
            StatusRequested?.Invoke("系统设置已保存");
        }
        catch (Exception ex)
        {
            SaveMessage = $"保存失败：{ex.Message}";
            StatusRequested?.Invoke(SaveMessage);
        }
    }

    [RelayCommand]
    private void ResetAiPublishInfoPrompt() => AiPublishInfoPrompt = ClientSettingsDefaults.AiPublishInfoPrompt;

    [RelayCommand]
    private void ResetKuaishouPublishInfoPrompt() =>
        KuaishouPublishInfoPrompt = ClientSettingsDefaults.KuaishouPublishInfoPrompt;

    [RelayCommand]
    private void ResetPosterAgentPrompt() =>
        PosterAgentPrompt = ClientSettingsDefaults.DefaultPosterAgentPrompt(PosterMode);

    [RelayCommand]
    private void ResetKuaishouPosterVerticalPrompt() =>
        KuaishouPosterVerticalPrompt = ClientSettingsDefaults.DefaultKuaishouPosterPrompt(horizontal: false, PosterMode);

    [RelayCommand]
    private void ResetKuaishouPosterHorizontalPrompt() =>
        KuaishouPosterHorizontalPrompt = ClientSettingsDefaults.DefaultKuaishouPosterPrompt(horizontal: true, PosterMode);

    partial void OnPosterModeChanged(string value)
    {
        ShowPosterAgentPromptForCurrentMode();
        ShowKuaishouPosterPromptsForCurrentMode();
    }

    partial void OnKuaishouPosterVerticalPromptChanged(string value)
    {
        if (ClientSettingsDefaults.IsEraseFixedTitlePosterMode(PosterMode))
            _kuaishouPosterVerticalErasePrompt = value;
        else
            _kuaishouPosterVerticalDirectPrompt = value;
    }

    partial void OnKuaishouPosterHorizontalPromptChanged(string value)
    {
        if (ClientSettingsDefaults.IsEraseFixedTitlePosterMode(PosterMode))
            _kuaishouPosterHorizontalErasePrompt = value;
        else
            _kuaishouPosterHorizontalDirectPrompt = value;
    }

    private void ShowKuaishouPosterPromptsForCurrentMode()
    {
        var erase = ClientSettingsDefaults.IsEraseFixedTitlePosterMode(PosterMode);
        KuaishouPosterVerticalPrompt = erase ? _kuaishouPosterVerticalErasePrompt : _kuaishouPosterVerticalDirectPrompt;
        KuaishouPosterHorizontalPrompt = erase ? _kuaishouPosterHorizontalErasePrompt : _kuaishouPosterHorizontalDirectPrompt;
    }

    partial void OnPosterAgentPromptChanged(string value)
    {
        if (ClientSettingsDefaults.IsEraseFixedTitlePosterMode(PosterMode))
            _posterAgentErasePrompt = value;
        else
            _posterAgentDirectPrompt = value;
    }

    private void ShowPosterAgentPromptForCurrentMode() =>
        PosterAgentPrompt = ClientSettingsDefaults.IsEraseFixedTitlePosterMode(PosterMode)
            ? _posterAgentErasePrompt
            : _posterAgentDirectPrompt;

    [RelayCommand]
    private void ReadHgnewUdid()
    {
        if (HongguoDeviceUdidHelper.TryReadFromRegistry(out var udid, out var message))
        {
            HgnewUdid = udid;
        }

        HgnewProbeStatus = message;
    }

    [RelayCommand]
    private void GenerateHgnewUdid()
    {
        HgnewUdid = Guid.NewGuid().ToString().ToUpperInvariant();
        HgnewProbeStatus = "已生成新的设备唯一标识。";
    }

    [RelayCommand]
    private async Task ProbeHgnewLoginAsync()
    {
        HgnewUdid = HongguoDeviceId.ResolveV14(HgnewUdid);
        if (string.IsNullOrWhiteSpace(HgnewAccount) || string.IsNullOrWhiteSpace(HgnewPassword))
        {
            HgnewProbeStatus = "请先填写红果账号和红果密码。";
            return;
        }

        if (string.IsNullOrWhiteSpace(HgnewUdid))
        {
            HgnewProbeStatus = "请先读取或生成红果新接口设备唯一标识。";
            return;
        }

        HgnewProbeStatus = "测试中...";
        try
        {
            var result = await HongguoNewLoginClient.ProbeLoginAsync(
                ProbeHttp,
                HgnewAccount.Trim(),
                HgnewPassword,
                HgnewUdid,
                HgnewClientVersion,
                HongguoDownloadTimeoutSeconds,
                CancellationToken.None);
            HgnewProbeStatus = $"测试登录成功：{DateTime.Now:HH:mm:ss}";
            HgnewLoginProbeSucceeded?.Invoke(result);
        }
        catch (HongguoLoginException ex)
        {
            HgnewProbeStatus = $"测试登录失败：{ex.Message}";
        }
        catch (Exception ex)
        {
            HgnewProbeStatus = $"测试登录异常：{ex.GetType().Name}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ReadHghighDeviceId()
    {
        var profile = SelectedHghighProfile;
        var deviceId = HongguoHighDeviceStore.TryReadDeviceId(profile);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            HghighProbeStatus = $"未读到{profile.DisplayName} DeviceId。请先安装并登录一次对应的官方 HG 客户端。";
            return;
        }

        HghighActiveDeviceId = deviceId;
        HghighProbeStatus = $"已从本机官方{profile.DisplayName}客户端读取 DeviceId。";
    }

    [RelayCommand]
    private async Task ProbeHghighLoginAsync()
    {
        if (string.IsNullOrWhiteSpace(HghighAccount) || string.IsNullOrWhiteSpace(HghighPassword))
        {
            HghighProbeStatus = "请先填写红果短剧助手账号和密码。";
            return;
        }

        if (!HongguoHighDeviceStore.IsReady(SelectedHghighProfile))
        {
            HghighProbeStatus = "本机还没有启动密钥。请先选择官方客户端 exe，再点「提取启动密钥」。";
            return;
        }

        HghighProbeStatus = "测试中...";
        try
        {
            var settings = DramaSourceSettingsMapping.FromClientSettings(ToSettings());
            var service = new HongguoHighApiService(ProbeHttp);
            var result = await service.ProbeLoginAsync(settings, CancellationToken.None);
            var preview = result.Token.Length > 12 ? $"{result.Token[..6]}…{result.Token[^4..]}" : result.Token;
            HghighProbeStatus = $"测试登录成功：{DateTime.Now:HH:mm:ss} token={preview}";
        }
        catch (Exception ex)
        {
            HghighProbeStatus = $"测试登录失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ProvisionHghighMastersAsync()
    {
        if (IsHghighBusy)
        {
            HghighMastersStatus = "正在提取启动密钥，请稍候。";
            return;
        }

        IsHghighBusy = true;
        var profile = SelectedHghighProfile;
        HghighMastersStatus = $"正在关闭已运行的官方{profile.DisplayName}客户端并提取密钥…";
        try
        {
            var progress = new Progress<string>(message => HghighMastersStatus = message);
            var result = await HongguoHighMasterProvisioner.ExtractAsync(
                HghighActiveClientExe,
                profile,
                progress,
                CancellationToken.None);
            if (string.IsNullOrWhiteSpace(HghighActiveClientExe))
            {
                HghighActiveClientExe = HongguoHighDeviceStore.FindOfficialClientExe(null, profile) ?? "";
            }

            if (string.IsNullOrWhiteSpace(HghighActiveDeviceId))
            {
                HghighActiveDeviceId = result.DeviceId;
            }

            ShowExtractedMasters(result.EncMaster, result.SignMaster);
            HghighMastersStatus = "已提取 Enc Master 和 Sign Master，已填入上方输入框。请点「保存设置」写入本机。";
            HghighProbeStatus = string.IsNullOrWhiteSpace(result.DeviceId)
                ? "启动密钥已填入上方输入框。"
                : $"启动密钥已填入上方输入框（设备 {result.DeviceId}）。";
        }
        catch (Exception ex)
        {
            RefreshHghighMastersStatus();
            HghighMastersStatus = $"提取失败：{ex.Message}";
        }
        finally
        {
            IsHghighBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleHghighRevealEnc() => HghighRevealEnc = !HghighRevealEnc;

    [RelayCommand]
    private void ToggleHghighRevealSign() => HghighRevealSign = !HghighRevealSign;

    [RelayCommand]
    private Task CopyHghighEncMasterAsync() => CopyMasterAsync(HghighEncMaster, "Enc Master");

    [RelayCommand]
    private Task CopyHghighSignMasterAsync() => CopyMasterAsync(HghighSignMaster, "Sign Master");

    private async Task CopyMasterAsync(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            HghighMastersStatus = $"{label} 为空，无法复制。";
            return;
        }

        if (CopyToClipboardAsync is null)
        {
            HghighMastersStatus = "当前窗口无法访问剪贴板。";
            return;
        }

        try
        {
            await CopyToClipboardAsync(value);
            HghighMastersStatus = $"{label} 已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            HghighMastersStatus = $"复制失败：{ex.Message}";
        }
    }

    partial void OnHghighRevealEncChanged(bool value) => OnPropertyChanged(nameof(HghighRevealEncButtonText));

    partial void OnHghighRevealSignChanged(bool value) => OnPropertyChanged(nameof(HghighRevealSignButtonText));

    partial void OnHghighEditionChanged(string value)
    {
        var normalized = HongguoClientProfile.NormalizeEdition(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            HghighEdition = normalized;
            return;
        }

        OnPropertyChanged(nameof(HghighEditionLabel));
        OnPropertyChanged(nameof(HghighDeviceLabel));
        OnPropertyChanged(nameof(HghighClientExeLabel));
        OnPropertyChanged(nameof(IsHghighStandardEdition));
        OnPropertyChanged(nameof(HghighActiveDeviceId));
        OnPropertyChanged(nameof(HghighActiveClientExe));
        OnPropertyChanged(nameof(HghighClientVersionLabel));
        OnPropertyChanged(nameof(HghighActiveClientVersion));
        var masters = HongguoHighDeviceStore.LoadStartupMastersRaw(SelectedHghighProfile);
        ApplyMastersToForm(masters.Enc, masters.Sign);
        RefreshHghighMastersStatus();
    }

    partial void OnHghighDeviceIdChanged(string value) => OnPropertyChanged(nameof(HghighActiveDeviceId));
    partial void OnHghighStandardDeviceIdChanged(string value) => OnPropertyChanged(nameof(HghighActiveDeviceId));
    partial void OnHghighClientExeChanged(string value) => OnPropertyChanged(nameof(HghighActiveClientExe));
    partial void OnHghighStandardClientExeChanged(string value) => OnPropertyChanged(nameof(HghighActiveClientExe));
    partial void OnHghighClientVersionChanged(string value) => OnPropertyChanged(nameof(HghighActiveClientVersion));
    partial void OnHghighStandardClientVersionChanged(string value) => OnPropertyChanged(nameof(HghighActiveClientVersion));

    private void ShowExtractedMasters(string enc, string sign)
    {
        HghighRevealEnc = false;
        HghighRevealSign = false;
        ApplyMastersToForm(enc, sign);
        var capturedEnc = enc ?? "";
        var capturedSign = sign ?? "";
        Dispatcher.UIThread.Post(
            () =>
            {
                HghighRevealEnc = false;
                HghighRevealSign = false;
                ApplyMastersToForm(capturedEnc, capturedSign);
            },
            DispatcherPriority.Background);
    }

    private void ApplyMastersToForm(string enc, string sign)
    {
        foreach (var step in BoundPasswordAssigner.AssignmentSteps(HghighEncMaster, enc))
        {
            HghighEncMaster = step;
        }

        foreach (var step in BoundPasswordAssigner.AssignmentSteps(HghighSignMaster, sign))
        {
            HghighSignMaster = step;
        }
    }

    private void PersistHghighMastersFromForm()
    {
        if (string.IsNullOrWhiteSpace(HghighEncMaster) || string.IsNullOrWhiteSpace(HghighSignMaster))
        {
            HongguoHighDeviceStore.ClearStartupMasters(SelectedHghighProfile);
            return;
        }

        if (string.IsNullOrWhiteSpace(HghighActiveDeviceId))
        {
            HghighActiveDeviceId = HongguoHighDeviceStore.TryReadDeviceId(SelectedHghighProfile);
        }

        HongguoHighDeviceStore.CacheStartupMasters(
            HghighEncMaster,
            HghighSignMaster,
            HghighActiveDeviceId,
            SelectedHghighProfile);
    }

    private void RefreshHghighMastersStatus()
    {
        if (HongguoHighDeviceStore.IsReady(SelectedHghighProfile))
        {
            HghighMastersStatus = "本机已缓存启动密钥，可正常登录。";
            return;
        }

        var (enc, sign) = HongguoHighDeviceStore.LoadStartupMastersRaw(SelectedHghighProfile);
        if (!string.IsNullOrWhiteSpace(enc) && !string.IsNullOrWhiteSpace(sign))
        {
            HghighMastersStatus = $"已有共享启动密钥缓存，但尚未读取到本机{HghighEditionLabel}设备身份。请先运行对应客户端一次。";
            return;
        }

        HghighMastersStatus = "尚未缓存启动密钥。安装包已内置 Frida，选择官方客户端后点「提取启动密钥」。";
    }

    [RelayCommand]
    private async Task ProbeDownloaderAsync()
    {
        DownloaderProbeStatus = "连接中...";
        try
        {
            var service = new DownloaderGatewayApiService(ProbeHttp);
            var health = await service.GetHealthAsync(
                DramaSourceSettingsMapping.FromClientSettings(ToSettings()), CancellationToken.None);
            DownloaderProbeStatus = health.Ok
                ? $"已连接：来源 {health.ActiveSource}，红果版本 {health.HighEdition}"
                : "下载器返回未就绪状态。";
        }
        catch (Exception ex)
        {
            DownloaderProbeStatus = $"连接失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ProbeHongguoLocalAsync()
    {
        HongguoLocalProbeStatus = "测试中...";
        try
        {
            var settings = DramaSourceSettingsMapping.FromClientSettings(ToSettings());
            var service = new HongguoLocalApiService(ProbeHttp);
            var results = await service.SearchAsync(settings, "测试", 1, CancellationToken.None);
            HongguoLocalProbeStatus = $"本地直连服务连接成功，返回 {results.Count} 条：{DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            HongguoLocalProbeStatus = $"本地直连服务连接失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ReadMapleleafUdid()
    {
        var deviceId = MapleleafDeviceStore.TryReadDeviceId();
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            MapleleafProbeStatus = "未在注册表中找到 HongGuoClient\\DeviceUDID。请先登录 Mapleleaf，或生成新设备号。";
            return;
        }

        MapleleafUdid = deviceId;
        MapleleafProbeStatus = "已从注册表读取 Mapleleaf DeviceUDID。";
    }

    [RelayCommand]
    private void GenerateMapleleafUdid()
    {
        if (!string.IsNullOrWhiteSpace(MapleleafUdid))
        {
            MapleleafProbeStatus = "设备号已有值；如确需更换，请先清空后再生成，避免账号与旧设备解绑。";
            return;
        }

        MapleleafUdid = MapleleafDeviceStore.GenerateDeviceId();
        MapleleafProbeStatus = "已生成新的 Mapleleaf DeviceUDID，请保存设置。";
    }

    [RelayCommand]
    private async Task ProbeMapleleafLoginAsync()
    {
        if (string.IsNullOrWhiteSpace(MapleleafAccount) || string.IsNullOrWhiteSpace(MapleleafPassword))
        {
            MapleleafProbeStatus = "请先填写 Mapleleaf 账号和密码。";
            return;
        }
        if (string.IsNullOrWhiteSpace(MapleleafUdid))
        {
            MapleleafProbeStatus = "请先填写、读取或生成 Mapleleaf DeviceUDID。";
            return;
        }

        MapleleafProbeStatus = "测试中...";
        try
        {
            var settings = DramaSourceSettingsMapping.FromClientSettings(ToSettings());
            var result = await new MapleleafApiService(ProbeHttp).ProbeLoginAsync(settings, CancellationToken.None);
            var preview = result.Token.Length > 12 ? $"{result.Token[..6]}…{result.Token[^4..]}" : result.Token;
            MapleleafProbeStatus = $"测试登录成功：{DateTime.Now:HH:mm:ss} token={preview}";
        }
        catch (Exception ex)
        {
            MapleleafProbeStatus = $"测试登录失败：{ex.Message}";
        }
    }

    [RelayCommand]
    partial void OnNetworkAddressPreferenceChanged(string value) =>
        ShortDrama.Infrastructure.Networking.PreferredNetworkHttp.Preference =
            ShortDrama.Infrastructure.Networking.PreferredNetworkHttp.Parse(value);

    private async Task ProbeAiTextAsync()
    {
        if (string.IsNullOrWhiteSpace(AiTextEndpoint) || string.IsNullOrWhiteSpace(AiTextApiKey))
        {
            AiTextProbeStatus = "请先填写 AI 文本接口地址和 API Key。";
            return;
        }

        AiTextProbeStatus = "测试中...";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{AiTextEndpoint.TrimEnd('/')}/chat/completions");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AiTextApiKey.Trim());
            request.Content = JsonContent.Create(new
            {
                model = AiTextModel,
                thinking = new { type = "disabled" },
                messages = new object[]
                {
                    new { role = "user", content = "回复 OK" }
                },
                max_tokens = 8,
            });

            using var response = await ProbeHttp.SendAsync(request, CancellationToken.None);
            var body = await response.Content.ReadAsStringAsync();
            AiTextProbeStatus = response.IsSuccessStatusCode
                ? $"AI 文本接口可用：{DateTime.Now:HH:mm:ss}"
                : AiApiErrorMessage.Create("AI 文本接口测试", response.StatusCode, response.ReasonPhrase, body);
        }
        catch (Exception ex)
        {
            AiTextProbeStatus = $"AI 文本测试失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ProbeLocalAsr()
    {
        var settings = ToSettings();
        var (ok, reason) = SherpaOnnxModelResolver.CheckAvailable(settings);
        AsrProbeStatus = ok ? "本地 Paraformer 配置可用" : reason;
        StatusRequested?.Invoke(AsrProbeStatus);
    }

    [RelayCommand]
    private void ProbeVolcengineAsr()
    {
        var settings = ToSettings();
        var ok = !string.IsNullOrWhiteSpace(settings.TiktokAsrAppId) &&
                 !string.IsNullOrWhiteSpace(settings.TiktokAsrAccessToken);
        var reason = ok ? "" : "未配置火山 ASR（AppID / AccessToken）";
        AsrProbeStatus = ok ? "火山 ASR 凭据已配置" : reason;
        StatusRequested?.Invoke(AsrProbeStatus);
    }

    [RelayCommand]
    private void BackupMainDatabase()
    {
        try
        {
            var source = MainDatabasePath;
            if (!File.Exists(source))
            {
                StatusRequested?.Invoke("主数据库不存在");
                return;
            }

            var backupDir = Path.Combine(Path.GetDirectoryName(source)!, "backups");
            Directory.CreateDirectory(backupDir);
            var target = Path.Combine(backupDir, $"tiktok_publisher_{DateTime.Now:yyyyMMdd_HHmmss}.db");
            File.Copy(source, target, overwrite: false);
            StatusRequested?.Invoke($"已备份到 {target}");
        }
        catch (Exception ex)
        {
            StatusRequested?.Invoke($"备份失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void BackupWorkspaceDatabase()
    {
        try
        {
            var source = WorkspaceDatabasePath;
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                StatusRequested?.Invoke("工作区数据库不存在");
                return;
            }

            var backupDir = Path.Combine(Path.GetDirectoryName(source)!, "backups");
            Directory.CreateDirectory(backupDir);
            var target = Path.Combine(backupDir, $".tiktok-task-queue_{DateTime.Now:yyyyMMdd_HHmmss}.db");
            File.Copy(source, target, overwrite: false);
            StatusRequested?.Invoke($"已备份到 {target}");
        }
        catch (Exception ex)
        {
            StatusRequested?.Invoke($"备份失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void CompactMainDatabase()
    {
        try
        {
            if (!File.Exists(MainDatabasePath))
            {
                StatusRequested?.Invoke("主数据库不存在");
                return;
            }

            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={MainDatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM;";
            command.ExecuteNonQuery();
            RefreshDatabaseStats();
            StatusRequested?.Invoke("主数据库已压缩");
        }
        catch (Exception ex)
        {
            StatusRequested?.Invoke($"压缩失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void RefreshDatabaseStats()
    {
        var lines = new List<string>();
        AppendDatabaseStats(lines, "主数据库", MainDatabasePath);
        if (!string.IsNullOrWhiteSpace(WorkspaceDatabasePath))
        {
            AppendDatabaseStats(lines, "工作区数据库", WorkspaceDatabasePath);
        }
        else
        {
            lines.Add("工作区数据库：未绑定工作目录");
        }

        DatabaseStatsText = string.Join(Environment.NewLine, lines);
    }

    [RelayCommand]
    private void OpenMainDatabaseFolder()
    {
        OpenParentFolder(MainDatabasePath);
    }

    [RelayCommand]
    private void OpenWorkspaceDatabaseFolder()
    {
        OpenParentFolder(WorkspaceDatabasePath);
    }

    public void UpdateWorkspacePath(string? workspacePath)
    {
        WorkspaceDatabasePath = ClientSettingsStore.WorkspaceDatabasePath(workspacePath);
        RefreshDatabaseStats();
    }

    private void ApplyFromSettings(ClientSettings settings)
    {
        DramaSourceChain = settings.DramaSourceChain;
        DramaDownloadDefaultQuality = settings.DramaDownloadDefaultQuality;
        DramaDownloadConcurrent = settings.DramaDownloadConcurrent;
        DramaDownloadMaxParallelProjects = settings.DramaDownloadMaxParallelProjects;
        DownloadFileSegments = settings.DownloadFileSegments;
        DownloaderApiBaseUrl = string.IsNullOrWhiteSpace(settings.DownloaderApiBaseUrl)
            ? "http://127.0.0.1:17891" : settings.DownloaderApiBaseUrl;
        DownloaderApiKey = settings.DownloaderApiKey;
        HongguoDownloadTimeoutSeconds = settings.HongguoDownloadTimeoutSeconds;
        HongguoEpisodeDownloadAttempts = settings.HongguoEpisodeDownloadAttempts;
        HgnewAccount = settings.HgnewAccount;
        HgnewPassword = settings.HgnewPassword;
        HgnewUdid = HongguoDeviceId.ResolveV14(settings.HgnewUdid);
        HgnewClientVersion = settings.HgnewClientVersion;
        HghighAccount = settings.HghighAccount;
        HghighPassword = settings.HghighPassword;
        HghighEdition = HongguoClientProfile.StandardEdition;
        HghighDeviceId = string.IsNullOrWhiteSpace(settings.HghighDeviceId)
            ? HongguoHighDeviceStore.TryReadDeviceId(HongguoClientProfile.High)
            : settings.HghighDeviceId;
        HghighClientExe = settings.HghighClientExe;
        HghighClientVersion = NormalizeHghighClientVersion(settings.HghighClientVersion, HongguoClientProfile.High.ClientVersion);
        HghighStandardDeviceId = string.IsNullOrWhiteSpace(settings.HghighStandardDeviceId)
            ? HongguoHighDeviceStore.TryReadDeviceId(HongguoClientProfile.Standard)
            : settings.HghighStandardDeviceId;
        HghighStandardClientExe = settings.HghighStandardClientExe;
        HghighStandardClientVersion = NormalizeHghighClientVersion(
            settings.HghighStandardClientVersion,
            HongguoClientProfile.Standard.ClientVersion);
        HghighStandardPlaybackMode = HongguoClientProfile.NormalizeStandardPlaybackMode(settings.HghighStandardPlaybackMode);
        HongguoAppDeviceId = settings.HongguoAppDeviceId ?? "";
        HongguoAppInstallId = settings.HongguoAppInstallId ?? "";
        HongguoAppPlan = ClientSettings.NormalizeHongguoAppPlan(settings.HongguoAppPlan);
        HongguoAppStreamLink1 = settings.HongguoAppStreamLink1;
        HongguoAppStreamLink2 = settings.HongguoAppStreamLink2;
        _hongguoAppPlaybackLoadedDeviceId = HongguoAppDeviceId;
        MapleleafAccount = settings.MapleleafAccount;
        MapleleafPassword = settings.MapleleafPassword;
        MapleleafUdid = string.IsNullOrWhiteSpace(settings.MapleleafUdid)
            ? MapleleafDeviceStore.TryReadDeviceId()
            : settings.MapleleafUdid;
        var masters = HongguoHighDeviceStore.LoadStartupMastersRaw(SelectedHghighProfile);
        HghighEncMaster = masters.Enc;
        HghighSignMaster = masters.Sign;
        RefreshHghighMastersStatus();
        HongguoLocalBaseUrl = settings.HongguoLocalBaseUrl;
        HongguoLocalApiKey = settings.HongguoLocalApiKey;
        HongguoLocalDownloadMode = NormalizeHongguoLocalDownloadMode(settings.HongguoLocalDownloadMode);
        HongguoLocalTranscodeEngine = NormalizeHongguoLocalTranscodeEngine(settings.HongguoLocalTranscodeEngine);
        PikachuServerUrl = settings.PikachuServerUrl;
        PikachuFanqieCookie = settings.PikachuFanqieCookie;
        PikachuDramaType = NormalizePikachuDramaType(settings.PikachuDramaType);
        PikachuDeviceId = settings.PikachuDeviceId;
        PikachuClientVersion = settings.PikachuClientVersion;
        PlayletServerUrl = string.IsNullOrWhiteSpace(settings.PlayletServerUrl)
            ? PlayletDramaClient.DefaultServerUrl
            : settings.PlayletServerUrl;
        PlayletDeviceId = settings.PlayletDeviceId;
        ForeignServerUrl = string.IsNullOrWhiteSpace(settings.ForeignServerUrl)
            ? ForeignPlayletClient.DefaultServerUrl
            : settings.ForeignServerUrl;
        ForeignDeviceId = settings.ForeignDeviceId;
        ForeignSearchSource = ForeignPlayletClient.NormalizeSource(settings.ForeignSearchSource);
        ForeignLanguage = ForeignPlayletClient.NormalizeLanguage(settings.ForeignLanguage);
        DramaBoxApiBaseUrl = string.IsNullOrWhiteSpace(settings.DramaBoxApiBaseUrl)
            ? DramaBoxApiService.DefaultBaseUrl
            : settings.DramaBoxApiBaseUrl;
        DramaBoxDeviceId = string.IsNullOrWhiteSpace(settings.DramaBoxDeviceId)
            ? DramaBoxApiService.DefaultDeviceId
            : settings.DramaBoxDeviceId;
        TiktokAsrLocalModelDir = settings.TiktokAsrLocalModelDir;
        TiktokAsrLocalVadPath = settings.TiktokAsrLocalVadPath;
        TiktokAsrAppId = settings.TiktokAsrAppId;
        TiktokAsrAccessToken = settings.TiktokAsrAccessToken;
        TiktokMaterialValidateConcurrency = settings.TiktokMaterialValidateConcurrency;
        TiktokGenerationPerProjectConcurrency = settings.TiktokGenerationPerProjectConcurrency;
        TiktokAiTextConcurrency = settings.TiktokAiTextConcurrency;
        TiktokAsrConcurrency = settings.TiktokAsrConcurrency;
        TiktokFfmpegConcurrency = settings.TiktokFfmpegConcurrency;
        TiktokImageGenerationConcurrency = settings.TiktokImageGenerationConcurrency;
        TiktokVisualConcurrency = settings.TiktokVisualConcurrency;
        TiktokDocumentConcurrency = settings.TiktokDocumentConcurrency;
        TiktokAsrLanguage = settings.TiktokAsrLanguage;
        TiktokManualInterventionOnSingleFailure = settings.TiktokManualInterventionOnSingleFailure;
        AiTextEndpoint = settings.AiTextEndpoint;
        AiTextApiKey = settings.AiTextApiKey;
        AiTextModel = settings.AiTextModel;
        AiTextTimeoutSeconds = settings.AiTextTimeoutSeconds;
        TiktokRoleReferenceSelectionMode = settings.TiktokRoleReferenceSelectionMode;
        TiktokRoleReferenceAiFallbackEnabled = settings.TiktokRoleReferenceAiFallbackEnabled;
        TiktokRoleVectorViewMode = settings.TiktokRoleVectorViewMode;
        AiPublishInfoPrompt = ClientSettingsStore.ResolvePublishInfoPrompt(settings);
        KuaishouPublishInfoPrompt = ClientSettingsStore.ResolveKuaishouPublishInfoPrompt(settings);
        PosterMode = settings.PosterMode;
        PosterLayoutRequestMode = settings.PosterLayoutRequestMode;
        NetworkAddressPreference = settings.NetworkAddressPreference;
        ImageProvider = settings.ImageProvider;
        ImageModelId = settings.ImageModelId;
        ImageModelApiKey = settings.ImageModelApiKey;
        ImageModelEndpoint = settings.ImageModelEndpoint;
        DoubaoImageResolution = settings.DoubaoImageResolution;
        DoubaoImageRatio = settings.DoubaoImageRatio;
        OfoxImage2ModelId = settings.OfoxImage2ModelId;
        OfoxImage2ApiKey = settings.OfoxImage2ApiKey;
        OfoxImage2Endpoint = settings.OfoxImage2Endpoint;
        OfoxImage2Quality = settings.OfoxImage2Quality;
        OfoxImage2Size = settings.OfoxImage2Size;
        PosterTitleVerifyEnabled = settings.PosterTitleVerifyEnabled;
        PosterTitleVerifyMode = settings.PosterTitleVerifyMode;
        PosterTitleVerifyAiRetryCount = settings.PosterTitleVerifyAiRetryCount;
        FrameExtractEpisodeIndex = settings.FrameExtractEpisodeIndex;
        FrameExtractTime = settings.FrameExtractTime;
        FrameExtractNeighborOffsetsSeconds = settings.FrameExtractNeighborOffsetsSeconds;
        FrameExtractFallbackPercents = settings.FrameExtractFallbackPercents;
        _posterAgentDirectPrompt = ClientSettingsStore.ResolvePosterAgentPrompt(
            settings,
            ClientSettingsDefaults.PosterModeDirect);
        _posterAgentErasePrompt = ClientSettingsStore.ResolvePosterAgentPrompt(
            settings,
            ClientSettingsDefaults.PosterModeEraseFixedTitle);
        ShowPosterAgentPromptForCurrentMode();
        _kuaishouPosterVerticalDirectPrompt = ClientSettingsStore.ResolveKuaishouPosterPrompt(
            settings, horizontal: false, ClientSettingsDefaults.PosterModeDirect);
        _kuaishouPosterVerticalErasePrompt = ClientSettingsStore.ResolveKuaishouPosterPrompt(
            settings, horizontal: false, ClientSettingsDefaults.PosterModeEraseFixedTitle);
        _kuaishouPosterHorizontalDirectPrompt = ClientSettingsStore.ResolveKuaishouPosterPrompt(
            settings, horizontal: true, ClientSettingsDefaults.PosterModeDirect);
        _kuaishouPosterHorizontalErasePrompt = ClientSettingsStore.ResolveKuaishouPosterPrompt(
            settings, horizontal: true, ClientSettingsDefaults.PosterModeEraseFixedTitle);
        ShowKuaishouPosterPromptsForCurrentMode();
        TiktokProjectImageGenerationMode = settings.TiktokProjectImageGenerationMode;
        TiktokProjectImageTemplateRoot = settings.TiktokProjectImageTemplateRoot;
        TiktokProjectImageTemplateId = settings.TiktokProjectImageTemplateId;
        TiktokProjectImageCount = settings.TiktokProjectImageCount;
        TiktokProjectImageRenderEpisodeLimit = settings.TiktokProjectImageRenderEpisodeLimit;
        TiktokProjectImageSubtitleAiMode = settings.TiktokProjectImageSubtitleAiMode;
        TiktokProjectImageFableCutRoot = settings.TiktokProjectImageFableCutRoot;
        TiktokProjectImageFableCutClipCount = settings.TiktokProjectImageFableCutClipCount;
        TiktokProofTemplateDocxPath = settings.TiktokProofTemplateDocxPath;
        TiktokProofWpsPath = settings.TiktokProofWpsPath;
        TiktokProofDeclarantCompanyName = settings.TiktokProofDeclarantCompanyName;
        TiktokProofSealPath = settings.TiktokProofSealPath;
        TiktokProofPdfRenderer = settings.TiktokProofPdfRenderer;
        TiktokProofKeepDocx = settings.TiktokProofKeepDocx;
        TiktokExcelAutoExportEnabled = settings.TiktokExcelAutoExportEnabled;
        TiktokAccountAnalyticsEnabled = settings.TiktokAccountAnalyticsEnabled;
        TiktokSyncWorkspaceRoot = settings.TiktokSyncWorkspaceRoot;
        ManagementDedupEnabled = settings.ManagementDedupEnabled;
        ManagementDedupScope = settings.ManagementDedupScope;
        TiktokAllowOverLimitUploadImport = settings.TiktokAllowOverLimitUploadImport;
        TiktokMinimumEpisodeCount = settings.TiktokMinimumEpisodeCount;
        TiktokMaximumEpisodeCount = settings.TiktokMaximumEpisodeCount;
    }

    private static void AppendDatabaseStats(List<string> lines, string label, string path)
    {
        lines.Add($"{label}：{path}");
        if (!File.Exists(path))
        {
            lines.Add("  状态：文件不存在");
            return;
        }

        var info = new FileInfo(path);
        lines.Add($"  大小：{FormatBytes(info.Length)}");
        lines.Add($"  修改时间：{info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.##} MB";
    }

    private static string NormalizeHghighClientVersion(string? value, string fallback)
    {
        var text = (value ?? "").Trim();
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static string NormalizeHongguoLocalDownloadMode(string? value)
    {
        var normalized = (value ?? "fast").Trim().ToLowerInvariant();
        return normalized is "compatible" ? "compatible" : "fast";
    }

    private static string NormalizeHongguoLocalTranscodeEngine(string? value)
    {
        var normalized = (value ?? "auto").Trim().ToLowerInvariant();
        return normalized is "auto" or "nvenc" or "cpu" ? normalized : "auto";
    }

    private static string NormalizePikachuDramaType(string? value) =>
        string.Equals(value?.Trim(), "manga", StringComparison.OrdinalIgnoreCase)
            ? "manga"
            : "short";

    private static void OpenParentFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Explorer open is best-effort.
        }
    }

    private static HttpClient CreateProbeHttpClient()
    {
        var handler = ShortDrama.Infrastructure.Networking.PreferredNetworkHttp.CreateHandler();
        handler.ConnectTimeout = TimeSpan.FromSeconds(15);
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(120),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }
}
