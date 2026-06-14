using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShortDrama.Desktop.Models;
using ShortDrama.Desktop.Services;
using ShortDrama.Infrastructure.Imaging;
using System.Collections.ObjectModel;

namespace ShortDrama.Desktop.ViewModels;

public partial class ConfigWindowViewModel : ViewModelBase
{
    private readonly DesktopConfigService _configService;
    private readonly DesktopShellService _shellService;
    private readonly XingeRemoteControlService _xingeRemoteControlService;
    private ProjectConfigSnapshot _loadedProjectConfig;
    private GlobalConfigSnapshot _loadedGlobalConfig;
    private Dictionary<string, ProjectImageTemplateDescriptor> _templateDescriptors = new(StringComparer.OrdinalIgnoreCase);

    public ConfigWindowViewModel(
        string rootDir,
        DesktopConfigService configService,
        DesktopShellService shellService,
        XingeRemoteControlService xingeRemoteControlService)
    {
        _configService = configService;
        _shellService = shellService;
        _xingeRemoteControlService = xingeRemoteControlService;

        ValidateConfigCommand = new RelayCommand(Validate);
        ReloadConfigCommand = new RelayCommand(LoadConfig);
        OpenConfigFileCommand = new RelayCommand(OpenConfigFile);
        OpenGlobalSettingsFileCommand = new RelayCommand(OpenGlobalSettingsFile);
        RefreshXingeCredentialsCommand = new AsyncRelayCommand(RefreshXingeCredentialsAsync);

        _loadedProjectConfig = configService.LoadProject(rootDir);
        _loadedGlobalConfig = configService.LoadGlobal();

        SetRootDir(rootDir);
    }

    public ObservableCollection<ConfigValidationItem> ConfigIssues { get; } = [];
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
        "short",
        "manga"
    ];

    public IReadOnlyList<string> FeishuReceiveIdTypeOptions { get; } =
    [
        "chat_id",
        "open_id",
        "user_id",
        "email"
    ];

    public IRelayCommand ValidateConfigCommand { get; }
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
    private string configValidationSummary = "未校验";

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
    private string dramaServiceOrderSearch = string.Empty;

    [ObservableProperty]
    private string dramaServiceOrderDownload = string.Empty;

    [ObservableProperty]
    private string dramaServiceOrderNewRelease = string.Empty;

    [ObservableProperty]
    private string dramaServiceOrderRanking = string.Empty;

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
    private string hongguoLocalBaseUrl = string.Empty;

    [ObservableProperty]
    private string hongguoLocalApiKey = string.Empty;

    [ObservableProperty]
    private string pikachuServerUrl = string.Empty;

    [ObservableProperty]
    private string pikachuFanqieCookie = string.Empty;

    [ObservableProperty]
    private string pikachuDramaType = "short";

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
    private string imageModelId = string.Empty;

    [ObservableProperty]
    private string imageModelApiKey = string.Empty;

    [ObservableProperty]
    private string imageModelEndpoint = string.Empty;

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
        DramaServiceOrderSearch = _loadedGlobalConfig.DramaServiceOrderSearch;
        DramaServiceOrderDownload = _loadedGlobalConfig.DramaServiceOrderDownload;
        DramaServiceOrderNewRelease = _loadedGlobalConfig.DramaServiceOrderNewRelease;
        DramaServiceOrderRanking = _loadedGlobalConfig.DramaServiceOrderRanking;
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
        HongguoLocalBaseUrl = _loadedGlobalConfig.HongguoLocalBaseUrl;
        HongguoLocalApiKey = _loadedGlobalConfig.HongguoLocalApiKey;
        PikachuServerUrl = _loadedGlobalConfig.PikachuServerUrl;
        PikachuFanqieCookie = _loadedGlobalConfig.PikachuFanqieCookie;
        PikachuDramaType = _loadedGlobalConfig.PikachuDramaType;
        AiTextEndpoint = merged.AiTextEndpoint;
        AiTextApiKey = merged.AiTextApiKey;
        AiTextModel = merged.AiTextModel;
        AiTextTimeoutSeconds = string.IsNullOrWhiteSpace(merged.AiTextTimeoutSeconds) ? "60" : merged.AiTextTimeoutSeconds;
        AiTextMaxBatchSize = string.IsNullOrWhiteSpace(merged.AiTextMaxBatchSize) ? "20" : merged.AiTextMaxBatchSize;
        AiTextSystemPrompt = merged.AiTextSystemPrompt;
        AiTextBatchPrompt = merged.AiTextBatchPrompt;
        AiTextRetryPrompt = merged.AiTextRetryPrompt;
        ImageModelId = merged.ImageModelId;
        ImageModelApiKey = merged.ImageModelApiKey;
        ImageModelEndpoint = merged.ImageModelEndpoint;
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
        FeishuNotifyStepKeysText = string.IsNullOrWhiteSpace(_loadedGlobalConfig.FeishuNotifyStepKeysText)
            ? "download\ntranscode\nrewrite\nposter-rename\nproject-image\ncost-report\nbatch-file-rename\nmaterial-convert\nweixin-upload\nweixin-material-upload"
            : _loadedGlobalConfig.FeishuNotifyStepKeysText;
        ProjectImageCount = string.IsNullOrWhiteSpace(_loadedProjectConfig.ProjectImageCount) ? "4" : _loadedProjectConfig.ProjectImageCount;
        ProjectImageTemplateRoot = ResolveInitialTemplateRoot();
        RefreshProjectImageTemplateOptions(_loadedProjectConfig.ProjectImageTemplateId);
        Validate();
    }

    public bool Save()
    {
        Validate();
        if (ConfigIssues.Any(item => string.Equals(item.Severity, "错误", StringComparison.Ordinal)))
        {
            return false;
        }

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
            DramaServiceOrderSearch = DramaServiceOrderSearch.Trim(),
            DramaServiceOrderDownload = DramaServiceOrderDownload.Trim(),
            DramaServiceOrderNewRelease = DramaServiceOrderNewRelease.Trim(),
            DramaServiceOrderRanking = DramaServiceOrderRanking.Trim(),
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
            HgnewUdid = HgnewUdid.Trim().ToUpperInvariant(),
            HgnewClientVersion = HgnewClientVersion.Trim(),
            HongguoLocalBaseUrl = HongguoLocalBaseUrl.Trim(),
            HongguoLocalApiKey = HongguoLocalApiKey.Trim(),
            PikachuServerUrl = PikachuServerUrl.Trim(),
            PikachuFanqieCookie = PikachuFanqieCookie.Trim(),
            PikachuDramaType = PikachuDramaType.Trim(),
            AiTextEndpoint = AiTextEndpoint.Trim(),
            AiTextApiKey = AiTextApiKey.Trim(),
            AiTextModel = AiTextModel.Trim(),
            AiTextTimeoutSeconds = AiTextTimeoutSeconds.Trim(),
            AiTextMaxBatchSize = AiTextMaxBatchSize.Trim(),
            AiTextSystemPrompt = AiTextSystemPrompt.Trim(),
            AiTextBatchPrompt = AiTextBatchPrompt.Trim(),
            AiTextRetryPrompt = AiTextRetryPrompt.Trim(),
            ImageModelId = ImageModelId.Trim(),
            ImageModelApiKey = ImageModelApiKey.Trim(),
            ImageModelEndpoint = ImageModelEndpoint.Trim(),
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
            FeishuNotifyStepKeysText = FeishuNotifyStepKeysText.Trim()
        };

        _configService.Save(project, global);
        _loadedProjectConfig = project;
        _loadedGlobalConfig = global;
        ConfigFilePath = project.ConfigFilePath;
        GlobalSettingsFilePath = global.SettingsFilePath;
        WasSaved = true;
        Validate();
        return true;
    }

    private void Validate()
    {
        ConfigIssues.Clear();

        if (string.IsNullOrWhiteSpace(RootDir))
        {
            AddIssue("错误", "未选择工作目录。");
        }

        if (!Directory.Exists(RootDir))
        {
            AddIssue("错误", $"工作目录不存在：{RootDir}");
        }

        if (!int.TryParse(SearchPageSize, out var pageSize) || pageSize <= 0)
        {
            AddIssue("错误", "SearchPageSize 必须是大于 0 的整数。");
        }

        if (!int.TryParse(WeixinSlowMoMs, out var slowMoMs) || slowMoMs < 0)
        {
            AddIssue("错误", "WeixinSlowMoMs 必须是不小于 0 的整数。");
        }

        if (!int.TryParse(WeixinKeepOpenSeconds, out var keepOpenSeconds) || keepOpenSeconds < 0)
        {
            AddIssue("错误", "WeixinKeepOpenSeconds 必须是不小于 0 的整数。");
        }

        if (!int.TryParse(WeixinLoginTimeoutSeconds, out var loginTimeoutSeconds) || loginTimeoutSeconds <= 0)
        {
            AddIssue("错误", "WeixinLoginTimeoutSeconds 必须是大于 0 的整数。");
        }

        if (!int.TryParse(WeixinTrialEpisodes, out var trialEpisodes) || trialEpisodes <= 0)
        {
            AddIssue("错误", "WeixinTrialEpisodes 必须是大于 0 的整数。");
        }

        if (!int.TryParse(AiTextTimeoutSeconds, out var aiTimeoutSeconds) || aiTimeoutSeconds <= 0)
        {
            AddIssue("错误", "AiTextTimeoutSeconds 必须是大于 0 的整数。");
        }

        if (!int.TryParse(AiTextMaxBatchSize, out var aiBatchSize) || aiBatchSize <= 0)
        {
            AddIssue("错误", "AiTextMaxBatchSize 必须是大于 0 的整数。");
        }

        if (!int.TryParse(ProjectImageCount, out var projectImageCount) || projectImageCount <= 0)
        {
            AddIssue("错误", "ProjectImageCount 必须是大于 0 的整数。");
        }

        if (string.IsNullOrWhiteSpace(ProjectImageTemplateRoot))
        {
            AddIssue("错误", "未配置工程图模板根目录。");
        }
        else if (!Directory.Exists(ProjectImageTemplateRoot))
        {
            AddIssue("错误", $"工程图模板根目录不存在：{ProjectImageTemplateRoot}");
        }

        if (string.IsNullOrWhiteSpace(ProjectImageTemplateDir))
        {
            AddIssue("错误", "未选择工程图截图模板。");
        }
        else
        {
            ValidateTemplateDirectory(ProjectImageTemplateDir, projectImageCount);
        }

        if (string.IsNullOrWhiteSpace(DramaServiceOrderSearch))
        {
            AddIssue("警告", "DramaServiceOrderSearch 为空，将使用默认顺序 hgnew,hglocal,pikachu。");
        }

        if (XingeEnabled && string.IsNullOrWhiteSpace(XingeServerUrl))
        {
            AddIssue("错误", "已启用 Xinge，但未配置 Xinge 服务地址。");
        }

        if (!string.IsNullOrWhiteSpace(XingePollIntervalSeconds) &&
            (!int.TryParse(XingePollIntervalSeconds, out var xingePollIntervalSeconds) || xingePollIntervalSeconds <= 0))
        {
            AddIssue("错误", "XingePollIntervalSeconds 必须是大于 0 的整数。");
        }

        if (XingeEnabled &&
            string.IsNullOrWhiteSpace(XingeClientId) &&
            string.IsNullOrWhiteSpace(XingeClientToken) &&
            (string.IsNullOrWhiteSpace(XingeUsername) || string.IsNullOrWhiteSpace(XingePassword)))
        {
            AddIssue("警告", "Xinge 未配置客户端凭证，且用户名/密码不完整，执行同步时会失败。");
        }

        if (string.IsNullOrWhiteSpace(HgnewAccount) || string.IsNullOrWhiteSpace(HgnewPassword) || string.IsNullOrWhiteSpace(HgnewUdid))
        {
            AddIssue("警告", "Hgnew 凭证未填写完整，使用 hgnew 时搜索/下载会失败。");
        }

        if (string.IsNullOrWhiteSpace(HongguoLocalBaseUrl))
        {
            AddIssue("警告", "Hglocal Base URL 为空，使用 hglocal 时搜索/下载会失败。");
        }

        if (string.IsNullOrWhiteSpace(PikachuServerUrl))
        {
            AddIssue("警告", "Pikachu Server URL 为空，使用 pikachu 时搜索/下载会失败。");
        }

        if (string.IsNullOrWhiteSpace(AiTextEndpoint))
        {
            AddIssue("警告", "AiTextEndpoint 为空，将回退到文本模型地址。");
        }

        if (string.IsNullOrWhiteSpace(ImageModelId))
        {
            AddIssue("错误", "ImageModelId 为空，海报生成无法执行。");
        }

        if (FeishuNotificationEnabled)
        {
            if (string.IsNullOrWhiteSpace(FeishuAppId))
            {
                AddIssue("错误", "启用飞书通知时必须填写 FeishuAppId。");
            }

            if (string.IsNullOrWhiteSpace(FeishuAppSecret))
            {
                AddIssue("错误", "启用飞书通知时必须填写 FeishuAppSecret。");
            }

            if (string.IsNullOrWhiteSpace(FeishuReceiveId))
            {
                AddIssue("错误", "启用飞书通知时必须填写 FeishuReceiveId。");
            }

            if (string.IsNullOrWhiteSpace(FeishuReceiveIdType))
            {
                AddIssue("错误", "启用飞书通知时必须填写 FeishuReceiveIdType。");
            }

            if (string.IsNullOrWhiteSpace(FeishuNotifyStepKeysText))
            {
                AddIssue("警告", "FeishuNotifyStepKeysText 为空时，不会发送任何步骤通知。");
            }
        }

        if (!string.IsNullOrWhiteSpace(TemplateDocxPath) && !File.Exists(TemplateDocxPath))
        {
            AddIssue("警告", $"成本报表模板不存在：{TemplateDocxPath}");
        }

        if (!string.IsNullOrWhiteSpace(CostReportBaseImagePath) && !File.Exists(CostReportBaseImagePath))
        {
            AddIssue("警告", $"成本报表底图不存在：{CostReportBaseImagePath}");
        }

        FinalizeValidationSummary();
    }

    private void ValidateTemplateDirectory(string templateDirectory, int expectedCount)
    {
        try
        {
            var manifest = ProjectImageTemplateManifest.Load(templateDirectory);
            if (manifest.Templates.Count == 0)
            {
                AddIssue("错误", $"工程图模板不包含任何页面：{templateDirectory}");
                return;
            }

            if (manifest.Count < expectedCount)
            {
                AddIssue("错误", $"工程图模板页面数量不足：期望 {expectedCount}，模板仅提供 {manifest.Count} 页。");
            }

            foreach (var page in manifest.Templates.Take(expectedCount))
            {
                var pagePath = Path.Combine(templateDirectory, page.File);
                if (!File.Exists(pagePath))
                {
                    AddIssue("错误", $"工程图模板缺少页面图片：{pagePath}");
                }

                foreach (var requiredRegion in new[] { "player", "material_panel", "timeline_strip" })
                {
                    if (!page.Regions.ContainsKey(requiredRegion))
                    {
                        AddIssue("错误", $"工程图模板页面缺少关键区域 {requiredRegion}：{page.File}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AddIssue("错误", $"工程图模板无效：{ex.Message}");
        }
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
        if (!string.IsNullOrWhiteSpace(_loadedProjectConfig.ProjectImageTemplateRoot))
        {
            return _loadedProjectConfig.ProjectImageTemplateRoot;
        }

        if (!string.IsNullOrWhiteSpace(_loadedProjectConfig.ProjectImageTemplateDir))
        {
            return _loadedProjectConfig.ProjectImageTemplateDir;
        }

        var candidates = new[]
        {
            Path.Combine(RootDir, "templates", "project-image"),
            Path.Combine(AppContext.BaseDirectory, "templates", "project-image"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "templates", "project-image"))
        };

        return candidates.FirstOrDefault(Directory.Exists) ?? string.Empty;
    }

    private void OpenConfigFile()
    {
        _shellService.TryRevealPath(ConfigFilePath, out _);
    }

    private void OpenGlobalSettingsFile()
    {
        _shellService.TryRevealPath(GlobalSettingsFilePath, out _);
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
            Validate();
            return true;
        }
        catch (Exception ex)
        {
            XingeOperationStatus = ex.Message;
            Validate();
            return false;
        }
    }

    private void AddIssue(string severity, string message)
    {
        ConfigIssues.Add(new ConfigValidationItem(severity, message));
    }

    private void FinalizeValidationSummary()
    {
        var errorCount = ConfigIssues.Count(item => string.Equals(item.Severity, "错误", StringComparison.Ordinal));
        var warningCount = ConfigIssues.Count(item => string.Equals(item.Severity, "警告", StringComparison.Ordinal));

        ConfigValidationSummary = errorCount == 0 && warningCount == 0
            ? "配置校验通过"
            : $"配置校验：错误 {errorCount} 项，警告 {warningCount} 项";
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
