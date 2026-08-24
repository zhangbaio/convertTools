using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShortDrama.Desktop.Models;
using ShortDrama.Desktop.Services;
using ShortDrama.Infrastructure.Automation;
using ShortDrama.Infrastructure.Imaging;
using System.Collections.ObjectModel;

namespace ShortDrama.Desktop.ViewModels;

public partial class ConfigWindowViewModel : ViewModelBase
{
    private readonly DesktopConfigService _configService;
    private readonly DesktopShellService _shellService;
    private readonly HongguoNewApiService _hongguoNewApiService;
    private readonly HongguoLocalApiService _hongguoLocalApiService;
    private readonly DramaSourceRouter _dramaSourceRouter;
    private readonly HongguoMemoryReaderService _hongguoMemoryReaderService;
    private readonly XingeRemoteControlService _xingeRemoteControlService;
    private ProjectConfigSnapshot _loadedProjectConfig;
    private GlobalConfigSnapshot _loadedGlobalConfig;
    private Dictionary<string, ProjectImageTemplateDescriptor> _templateDescriptors = new(StringComparer.OrdinalIgnoreCase);

    public ConfigWindowViewModel(
        string rootDir,
        DesktopConfigService configService,
        DesktopShellService shellService,
        HongguoNewApiService hongguoNewApiService,
        HongguoLocalApiService hongguoLocalApiService,
        DramaSourceRouter dramaSourceRouter,
        HongguoMemoryReaderService hongguoMemoryReaderService,
        XingeRemoteControlService xingeRemoteControlService)
    {
        _configService = configService;
        _shellService = shellService;
        _hongguoNewApiService = hongguoNewApiService;
        _hongguoLocalApiService = hongguoLocalApiService;
        _dramaSourceRouter = dramaSourceRouter;
        _hongguoMemoryReaderService = hongguoMemoryReaderService;
        _xingeRemoteControlService = xingeRemoteControlService;
        ReloadConfigCommand = new RelayCommand(LoadConfig);
        OpenConfigFileCommand = new RelayCommand(OpenConfigFile);
        OpenGlobalSettingsFileCommand = new RelayCommand(OpenGlobalSettingsFile);
        RefreshXingeCredentialsCommand = new AsyncRelayCommand(RefreshXingeCredentialsAsync);

        _loadedProjectConfig = configService.LoadProject(rootDir);
        _loadedGlobalConfig = configService.LoadGlobal();

        SetRootDir(rootDir);
    }
    public ObservableCollection<WorkflowStepOption> ProjectImageTemplateOptions { get; } = [];

    public IReadOnlyList<string> WeixinMonetizationTypeOptions { get; } =
    [
        "IAA广告变现",
        "IAA广告",
        "IAP付费观看",
        "混合变现"
    ];

    public IReadOnlyList<string> WeixinDramaTypeOptions { get; } =
    [
        "漫剧",
        "真人",
        "自动检测"
    ];

    public IReadOnlyList<string> WeixinDramaQualificationOptions { get; } =
    [
        "其他微短剧",
        "重点普通微短剧"
    ];

    public IReadOnlyList<string> WeixinSubmitterIdentityOptions { get; } =
    [
        "剧目制作方",
        "版权方",
        "平台方"
    ];

    public IReadOnlyList<string> PikachuDramaTypeOptions { get; } =
    [
        "manga"
    ];

    public IReadOnlyList<string> FeishuReceiveIdTypeOptions { get; } =
    [
        "chat_id",
        "open_id",
        "user_id",
        "email"
    ];
    public IRelayCommand ReloadConfigCommand { get; }
    public IRelayCommand OpenConfigFileCommand { get; }
    public IRelayCommand OpenGlobalSettingsFileCommand { get; }
    public IAsyncRelayCommand RefreshXingeCredentialsCommand { get; }

    public string ProjectImageGenerationModeDisplay => "图片框选模板 (image_template)";

    public bool WasSaved { get; private set; }

    [ObservableProperty]
    private string rootDir = string.Empty;

    [ObservableProperty]
    private string configFilePath = string.Empty;

    [ObservableProperty]
    private string globalSettingsFilePath = string.Empty;

    [ObservableProperty]
    private string companyName = string.Empty;

    [ObservableProperty]
    private string searchPageSize = string.Empty;

    [ObservableProperty]
    private string templateDocxPath = string.Empty;

    [ObservableProperty]
    private string costReportBaseImagePath = string.Empty;

    [ObservableProperty]
    private string costReportActorPayRatio = string.Empty;

    [ObservableProperty]
    private string costReportLegalRepresentative = string.Empty;

    [ObservableProperty]
    private bool weixinHeadless;

    [ObservableProperty]
    private string weixinSlowMoMs = string.Empty;

    [ObservableProperty]
    private string weixinKeepOpenSeconds = string.Empty;

    [ObservableProperty]
    private string weixinLoginTimeoutSeconds = string.Empty;

    [ObservableProperty]
    private bool weixinSubmitEnabled;

    [ObservableProperty]
    private bool weixinPauseOnError;

    [ObservableProperty]
    private bool weixinSaveHtml;

    [ObservableProperty]
    private bool weixinSaveText;

    [ObservableProperty]
    private string weixinSubmissionReportDir = string.Empty;

    [ObservableProperty]
    private string weixinMonetizationType = string.Empty;

    [ObservableProperty]
    private string weixinDramaType = string.Empty;

    [ObservableProperty]
    private string weixinDramaQualification = string.Empty;

    [ObservableProperty]
    private string weixinSubmitterIdentity = string.Empty;

    [ObservableProperty]
    private string weixinTrialEpisodes = string.Empty;

    [ObservableProperty]
    private bool weixinFillRecommendation;

    [ObservableProperty]
    private string dramaSourceChain = "hgnew";

    [ObservableProperty]
    private bool xingeEnabled;

    [ObservableProperty]
    private string xingeServerUrl = string.Empty;

    [ObservableProperty]
    private string xingeUsername = string.Empty;

    [ObservableProperty]
    private string xingePassword = string.Empty;

    [ObservableProperty]
    private string xingeClientId = string.Empty;

    [ObservableProperty]
    private string xingeClientToken = string.Empty;

    [ObservableProperty]
    private string xingeUserRole = string.Empty;

    [ObservableProperty]
    private string xingeClientName = string.Empty;

    [ObservableProperty]
    private bool xingeWsEnabled = true;

    [ObservableProperty]
    private string xingePollIntervalSeconds = "3";

    [ObservableProperty]
    private string downloadFileSegments = "4";

    [ObservableProperty]
    private bool xingeUploadLoginQr = true;

    [ObservableProperty]
    private string xingeOperationStatus = string.Empty;

    [ObservableProperty]
    private string hgnewAccount = string.Empty;

    [ObservableProperty]
    private string hgnewPassword = string.Empty;

    [ObservableProperty]
    private string hgnewUdid = string.Empty;

    [ObservableProperty]
    private string hgnewClientVersion = string.Empty;

    [ObservableProperty]
    private string hghighAccount = string.Empty;

    [ObservableProperty]
    private string hghighPassword = string.Empty;

    [ObservableProperty]
    private string hghighDeviceId = string.Empty;

    [ObservableProperty]
    private string hghighClientExe = string.Empty;

    [ObservableProperty]
    private string mapleleafAccount = string.Empty;

    [ObservableProperty]
    private string mapleleafPassword = string.Empty;

    [ObservableProperty]
    private string mapleleafUdid = string.Empty;

    [ObservableProperty]
    private string mapleleafProbeStatus = string.Empty;

    [ObservableProperty]
    private string hghighProbeStatus = string.Empty;

    [ObservableProperty]
    private string hghighMastersStatus = string.Empty;

    [ObservableProperty]
    private string hongguoDownloadTimeoutSeconds = "60";

    [ObservableProperty]
    private string hongguoEpisodeDownloadAttempts = "5";

    [ObservableProperty]
    private string hgnewProbeStatus = string.Empty;

    [ObservableProperty]
    private string hongguoLocalBaseUrl = string.Empty;

    [ObservableProperty]
    private string hongguoLocalApiKey = string.Empty;

    [ObservableProperty]
    private string hongguoLocalProbeStatus = string.Empty;

    [ObservableProperty]
    private string pikachuServerUrl = string.Empty;

    [ObservableProperty]
    private string pikachuFanqieCookie = string.Empty;

    [ObservableProperty]
    private string pikachuDramaType = "short";

    [ObservableProperty]
    private string pikachuDeviceId = string.Empty;

    [ObservableProperty]
    private string pikachuClientVersion = "1.4.4";

    [ObservableProperty]
    private string pikachuProbeStatus = string.Empty;

    [ObservableProperty]
    private string aiTextEndpoint = string.Empty;

    [ObservableProperty]
    private string aiTextApiKey = string.Empty;

    [ObservableProperty]
    private string aiTextModel = string.Empty;

    [ObservableProperty]
    private string aiTextTimeoutSeconds = string.Empty;

    [ObservableProperty]
    private string aiTextMaxBatchSize = string.Empty;

    [ObservableProperty]
    private string aiTextSystemPrompt = string.Empty;

    [ObservableProperty]
    private string aiTextBatchPrompt = string.Empty;

    [ObservableProperty]
    private string aiTextRetryPrompt = string.Empty;

    [ObservableProperty]
    private string aiTitleSystemPrompt = string.Empty;

    [ObservableProperty]
    private string aiTitleBatchPrompt = string.Empty;

    [ObservableProperty]
    private string aiTagSystemPrompt = string.Empty;

    [ObservableProperty]
    private string aiTagBatchPrompt = string.Empty;

    [ObservableProperty]
    private string aiFullInfoSystemPrompt = string.Empty;

    [ObservableProperty]
    private string aiFullInfoBatchPrompt = string.Empty;

    [ObservableProperty]
    private string aiFullInfoRetryPrompt = string.Empty;

    [ObservableProperty]
    private string imageModelId = string.Empty;

    [ObservableProperty]
    private string imageModelApiKey = string.Empty;

    [ObservableProperty]
    private string imageModelEndpoint = string.Empty;

    [ObservableProperty]
    private string frameCoverPrompt = string.Empty;

    [ObservableProperty]
    private string posterLayoutDetectPrompt = string.Empty;

    [ObservableProperty]
    private string posterInpaintPrompt = string.Empty;

    [ObservableProperty]
    private string posterInpaintSafeRetryPrompt = string.Empty;

    [ObservableProperty]
    private string posterGenerationPrompt = string.Empty;

    [ObservableProperty]
    private string posterGenerationSafeRetryPrompt = string.Empty;

    [ObservableProperty]
    private string posterNameSystemPrompt = string.Empty;

    [ObservableProperty]
    private string posterNameUserPrompt = string.Empty;

    [ObservableProperty]
    private bool feishuNotificationEnabled;

    [ObservableProperty]
    private string feishuAppId = string.Empty;

    [ObservableProperty]
    private string feishuAppSecret = string.Empty;

    [ObservableProperty]
    private string feishuReceiveId = string.Empty;

    [ObservableProperty]
    private string feishuReceiveIdType = "chat_id";

    [ObservableProperty]
    private bool feishuNotifyOnStepStart;

    [ObservableProperty]
    private bool feishuNotifyOnStepSuccess = true;

    [ObservableProperty]
    private bool feishuNotifyOnStepFailure = true;

    [ObservableProperty]
    private bool feishuNotifyOnQueueSummary = true;

    [ObservableProperty]
    private bool feishuNotifyOnLoginQr = true;

    [ObservableProperty]
    private string feishuNotifyStepKeysText = string.Empty;

    [ObservableProperty]
    private string projectImageTemplateRoot = string.Empty;

    [ObservableProperty]
    private WorkflowStepOption? selectedProjectImageTemplateOption;

    [ObservableProperty]
    private string projectImageTemplateDir = string.Empty;

    [ObservableProperty]
    private string projectImageCount = string.Empty;

    public void SetRootDir(string path)
    {
        RootDir = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
        LoadConfig();
    }

    public void SetTemplateDocxPath(string path) => TemplateDocxPath = path ?? string.Empty;

    public void SetCostReportBaseImagePath(string path) => CostReportBaseImagePath = path ?? string.Empty;

    public void SetWeixinSubmissionReportDir(string path) => WeixinSubmissionReportDir = path ?? string.Empty;

    public void SetProjectImageTemplateRoot(string path)
    {
        ProjectImageTemplateRoot = path ?? string.Empty;
        RefreshProjectImageTemplateOptions();
    }

    public void ReadHgnewDeviceUdid()
    {
        if (!OperatingSystem.IsWindows())
        {
            HgnewProbeStatus = "当前平台不支持从注册表读取 DeviceUDID。";
            return;
        }

        try
        {
            var value = HongguoDeviceId.TryReadFromRegistry();
            HgnewProbeStatus = string.IsNullOrWhiteSpace(value)
                ? "未在注册表中找到 HongGuoClient\\DeviceUDID。"
                : "已从注册表读取 DeviceUDID。";
            if (!string.IsNullOrWhiteSpace(value))
            {
                HgnewUdid = HongguoDeviceId.Normalize(value);
            }
        }
        catch (Exception ex)
        {
            HgnewProbeStatus = $"读取 DeviceUDID 失败：{ex.Message}";
        }
    }

    public async Task ProbeHgnewLoginAsync()
    {
        try
        {
            await _hongguoNewApiService.ProbeLoginAsync(GlobalDramaSettingsProvider.FromGlobal(BuildWorkingGlobalConfig()), CancellationToken.None);
            HgnewProbeStatus = $"测试登录成功：{DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            HgnewProbeStatus = $"测试登录失败：{ex.Message}";
        }
    }

    public void ReadHghighDeviceId()
    {
        var deviceId = HongguoHighDeviceStore.TryReadDeviceId();
        HghighProbeStatus = string.IsNullOrWhiteSpace(deviceId)
            ? "未读到高码率 DeviceId。请先安装并登录一次官方高码率客户端。"
            : "已从本机官方高码率客户端读取 DeviceId。";
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            HghighDeviceId = deviceId;
        }
    }

    public async Task ProbeHghighLoginAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var service = new HongguoHighApiService(http);
            await service.ProbeLoginAsync(GlobalDramaSettingsProvider.FromGlobal(BuildWorkingGlobalConfig()), CancellationToken.None);
            HghighProbeStatus = $"测试登录成功：{DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            HghighProbeStatus = $"测试登录失败：{ex.Message}";
        }
    }

    public async Task ProvisionHghighMastersAsync()
    {
        try
        {
            HghighMastersStatus = "正在提取启动密钥…请确保官方客户端已完全退出。";
            var result = await HongguoHighMasterProvisioner.ExtractAsync(HghighClientExe, null, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(HghighDeviceId))
            {
                HghighDeviceId = result.DeviceId;
            }

            RefreshHghighMastersStatus();
            HghighMastersStatus = "已提取并缓存 Enc/Sign Master。提取前会自动关闭官方高码率客户端。";
            HghighProbeStatus = $"启动密钥已缓存（设备 {result.DeviceId}）。";
        }
        catch (Exception ex)
        {
            RefreshHghighMastersStatus();
            HghighMastersStatus = $"提取失败：{ex.Message}";
        }
    }

    private void RefreshHghighMastersStatus()
    {
        HghighMastersStatus = HongguoHighDeviceStore.IsReady()
            ? "本机已缓存启动密钥，可正常登录。"
            : "尚未缓存启动密钥。选择官方客户端后点「提取启动密钥」（安装包已内置 Frida）。";
    }

    public void ReadMapleleafUdid()
    {
        var deviceId = MapleleafDeviceStore.TryReadDeviceId();
        MapleleafProbeStatus = string.IsNullOrWhiteSpace(deviceId)
            ? "未在注册表中找到 HongGuoClient\\DeviceUDID。"
            : "已从注册表读取 Mapleleaf DeviceUDID。";
        if (!string.IsNullOrWhiteSpace(deviceId))
            MapleleafUdid = deviceId;
    }

    public void GenerateMapleleafUdid()
    {
        if (!string.IsNullOrWhiteSpace(MapleleafUdid))
        {
            MapleleafProbeStatus = "设备号已有值；请先清空后再生成，避免账号与旧设备解绑。";
            return;
        }
        MapleleafUdid = MapleleafDeviceStore.GenerateDeviceId();
        MapleleafProbeStatus = "已生成新的 Mapleleaf DeviceUDID。";
    }

    public async Task ProbeMapleleafLoginAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var service = new MapleleafApiService(http);
            await service.ProbeLoginAsync(GlobalDramaSettingsProvider.FromGlobal(BuildWorkingGlobalConfig()), CancellationToken.None);
            MapleleafProbeStatus = $"测试登录成功：{DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            MapleleafProbeStatus = $"测试登录失败：{ex.Message}";
        }
    }

    public async Task ProbeHongguoLocalAsync()
    {
        try
        {
            var results = await _hongguoLocalApiService.SearchAsync(GlobalDramaSettingsProvider.FromGlobal(BuildWorkingGlobalConfig()), "测试", 1, CancellationToken.None);
            HongguoLocalProbeStatus = $"hglocal 连接成功，返回 {results.Count} 条：{DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            HongguoLocalProbeStatus = $"hglocal 连接失败：{ex.Message}";
        }
    }

    public async Task ReadPikachuRuntimeAsync()
    {
        try
        {
            var result = await _hongguoMemoryReaderService.ReadRuntimeAsync(CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(result.FanqieCookie))
            {
                PikachuFanqieCookie = result.FanqieCookie;
            }

            if (!string.IsNullOrWhiteSpace(result.DeviceId))
            {
                PikachuDeviceId = result.DeviceId;
            }

            PikachuProbeStatus = result.HasAnyValue
                ? $"已从红果读取运行信息：{result.Reason}"
                : $"未读取到红果运行信息：{result.Reason}";
        }
        catch (Exception ex)
        {
            PikachuProbeStatus = $"读取红果运行信息失败：{ex.Message}";
        }
    }

    public async Task ProbePikachuAsync()
    {
        try
        {
            var count = await _dramaSourceRouter.ProbePikachuSearchAsync(GlobalDramaSettingsProvider.FromGlobal(BuildWorkingGlobalConfig()), CancellationToken.None);
            PikachuProbeStatus = $"pikachu 搜索成功，返回 {count} 条：{DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            PikachuProbeStatus = $"pikachu 搜索失败：{ex.Message}";
        }
    }

    public void LoadConfig()
    {
        WasSaved = false;
        _loadedProjectConfig = _configService.LoadProject(RootDir);
        _loadedGlobalConfig = _configService.LoadGlobal();
        var merged = _configService.Load(RootDir);

        ConfigFilePath = merged.ConfigFilePath;
        GlobalSettingsFilePath = _loadedGlobalConfig.SettingsFilePath;
        CompanyName = merged.CompanyName;
        SearchPageSize = string.IsNullOrWhiteSpace(merged.SearchPageSize) ? "20" : merged.SearchPageSize;
        TemplateDocxPath = merged.TemplateDocxPath;
        CostReportBaseImagePath = merged.CostReportBaseImagePath;
        CostReportActorPayRatio = merged.CostReportActorPayRatio;
        CostReportLegalRepresentative = merged.CostReportLegalRepresentative;
        WeixinHeadless = merged.WeixinHeadless;
        WeixinSlowMoMs = string.IsNullOrWhiteSpace(merged.WeixinSlowMoMs) ? "50" : merged.WeixinSlowMoMs;
        WeixinKeepOpenSeconds = string.IsNullOrWhiteSpace(merged.WeixinKeepOpenSeconds) ? "0" : merged.WeixinKeepOpenSeconds;
        WeixinLoginTimeoutSeconds = string.IsNullOrWhiteSpace(merged.WeixinLoginTimeoutSeconds) ? "300" : merged.WeixinLoginTimeoutSeconds;
        WeixinSubmitEnabled = merged.WeixinSubmitEnabled;
        WeixinPauseOnError = merged.WeixinPauseOnError;
        WeixinSaveHtml = merged.WeixinSaveHtml;
        WeixinSaveText = merged.WeixinSaveText;
        WeixinSubmissionReportDir = merged.WeixinSubmissionReportDir;
        WeixinMonetizationType = string.IsNullOrWhiteSpace(merged.WeixinMonetizationType) ? "IAA广告变现" : merged.WeixinMonetizationType;
        WeixinDramaType = string.IsNullOrWhiteSpace(merged.WeixinDramaType) ? "漫剧" : merged.WeixinDramaType;
        WeixinDramaQualification = string.IsNullOrWhiteSpace(merged.WeixinDramaQualification) ? "其他微短剧" : merged.WeixinDramaQualification;
        WeixinSubmitterIdentity = string.IsNullOrWhiteSpace(merged.WeixinSubmitterIdentity) ? "剧目制作方" : merged.WeixinSubmitterIdentity;
        WeixinTrialEpisodes = string.IsNullOrWhiteSpace(merged.WeixinTrialEpisodes) ? "3" : merged.WeixinTrialEpisodes;
        WeixinFillRecommendation = merged.WeixinFillRecommendation;
        DramaSourceChain = string.IsNullOrWhiteSpace(_loadedGlobalConfig.DramaSourceChain) ? "hgnew" : _loadedGlobalConfig.DramaSourceChain;
        DownloadFileSegments = string.IsNullOrWhiteSpace(_loadedGlobalConfig.DownloadFileSegments) ? "4" : _loadedGlobalConfig.DownloadFileSegments;
        XingeEnabled = _loadedGlobalConfig.XingeEnabled;
        XingeServerUrl = _loadedGlobalConfig.XingeServerUrl;
        XingeUsername = _loadedGlobalConfig.XingeUsername;
        XingePassword = _loadedGlobalConfig.XingePassword;
        XingeClientId = _loadedGlobalConfig.XingeClientId;
        XingeClientToken = _loadedGlobalConfig.XingeClientToken;
        XingeUserRole = _loadedGlobalConfig.XingeUserRole;
        XingeClientName = _loadedGlobalConfig.XingeClientName;
        XingeWsEnabled = _loadedGlobalConfig.XingeWsEnabled;
        XingePollIntervalSeconds = string.IsNullOrWhiteSpace(_loadedGlobalConfig.XingePollIntervalSeconds) ? "3" : _loadedGlobalConfig.XingePollIntervalSeconds;
        XingeUploadLoginQr = _loadedGlobalConfig.XingeUploadLoginQr;
        XingeOperationStatus = string.Empty;
        HgnewAccount = _loadedGlobalConfig.HgnewAccount;
        HgnewPassword = _loadedGlobalConfig.HgnewPassword;
        HgnewUdid = _loadedGlobalConfig.HgnewUdid;
        HgnewClientVersion = _loadedGlobalConfig.HgnewClientVersion;
        HghighAccount = _loadedGlobalConfig.HghighAccount;
        HghighPassword = _loadedGlobalConfig.HghighPassword;
        HghighDeviceId = string.IsNullOrWhiteSpace(_loadedGlobalConfig.HghighDeviceId)
            ? HongguoHighDeviceStore.TryReadDeviceId()
            : _loadedGlobalConfig.HghighDeviceId;
        HghighClientExe = _loadedGlobalConfig.HghighClientExe;
        MapleleafAccount = _loadedGlobalConfig.MapleleafAccount;
        MapleleafPassword = _loadedGlobalConfig.MapleleafPassword;
        MapleleafUdid = string.IsNullOrWhiteSpace(_loadedGlobalConfig.MapleleafUdid)
            ? MapleleafDeviceStore.TryReadDeviceId()
            : _loadedGlobalConfig.MapleleafUdid;
        RefreshHghighMastersStatus();
        HongguoDownloadTimeoutSeconds = string.IsNullOrWhiteSpace(_loadedGlobalConfig.HongguoDownloadTimeoutSeconds) ? "60" : _loadedGlobalConfig.HongguoDownloadTimeoutSeconds;
        HongguoEpisodeDownloadAttempts = string.IsNullOrWhiteSpace(_loadedGlobalConfig.HongguoEpisodeDownloadAttempts) ? "5" : _loadedGlobalConfig.HongguoEpisodeDownloadAttempts;
        HongguoLocalBaseUrl = _loadedGlobalConfig.HongguoLocalBaseUrl;
        HongguoLocalApiKey = _loadedGlobalConfig.HongguoLocalApiKey;
        HongguoLocalProbeStatus = string.Empty;
        PikachuServerUrl = _loadedGlobalConfig.PikachuServerUrl;
        PikachuFanqieCookie = _loadedGlobalConfig.PikachuFanqieCookie;
        PikachuDramaType = "manga";
        PikachuDeviceId = _loadedGlobalConfig.PikachuDeviceId;
        PikachuClientVersion = string.IsNullOrWhiteSpace(_loadedGlobalConfig.PikachuClientVersion) ? "1.4.4" : _loadedGlobalConfig.PikachuClientVersion;
        PikachuProbeStatus = string.Empty;
        AiTextEndpoint = merged.AiTextEndpoint;
        AiTextApiKey = merged.AiTextApiKey;
        AiTextModel = merged.AiTextModel;
        AiTextTimeoutSeconds = string.IsNullOrWhiteSpace(merged.AiTextTimeoutSeconds) ? "60" : merged.AiTextTimeoutSeconds;
        AiTextMaxBatchSize = string.IsNullOrWhiteSpace(merged.AiTextMaxBatchSize) ? "20" : merged.AiTextMaxBatchSize;
        AiTextSystemPrompt = merged.AiTextSystemPrompt;
        AiTextBatchPrompt = merged.AiTextBatchPrompt;
        AiTextRetryPrompt = merged.AiTextRetryPrompt;
        AiTitleSystemPrompt = string.IsNullOrWhiteSpace(merged.AiTitleSystemPrompt) ? merged.AiTextSystemPrompt : merged.AiTitleSystemPrompt;
        AiTitleBatchPrompt = string.IsNullOrWhiteSpace(merged.AiTitleBatchPrompt) ? merged.AiTextBatchPrompt : merged.AiTitleBatchPrompt;
        AiTagSystemPrompt = merged.AiTagSystemPrompt;
        AiTagBatchPrompt = merged.AiTagBatchPrompt;
        AiFullInfoSystemPrompt = string.IsNullOrWhiteSpace(merged.AiFullInfoSystemPrompt) ? merged.AiTextSystemPrompt : merged.AiFullInfoSystemPrompt;
        AiFullInfoBatchPrompt = string.IsNullOrWhiteSpace(merged.AiFullInfoBatchPrompt) ? merged.AiTextBatchPrompt : merged.AiFullInfoBatchPrompt;
        AiFullInfoRetryPrompt = string.IsNullOrWhiteSpace(merged.AiFullInfoRetryPrompt) ? merged.AiTextRetryPrompt : merged.AiFullInfoRetryPrompt;
        ImageModelId = merged.ImageModelId;
        ImageModelApiKey = merged.ImageModelApiKey;
        ImageModelEndpoint = merged.ImageModelEndpoint;
        FrameCoverPrompt = merged.FrameCoverPrompt;
        PosterLayoutDetectPrompt = merged.PosterLayoutDetectPrompt;
        PosterInpaintPrompt = merged.PosterInpaintPrompt;
        PosterInpaintSafeRetryPrompt = merged.PosterInpaintSafeRetryPrompt;
        PosterGenerationPrompt = merged.PosterGenerationPrompt;
        PosterGenerationSafeRetryPrompt = merged.PosterGenerationSafeRetryPrompt;
        PosterNameSystemPrompt = merged.PosterNameSystemPrompt;
        PosterNameUserPrompt = merged.PosterNameUserPrompt;
        FeishuNotificationEnabled = _loadedGlobalConfig.FeishuNotificationEnabled;
        FeishuAppId = _loadedGlobalConfig.FeishuAppId;
        FeishuAppSecret = _loadedGlobalConfig.FeishuAppSecret;
        FeishuReceiveId = _loadedGlobalConfig.FeishuReceiveId;
        FeishuReceiveIdType = string.IsNullOrWhiteSpace(_loadedGlobalConfig.FeishuReceiveIdType) ? "chat_id" : _loadedGlobalConfig.FeishuReceiveIdType;
        FeishuNotifyOnStepStart = _loadedGlobalConfig.FeishuNotifyOnStepStart;
        FeishuNotifyOnStepSuccess = _loadedGlobalConfig.FeishuNotifyOnStepSuccess;
        FeishuNotifyOnStepFailure = _loadedGlobalConfig.FeishuNotifyOnStepFailure;
        FeishuNotifyOnQueueSummary = _loadedGlobalConfig.FeishuNotifyOnQueueSummary;
        FeishuNotifyOnLoginQr = _loadedGlobalConfig.FeishuNotifyOnLoginQr;
        FeishuNotifyStepKeysText = string.IsNullOrWhiteSpace(_loadedGlobalConfig.FeishuNotifyStepKeysText)
            ? "download\ntranscode\nrewrite\nposter-rename\nproject-image\ncost-report\nbatch-file-rename\nmaterial-convert\nweixin-upload\nweixin-material-upload"
            : _loadedGlobalConfig.FeishuNotifyStepKeysText;
        ProjectImageCount = string.IsNullOrWhiteSpace(_loadedProjectConfig.ProjectImageCount) ? "4" : _loadedProjectConfig.ProjectImageCount;
        ProjectImageTemplateRoot = ResolveInitialTemplateRoot();
        RefreshProjectImageTemplateOptions(_loadedProjectConfig.ProjectImageTemplateId);
    }

    public bool Save()
    {

        var project = _loadedProjectConfig with
        {
            ConfigFilePath = DesktopConfigService.GetConfigFilePath(RootDir),
            CompanyName = CompanyName.Trim(),
            SearchPageSize = SearchPageSize.Trim(),
            TemplateDocxPath = TemplateDocxPath.Trim(),
            CostReportBaseImagePath = CostReportBaseImagePath.Trim(),
            CostReportActorPayRatio = CostReportActorPayRatio.Trim(),
            CostReportLegalRepresentative = CostReportLegalRepresentative.Trim(),
            WeixinHeadless = WeixinHeadless,
            WeixinSlowMoMs = WeixinSlowMoMs.Trim(),
            WeixinKeepOpenSeconds = WeixinKeepOpenSeconds.Trim(),
            WeixinLoginTimeoutSeconds = WeixinLoginTimeoutSeconds.Trim(),
            WeixinSubmitEnabled = WeixinSubmitEnabled,
            WeixinPauseOnError = WeixinPauseOnError,
            WeixinSaveHtml = WeixinSaveHtml,
            WeixinSaveText = WeixinSaveText,
            WeixinSubmissionReportDir = WeixinSubmissionReportDir.Trim(),
            WeixinMonetizationType = WeixinMonetizationType.Trim(),
            WeixinDramaType = WeixinDramaType.Trim(),
            WeixinDramaQualification = WeixinDramaQualification.Trim(),
            WeixinSubmitterIdentity = WeixinSubmitterIdentity.Trim(),
            WeixinTrialEpisodes = WeixinTrialEpisodes.Trim(),
            WeixinFillRecommendation = WeixinFillRecommendation,
            ProjectImageGenerationMode = "image_template",
            ProjectImageTemplateRoot = ProjectImageTemplateRoot.Trim(),
            ProjectImageTemplateId = SelectedProjectImageTemplateOption?.Key ?? _loadedProjectConfig.ProjectImageTemplateId,
            ProjectImageTemplateDir = ProjectImageTemplateDir.Trim(),
            ProjectImageCount = ProjectImageCount.Trim()
        };

        var global = _loadedGlobalConfig with
        {
            DramaSourceChain = DramaSourceChain.Trim(),
            DownloadFileSegments = DownloadFileSegments.Trim(),
            XingeEnabled = XingeEnabled,
            XingeServerUrl = XingeServerUrl.Trim(),
            XingeUsername = XingeUsername.Trim(),
            XingePassword = XingePassword,
            XingeClientId = XingeClientId.Trim(),
            XingeClientToken = XingeClientToken.Trim(),
            XingeUserRole = XingeUserRole.Trim(),
            XingeClientName = XingeClientName.Trim(),
            XingeWsEnabled = XingeWsEnabled,
            XingePollIntervalSeconds = XingePollIntervalSeconds.Trim(),
            XingeUploadLoginQr = XingeUploadLoginQr,
            HgnewAccount = HgnewAccount.Trim(),
            HgnewPassword = HgnewPassword,
            HgnewUdid = HongguoDeviceId.Normalize(HgnewUdid),
            HgnewClientVersion = HongguoClientVersion.Normalize(HgnewClientVersion),
            HghighAccount = HghighAccount.Trim(),
            HghighPassword = HghighPassword,
            HghighDeviceId = HghighDeviceId.Trim(),
            HghighClientExe = HghighClientExe.Trim(),
            MapleleafAccount = MapleleafAccount.Trim(),
            MapleleafPassword = MapleleafPassword,
            MapleleafUdid = MapleleafUdid.Trim(),
            HongguoDownloadTimeoutSeconds = HongguoDownloadTimeoutSeconds.Trim(),
            HongguoEpisodeDownloadAttempts = HongguoEpisodeDownloadAttempts.Trim(),
            HongguoLocalBaseUrl = HongguoLocalBaseUrl.Trim(),
            HongguoLocalApiKey = HongguoLocalApiKey.Trim(),
            PikachuServerUrl = PikachuServerUrl.Trim(),
            PikachuFanqieCookie = PikachuFanqieCookie.Trim(),
            PikachuDramaType = "manga",
            PikachuDeviceId = PikachuDeviceId.Trim(),
            PikachuClientVersion = PikachuClientVersion.Trim(),
            AiTextEndpoint = AiTextEndpoint.Trim(),
            AiTextApiKey = AiTextApiKey.Trim(),
            AiTextModel = AiTextModel.Trim(),
            AiTextTimeoutSeconds = AiTextTimeoutSeconds.Trim(),
            AiTextMaxBatchSize = AiTextMaxBatchSize.Trim(),
            AiTextSystemPrompt = (string.IsNullOrWhiteSpace(AiFullInfoSystemPrompt) ? AiTextSystemPrompt : AiFullInfoSystemPrompt).Trim(),
            AiTextBatchPrompt = (string.IsNullOrWhiteSpace(AiFullInfoBatchPrompt) ? AiTextBatchPrompt : AiFullInfoBatchPrompt).Trim(),
            AiTextRetryPrompt = (string.IsNullOrWhiteSpace(AiFullInfoRetryPrompt) ? AiTextRetryPrompt : AiFullInfoRetryPrompt).Trim(),
            AiTitleSystemPrompt = AiTitleSystemPrompt.Trim(),
            AiTitleBatchPrompt = AiTitleBatchPrompt.Trim(),
            AiTagSystemPrompt = AiTagSystemPrompt.Trim(),
            AiTagBatchPrompt = AiTagBatchPrompt.Trim(),
            AiFullInfoSystemPrompt = AiFullInfoSystemPrompt.Trim(),
            AiFullInfoBatchPrompt = AiFullInfoBatchPrompt.Trim(),
            AiFullInfoRetryPrompt = AiFullInfoRetryPrompt.Trim(),
            ImageModelId = ImageModelId.Trim(),
            ImageModelApiKey = ImageModelApiKey.Trim(),
            ImageModelEndpoint = ImageModelEndpoint.Trim(),
            FrameCoverPrompt = FrameCoverPrompt.Trim(),
            PosterLayoutDetectPrompt = PosterLayoutDetectPrompt.Trim(),
            PosterInpaintPrompt = PosterInpaintPrompt.Trim(),
            PosterInpaintSafeRetryPrompt = PosterInpaintSafeRetryPrompt.Trim(),
            PosterGenerationPrompt = PosterGenerationPrompt.Trim(),
            PosterGenerationSafeRetryPrompt = PosterGenerationSafeRetryPrompt.Trim(),
            PosterNameSystemPrompt = PosterNameSystemPrompt.Trim(),
            PosterNameUserPrompt = PosterNameUserPrompt.Trim(),
            FeishuNotificationEnabled = FeishuNotificationEnabled,
            FeishuAppId = FeishuAppId.Trim(),
            FeishuAppSecret = FeishuAppSecret.Trim(),
            FeishuReceiveId = FeishuReceiveId.Trim(),
            FeishuReceiveIdType = FeishuReceiveIdType.Trim(),
            FeishuNotifyOnStepStart = FeishuNotifyOnStepStart,
            FeishuNotifyOnStepSuccess = FeishuNotifyOnStepSuccess,
            FeishuNotifyOnStepFailure = FeishuNotifyOnStepFailure,
            FeishuNotifyOnQueueSummary = FeishuNotifyOnQueueSummary,
            FeishuNotifyOnLoginQr = FeishuNotifyOnLoginQr,
            FeishuNotifyStepKeysText = FeishuNotifyStepKeysText.Trim()
        };

        _configService.Save(project, global);
        _loadedProjectConfig = project;
        _loadedGlobalConfig = global;
        ConfigFilePath = project.ConfigFilePath;
        GlobalSettingsFilePath = global.SettingsFilePath;
        WasSaved = true;
        return true;
    }


    private void RefreshProjectImageTemplateOptions(string? preferredTemplateId = null)
    {
        _templateDescriptors = ProjectImageTemplateCatalog.Discover(ProjectImageTemplateRoot)
            .ToDictionary(item => item.Id, item => item, StringComparer.OrdinalIgnoreCase);

        ProjectImageTemplateOptions.Clear();
        foreach (var descriptor in _templateDescriptors.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            ProjectImageTemplateOptions.Add(new WorkflowStepOption(descriptor.Id, $"{descriptor.Name} ({descriptor.Id})"));
        }

        var targetId = string.IsNullOrWhiteSpace(preferredTemplateId)
            ? _loadedProjectConfig.ProjectImageTemplateId
            : preferredTemplateId;

        SelectedProjectImageTemplateOption = ProjectImageTemplateOptions.FirstOrDefault(item =>
            string.Equals(item.Key, targetId, StringComparison.OrdinalIgnoreCase))
            ?? ProjectImageTemplateOptions.FirstOrDefault();

        if (SelectedProjectImageTemplateOption is null)
        {
            ProjectImageTemplateDir = ProjectImageTemplateRoot;
        }
    }

    private string ResolveInitialTemplateRoot()
    {
        return ProjectImageTemplateCatalog.ResolveTemplateRoot(
            _loadedProjectConfig.ProjectImageTemplateRoot,
            _loadedProjectConfig.ProjectImageTemplateDir,
            RootDir);
    }

    private void OpenConfigFile()
    {
        _shellService.TryRevealPath(ConfigFilePath, out _);
    }

    private void OpenGlobalSettingsFile()
    {
        _shellService.TryRevealPath(GlobalSettingsFilePath, out _);
    }

    private GlobalConfigSnapshot BuildWorkingGlobalConfig()
    {
        return _loadedGlobalConfig with
        {
            DownloadFileSegments = DownloadFileSegments.Trim(),
            HgnewAccount = HgnewAccount.Trim(),
            HgnewPassword = HgnewPassword,
            HgnewUdid = HongguoDeviceId.Normalize(HgnewUdid),
            HgnewClientVersion = HongguoClientVersion.Normalize(HgnewClientVersion),
            HghighAccount = HghighAccount.Trim(),
            HghighPassword = HghighPassword,
            HghighDeviceId = HghighDeviceId.Trim(),
            HghighClientExe = HghighClientExe.Trim(),
            MapleleafAccount = MapleleafAccount.Trim(),
            MapleleafPassword = MapleleafPassword,
            MapleleafUdid = MapleleafUdid.Trim(),
            HongguoDownloadTimeoutSeconds = HongguoDownloadTimeoutSeconds.Trim(),
            HongguoEpisodeDownloadAttempts = HongguoEpisodeDownloadAttempts.Trim(),
            HongguoLocalBaseUrl = HongguoLocalBaseUrl.Trim(),
            HongguoLocalApiKey = HongguoLocalApiKey.Trim(),
            PikachuServerUrl = PikachuServerUrl.Trim(),
            PikachuFanqieCookie = PikachuFanqieCookie.Trim(),
            PikachuDramaType = "manga",
            PikachuDeviceId = PikachuDeviceId.Trim(),
            PikachuClientVersion = PikachuClientVersion.Trim()
        };
    }

    public async Task<bool> RefreshXingeCredentialsAsync()
    {
        try
        {
            var global = _loadedGlobalConfig with
            {
                XingeEnabled = XingeEnabled,
                XingeServerUrl = XingeServerUrl.Trim(),
                XingeUsername = XingeUsername.Trim(),
                XingePassword = XingePassword,
                XingeClientId = XingeClientId.Trim(),
                XingeClientToken = XingeClientToken.Trim(),
                XingeUserRole = XingeUserRole.Trim(),
                XingeClientName = XingeClientName.Trim(),
                XingeWsEnabled = XingeWsEnabled,
                XingePollIntervalSeconds = XingePollIntervalSeconds.Trim(),
                XingeUploadLoginQr = XingeUploadLoginQr
            };

            var result = await _xingeRemoteControlService.FetchClientCredentialsAsync(global, CancellationToken.None);
            _loadedGlobalConfig = result.UpdatedGlobalConfig;
            XingeServerUrl = result.UpdatedGlobalConfig.XingeServerUrl;
            XingeClientId = result.LoginResult.ClientId;
            XingeClientToken = result.LoginResult.ClientToken;
            XingeUserRole = result.LoginResult.UserRole;
            XingeOperationStatus = $"已获取客户端凭证并通过连接测试，角色：{(string.IsNullOrWhiteSpace(result.LoginResult.UserRole) ? "unknown" : result.LoginResult.UserRole)}";
            _configService.SaveGlobal(_loadedGlobalConfig);
            GlobalSettingsFilePath = _loadedGlobalConfig.SettingsFilePath;
            return true;
        }
        catch (Exception ex)
        {
            XingeOperationStatus = ex.Message;
            return false;
        }
    }


    partial void OnSelectedProjectImageTemplateOptionChanged(WorkflowStepOption? value)
    {
        if (value is null || !_templateDescriptors.TryGetValue(value.Key, out var descriptor))
        {
            return;
        }

        ProjectImageTemplateDir = descriptor.TemplateDirectory;
        if (descriptor.Count > 0)
        {
            ProjectImageCount = descriptor.Count.ToString();
        }
    }
}
