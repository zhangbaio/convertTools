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

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class SystemSettingsViewModel : ViewModelBase
{
    private static readonly HttpClient ProbeHttp = CreateProbeHttpClient();

    public event Action<ClientSettings>? SettingsSaved;
    public event Action<string>? StatusRequested;
    public event Action<HongguoLoginProbeResult>? HgnewLoginProbeSucceeded;
    public event Func<string, Task>? CopyToClipboardAsync;

    [ObservableProperty] private string _saveMessage = "";
    [ObservableProperty] private string _hgnewProbeStatus = "";
    [ObservableProperty] private string _databaseStatsText = "";
    [ObservableProperty] private string _mainDatabasePath = ClientSettingsStore.MainDatabasePath;
    [ObservableProperty] private string _workspaceDatabasePath = "";

    [ObservableProperty] private string _dramaSourceChain = "hgnew";
    [ObservableProperty] private string _dramaDownloadDefaultQuality = "1080P";
    [ObservableProperty] private int _dramaDownloadConcurrent = 5;
    [ObservableProperty] private int _dramaDownloadMaxParallelProjects = 1;
    [ObservableProperty] private int _downloadFileSegments = 4;
    [ObservableProperty] private int _hongguoDownloadTimeoutSeconds = 60;
    [ObservableProperty] private int _hongguoEpisodeDownloadAttempts = 5;

    [ObservableProperty] private string _hgnewAccount = "";
    [ObservableProperty] private string _hgnewPassword = "";
    [ObservableProperty] private string _hgnewUdid = "";
    [ObservableProperty] private string _hgnewClientVersion = ClientSettings.DefaultHongguoClientVersion;

    [ObservableProperty] private string _hghighAccount = "";
    [ObservableProperty] private string _hghighPassword = "";
    [ObservableProperty] private string _hghighDeviceId = "";
    [ObservableProperty] private string _hghighClientExe = "";
    [ObservableProperty] private string _hghighEncMaster = "";
    [ObservableProperty] private string _hghighSignMaster = "";
    [ObservableProperty] private string _hghighProbeStatus = "";
    [ObservableProperty] private string _hghighMastersStatus = "";
    [ObservableProperty] private bool _isHghighBusy;
    [ObservableProperty] private bool _hghighRevealEnc;
    [ObservableProperty] private bool _hghighRevealSign;

    public string HghighRevealEncButtonText => HghighRevealEnc ? "隐藏密钥" : "显示密钥";
    public string HghighRevealSignButtonText => HghighRevealSign ? "隐藏密钥" : "显示密钥";

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
    [ObservableProperty] private string _hongguoLocalProbeStatus = "";
    [ObservableProperty] private string _aiTextProbeStatus = "";

    [ObservableProperty] private string _tiktokSilenceAsrEngine = "local";
    [ObservableProperty] private string _tiktokSilenceLocalModelDir = "";
    [ObservableProperty] private string _tiktokSilenceLocalVadPath = "";
    [ObservableProperty] private double _tiktokSilenceHybridLowSeconds = 15;
    [ObservableProperty] private double _tiktokSilenceHybridHighSeconds = 25;
    [ObservableProperty] private string _tiktokSilenceAsrAppId = "";
    [ObservableProperty] private string _tiktokSilenceAsrAccessToken = "";
    [ObservableProperty] private int _tiktokSilenceAsrThresholdSeconds = 20;
    [ObservableProperty] private string _tiktokSilenceRepairMode = "auto";
    [ObservableProperty] private double _tiktokSilenceRepairTargetSeconds = 17;
    [ObservableProperty] private double _tiktokSilenceRepairMaxSpeed = 2;
    [ObservableProperty] private bool _tiktokSilenceRepairBlocking;
    [ObservableProperty] private int _tiktokSilenceDetectConcurrency = 5;
    [ObservableProperty] private int _tiktokMaterialValidateConcurrency = 4;
    [ObservableProperty] private string _tiktokSilenceAsrLanguage = "zh-CN";
    [ObservableProperty] private bool _tiktokManualInterventionOnSingleFailure = true;
    [ObservableProperty] private string _asrProbeStatus = "";

    [ObservableProperty] private string _aiTextEndpoint = ClientSettingsDefaults.AiTextEndpoint;
    [ObservableProperty] private string _aiTextApiKey = "";
    [ObservableProperty] private string _aiTextModel = ClientSettingsDefaults.AiTextModel;
    [ObservableProperty] private int _aiTextTimeoutSeconds = ClientSettingsDefaults.AiTextTimeoutSeconds;
    [ObservableProperty] private int _aiTextMaxBatchSize = ClientSettingsDefaults.AiTextMaxBatchSize;
    [ObservableProperty] private string _tiktokRoleReferenceSelectionMode =
        ClientSettingsDefaults.TiktokRoleReferenceSelectionMode;
    [ObservableProperty] private bool _tiktokRoleReferenceAiFallbackEnabled =
        ClientSettingsDefaults.TiktokRoleReferenceAiFallbackEnabled;
    [ObservableProperty] private string _aiTagSystemPrompt = "";
    [ObservableProperty] private string _aiTagBatchPrompt = "";
    [ObservableProperty] private string _aiFullInfoSystemPrompt = "";
    [ObservableProperty] private string _aiFullInfoBatchPrompt = "";
    [ObservableProperty] private string _aiFullInfoRetryPrompt = "";

    [ObservableProperty] private string _posterMode = ClientSettingsDefaults.PosterMode;
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
    [ObservableProperty] private string _frameCoverPrompt = "";
    [ObservableProperty] private string _posterLayoutDetectPrompt = "";
    [ObservableProperty] private string _posterInpaintPrompt = "";
    [ObservableProperty] private string _posterInpaintSafeRetryPrompt = "";
    [ObservableProperty] private string _posterGenerationPrompt = "";
    [ObservableProperty] private string _posterGenerationSafeRetryPrompt = "";
    [ObservableProperty] private string _posterNameSystemPrompt = "";
    [ObservableProperty] private string _posterNameUserPrompt = "";
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
    [ObservableProperty] private bool _managementDedupEnabled;
    [ObservableProperty] private string _managementDedupScope = "tiktok_username";
    [ObservableProperty] private bool _tiktokAllowOverLimitUploadImport = ClientSettingsDefaults.TiktokAllowOverLimitUploadImport;
    [ObservableProperty] private int _tiktokOverLimitDownloadEpisodeCount = ClientSettingsDefaults.TiktokOverLimitDownloadEpisodeCount;

    public IReadOnlyList<string> DramaSourceOptions { get; } =
    [
        "hgnew",
        "hghigh",
        "hglocal",
        "pikachu"
    ];

    public IReadOnlyList<string> AsrEngineOptions { get; } = ["volcengine", "local", "hybrid"];
    public IReadOnlyList<string> SilenceRepairModeOptions { get; } = ["auto", "trim", "speedup"];
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
        ApplyFromSettings(ClientSettingsStore.Load());
        MainDatabasePath = ClientSettingsStore.MainDatabasePath;
        WorkspaceDatabasePath = ClientSettingsStore.WorkspaceDatabasePath(workspacePath);
        RefreshDatabaseStats();
    }

    public ClientSettings ToSettings() => new()
    {
        DramaSourceChain = DramaSourceChain,
        DramaDownloadDefaultQuality = DramaDownloadDefaultQuality,
        DramaDownloadConcurrent = DramaDownloadConcurrent,
        DramaDownloadMaxParallelProjects = DramaDownloadMaxParallelProjects,
        DownloadFileSegments = DownloadFileSegments,
        HongguoDownloadTimeoutSeconds = HongguoDownloadTimeoutSeconds,
        HongguoEpisodeDownloadAttempts = HongguoEpisodeDownloadAttempts,
        HgnewAccount = HgnewAccount.Trim(),
        HgnewPassword = HgnewPassword,
        HgnewUdid = ClientSettingsStore.NormalizeUdid(HgnewUdid),
        HgnewClientVersion = HongguoClientVersion.Normalize(HgnewClientVersion),
        HghighAccount = HghighAccount.Trim(),
        HghighPassword = HghighPassword,
        HghighDeviceId = HghighDeviceId.Trim(),
        HghighClientExe = HghighClientExe.Trim(),
        HongguoLocalBaseUrl = HongguoLocalBaseUrl.Trim(),
        HongguoLocalApiKey = HongguoLocalApiKey.Trim(),
        HongguoLocalDownloadMode = NormalizeHongguoLocalDownloadMode(HongguoLocalDownloadMode),
        HongguoLocalTranscodeEngine = NormalizeHongguoLocalTranscodeEngine(HongguoLocalTranscodeEngine),
        PikachuServerUrl = PikachuServerUrl.Trim(),
        PikachuFanqieCookie = PikachuFanqieCookie.Trim(),
        PikachuDramaType = NormalizePikachuDramaType(PikachuDramaType),
        PikachuDeviceId = PikachuDeviceId.Trim(),
        PikachuClientVersion = string.IsNullOrWhiteSpace(PikachuClientVersion) ? "1.4.4" : PikachuClientVersion.Trim(),
        TiktokSilenceAsrEngine = TiktokSilenceAsrEngine,
        TiktokSilenceLocalModelDir = TiktokSilenceLocalModelDir.Trim(),
        TiktokSilenceLocalVadPath = TiktokSilenceLocalVadPath.Trim(),
        TiktokSilenceHybridLowSeconds = TiktokSilenceHybridLowSeconds,
        TiktokSilenceHybridHighSeconds = TiktokSilenceHybridHighSeconds,
        TiktokSilenceAsrAppId = TiktokSilenceAsrAppId.Trim(),
        TiktokSilenceAsrAccessToken = TiktokSilenceAsrAccessToken,
        TiktokSilenceAsrThresholdSeconds = TiktokSilenceAsrThresholdSeconds,
        TiktokSilenceRepairMode = TiktokSilenceRepairMode,
        TiktokSilenceRepairTargetSeconds = TiktokSilenceRepairTargetSeconds,
        TiktokSilenceRepairMaxSpeed = TiktokSilenceRepairMaxSpeed,
        TiktokSilenceRepairBlocking = TiktokSilenceRepairBlocking,
        TiktokSilenceDetectConcurrency = TiktokSilenceDetectConcurrency,
        TiktokMaterialValidateConcurrency = TiktokMaterialValidateConcurrency,
        TiktokSilenceAsrLanguage = TiktokSilenceAsrLanguage.Trim(),
        TiktokManualInterventionOnSingleFailure = TiktokManualInterventionOnSingleFailure,
        AiTextEndpoint = AiTextEndpoint.Trim(),
        AiTextApiKey = AiTextApiKey,
        AiTextModel = AiTextModel.Trim(),
        AiTextTimeoutSeconds = AiTextTimeoutSeconds,
        AiTextMaxBatchSize = AiTextMaxBatchSize,
        TiktokRoleReferenceSelectionMode = TiktokRoleReferenceSelectionMode,
        TiktokRoleReferenceAiFallbackEnabled = TiktokRoleReferenceAiFallbackEnabled,
        AiTagSystemPrompt = AiTagSystemPrompt,
        AiTagBatchPrompt = AiTagBatchPrompt,
        AiFullInfoSystemPrompt = AiFullInfoSystemPrompt,
        AiFullInfoBatchPrompt = AiFullInfoBatchPrompt,
        AiFullInfoRetryPrompt = AiFullInfoRetryPrompt,
        PosterMode = PosterMode,
        ImageProvider = ImageProvider,
        ImageModelId = ImageModelId.Trim(),
        ImageModelApiKey = ImageModelApiKey,
        ImageModelEndpoint = ImageModelEndpoint.Trim(),
        DoubaoImageResolution = DoubaoImageResolution,
        DoubaoImageRatio = DoubaoImageRatio,
        OfoxImage2ModelId = OfoxImage2ModelId.Trim(),
        OfoxImage2ApiKey = OfoxImage2ApiKey,
        OfoxImage2Endpoint = OfoxImage2Endpoint.Trim(),
        OfoxImage2Quality = OfoxImage2Quality,
        OfoxImage2Size = OfoxImage2Size.Trim(),
        PosterTitleVerifyEnabled = PosterTitleVerifyEnabled,
        PosterTitleVerifyMode = PosterTitleVerifyMode,
        PosterTitleVerifyAiRetryCount = PosterTitleVerifyAiRetryCount,
        FrameExtractEpisodeIndex = FrameExtractEpisodeIndex,
        FrameExtractTime = FrameExtractTime,
        FrameExtractNeighborOffsetsSeconds = FrameExtractNeighborOffsetsSeconds,
        FrameExtractFallbackPercents = FrameExtractFallbackPercents,
        FrameCoverPrompt = FrameCoverPrompt,
        PosterLayoutDetectPrompt = PosterLayoutDetectPrompt,
        PosterInpaintPrompt = PosterInpaintPrompt,
        PosterInpaintSafeRetryPrompt = PosterInpaintSafeRetryPrompt,
        PosterGenerationPrompt = PosterGenerationPrompt,
        PosterGenerationSafeRetryPrompt = PosterGenerationSafeRetryPrompt,
        PosterNameSystemPrompt = PosterNameSystemPrompt,
        PosterNameUserPrompt = PosterNameUserPrompt,
        TiktokProjectImageGenerationMode = TiktokProjectImageGenerationMode,
        TiktokProjectImageTemplateRoot = TiktokProjectImageTemplateRoot.Trim(),
        TiktokProjectImageTemplateId = TiktokProjectImageTemplateId.Trim(),
        TiktokProjectImageCount = TiktokProjectImageCount,
        TiktokProjectImageRenderEpisodeLimit = TiktokProjectImageRenderEpisodeLimit,
        TiktokProjectImageSubtitleAiMode = TiktokProjectImageSubtitleAiMode,
        TiktokProjectImageFableCutRoot = TiktokProjectImageFableCutRoot.Trim(),
        TiktokProjectImageFableCutClipCount = Math.Clamp(TiktokProjectImageFableCutClipCount, 12, 36),
        TiktokProofTemplateDocxPath = TiktokProofTemplateDocxPath.Trim(),
        TiktokProofWpsPath = TiktokProofWpsPath.Trim(),
        TiktokProofDeclarantCompanyName = TiktokProofDeclarantCompanyName.Trim(),
        TiktokProofSealPath = TiktokProofSealPath.Trim(),
        TiktokProofPdfRenderer = TiktokProofPdfRenderer,
        TiktokProofKeepDocx = TiktokProofKeepDocx,
        TiktokExcelAutoExportEnabled = TiktokExcelAutoExportEnabled,
        ManagementDedupEnabled = ManagementDedupEnabled,
        ManagementDedupScope = ManagementDedupScope,
        TiktokAllowOverLimitUploadImport = TiktokAllowOverLimitUploadImport,
        TiktokOverLimitDownloadEpisodeCount = TiktokOverLimitDownloadEpisodeCount,
    };

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

                ClientSettingsStore.PatchPikachuRuntimeFields(result.FanqieCookie, result.DeviceId);
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
            var settings = ToSettings();
            PersistHghighMastersFromForm();
            ClientSettingsStore.Save(settings);
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
        var deviceId = HongguoHighDeviceStore.TryReadDeviceId();
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            HghighProbeStatus = "未读到高码率 DeviceId。请先安装并登录一次官方 HG 高码率版客户端。";
            return;
        }

        HghighDeviceId = deviceId;
        HghighProbeStatus = "已从本机官方高码率客户端读取 DeviceId。";
    }

    [RelayCommand]
    private async Task ProbeHghighLoginAsync()
    {
        if (string.IsNullOrWhiteSpace(HghighAccount) || string.IsNullOrWhiteSpace(HghighPassword))
        {
            HghighProbeStatus = "请先填写红果高码率账号和密码。";
            return;
        }

        if (!HongguoHighDeviceStore.IsReady())
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
        HghighMastersStatus = "正在关闭已运行的官方高码率客户端并提取密钥…";
        try
        {
            var progress = new Progress<string>(message => HghighMastersStatus = message);
            var result = await HongguoHighMasterProvisioner.ExtractAsync(
                HghighClientExe,
                progress,
                CancellationToken.None);
            if (string.IsNullOrWhiteSpace(HghighClientExe))
            {
                HghighClientExe = HongguoHighDeviceStore.FindOfficialClientExe(null) ?? "";
            }

            if (string.IsNullOrWhiteSpace(HghighDeviceId))
            {
                HghighDeviceId = result.DeviceId;
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

    private void ShowExtractedMasters(string enc, string sign)
    {
        HghighRevealEnc = true;
        HghighRevealSign = true;
        ApplyMastersToForm(enc, sign);
        var capturedEnc = enc ?? "";
        var capturedSign = sign ?? "";
        Dispatcher.UIThread.Post(
            () =>
            {
                HghighRevealEnc = true;
                HghighRevealSign = true;
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
            HongguoHighDeviceStore.ClearStartupMasters();
            return;
        }

        if (string.IsNullOrWhiteSpace(HghighDeviceId))
        {
            HghighDeviceId = HongguoHighDeviceStore.TryReadDeviceId();
        }

        HongguoHighDeviceStore.CacheStartupMasters(HghighEncMaster, HghighSignMaster, HghighDeviceId);
    }

    private void RefreshHghighMastersStatus()
    {
        if (HongguoHighDeviceStore.IsReady())
        {
            HghighMastersStatus = "本机已缓存启动密钥，可正常登录。";
            return;
        }

        var (enc, sign) = HongguoHighDeviceStore.LoadStartupMastersRaw();
        if (!string.IsNullOrWhiteSpace(enc) && !string.IsNullOrWhiteSpace(sign))
        {
            HghighMastersStatus = "已有启动密钥缓存，但尚未读取到本机高码率设备身份。请先运行官方客户端一次。";
            return;
        }

        HghighMastersStatus = "尚未缓存启动密钥。安装包已内置 Frida，选择官方客户端后点「提取启动密钥」。";
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
        settings.TiktokSilenceAsrEngine = "local";
        var (ok, reason) = TikTokSilenceAsrService.CheckAvailable(settings);
        AsrProbeStatus = ok ? "本地 Paraformer 配置可用" : reason;
        StatusRequested?.Invoke(AsrProbeStatus);
    }

    [RelayCommand]
    private void ProbeVolcengineAsr()
    {
        var settings = ToSettings();
        settings.TiktokSilenceAsrEngine = "volcengine";
        var (ok, reason) = TikTokSilenceAsrService.CheckAvailable(settings);
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
        HongguoDownloadTimeoutSeconds = settings.HongguoDownloadTimeoutSeconds;
        HongguoEpisodeDownloadAttempts = settings.HongguoEpisodeDownloadAttempts;
        HgnewAccount = settings.HgnewAccount;
        HgnewPassword = settings.HgnewPassword;
        HgnewUdid = HongguoDeviceId.ResolveV14(settings.HgnewUdid);
        HgnewClientVersion = settings.HgnewClientVersion;
        HghighAccount = settings.HghighAccount;
        HghighPassword = settings.HghighPassword;
        HghighDeviceId = string.IsNullOrWhiteSpace(settings.HghighDeviceId)
            ? HongguoHighDeviceStore.TryReadDeviceId()
            : settings.HghighDeviceId;
        HghighClientExe = settings.HghighClientExe;
        var masters = HongguoHighDeviceStore.LoadStartupMastersRaw();
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
        TiktokSilenceAsrEngine = settings.TiktokSilenceAsrEngine;
        TiktokSilenceLocalModelDir = settings.TiktokSilenceLocalModelDir;
        TiktokSilenceLocalVadPath = settings.TiktokSilenceLocalVadPath;
        TiktokSilenceHybridLowSeconds = settings.TiktokSilenceHybridLowSeconds;
        TiktokSilenceHybridHighSeconds = settings.TiktokSilenceHybridHighSeconds;
        TiktokSilenceAsrAppId = settings.TiktokSilenceAsrAppId;
        TiktokSilenceAsrAccessToken = settings.TiktokSilenceAsrAccessToken;
        TiktokSilenceAsrThresholdSeconds = settings.TiktokSilenceAsrThresholdSeconds;
        TiktokSilenceRepairMode = settings.TiktokSilenceRepairMode;
        TiktokSilenceRepairTargetSeconds = settings.TiktokSilenceRepairTargetSeconds;
        TiktokSilenceRepairMaxSpeed = settings.TiktokSilenceRepairMaxSpeed;
        TiktokSilenceRepairBlocking = settings.TiktokSilenceRepairBlocking;
        TiktokSilenceDetectConcurrency = settings.TiktokSilenceDetectConcurrency;
        TiktokMaterialValidateConcurrency = settings.TiktokMaterialValidateConcurrency;
        TiktokSilenceAsrLanguage = settings.TiktokSilenceAsrLanguage;
        TiktokManualInterventionOnSingleFailure = settings.TiktokManualInterventionOnSingleFailure;
        AiTextEndpoint = settings.AiTextEndpoint;
        AiTextApiKey = settings.AiTextApiKey;
        AiTextModel = settings.AiTextModel;
        AiTextTimeoutSeconds = settings.AiTextTimeoutSeconds;
        AiTextMaxBatchSize = settings.AiTextMaxBatchSize;
        TiktokRoleReferenceSelectionMode = settings.TiktokRoleReferenceSelectionMode;
        TiktokRoleReferenceAiFallbackEnabled = settings.TiktokRoleReferenceAiFallbackEnabled;
        AiTagSystemPrompt = settings.AiTagSystemPrompt;
        AiTagBatchPrompt = settings.AiTagBatchPrompt;
        AiFullInfoSystemPrompt = settings.AiFullInfoSystemPrompt;
        AiFullInfoBatchPrompt = settings.AiFullInfoBatchPrompt;
        AiFullInfoRetryPrompt = settings.AiFullInfoRetryPrompt;
        PosterMode = settings.PosterMode;
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
        FrameCoverPrompt = settings.FrameCoverPrompt;
        PosterLayoutDetectPrompt = settings.PosterLayoutDetectPrompt;
        PosterInpaintPrompt = settings.PosterInpaintPrompt;
        PosterInpaintSafeRetryPrompt = settings.PosterInpaintSafeRetryPrompt;
        PosterGenerationPrompt = settings.PosterGenerationPrompt;
        PosterGenerationSafeRetryPrompt = settings.PosterGenerationSafeRetryPrompt;
        PosterNameSystemPrompt = settings.PosterNameSystemPrompt;
        PosterNameUserPrompt = settings.PosterNameUserPrompt;
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
        ManagementDedupEnabled = settings.ManagementDedupEnabled;
        ManagementDedupScope = settings.ManagementDedupScope;
        TiktokAllowOverLimitUploadImport = settings.TiktokAllowOverLimitUploadImport;
        TiktokOverLimitDownloadEpisodeCount = settings.TiktokOverLimitDownloadEpisodeCount;
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
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(120),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }
}
