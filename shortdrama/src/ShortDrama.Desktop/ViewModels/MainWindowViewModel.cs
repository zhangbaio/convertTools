using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Desktop.Models;
using ShortDrama.Desktop.Services;
using ShortDrama.Infrastructure.Automation;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShortDrama.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private const int DefaultSearchPageSize = 20;
    private const int MinimumUploadVideoBitrate = 4_194_304;
    private const string AllProjectsFilterKey = "__all_projects";
    private const string AllStepsFilterKey = "__all_steps";
    private const string SystemStepFilterKey = "__system";
    private const string SearchModeKeyword = "search";
    private const string SearchModeToday = "today";
    private const string SearchModeMangaToday = "manga_today";
    private const string SearchModeAiToday = "ai_today";
    private const string SearchModeHistory = "history";
    private const string EpisodeRangeAll = "all";
    private const string EpisodeRangeFirst3 = "first-3";
    private const string EpisodeRangeCustom = "custom";
    private const string ExecutionModeSerial = "serial";
    private const string ExecutionModeConcurrent2 = "concurrent-2";
    private const string TaskQueueDetailDownload = "download";
    private const string TaskQueueDetailProjectMaterial = "project-material";
    private const string TaskQueueDetailEpisodeUpload = "episode-upload";
    private const string TaskQueueDetailMaterialUpload = "material-upload";
    private static readonly string[] WeixinUploadConfigNames =
    [
        "weixin-channel-autogen.json",
        "weixin-channel-submit.json",
        "weixin-channel-config.json"
    ];
    private static readonly (string Key, string Label)[] ProjectMaterialPipelineSteps =
    [
        ("transcode", "瑙嗛杞爜"),
        ("rewrite", "浠垮啓鍓у悕绠€浠?),
        ("poster-rename", "鐢熸垚娴锋姤鍥剧墖"),
        ("project-image", "鐢熸垚宸ョ▼鍥?),
        ("cost-report", "鐢熸垚鎴愭湰鎶ヨ〃"),
        ("batch-file-rename", "閲嶅懡鍚嶈棰戞枃浠?),
        ("material-convert", "杞崲绱犳潗瑙嗛")
    ];

    private readonly IProjectScanner _projectScanner;
    private readonly IProjectArchiveService _projectArchiveService;
    private readonly IArchivedProjectDeleteService _archivedProjectDeleteService;
    private readonly IMaterialValidationService _materialValidationService;
    private readonly IWorkService _workService;
    private readonly IDramaSearchService _dramaSearchService;
    private readonly IDramaProjectBootstrapper _projectBootstrapper;
    private readonly IDramaDownloader _dramaDownloader;
    private readonly DesktopConfigService _configService;
    private readonly DesktopStateService _stateService;
    private readonly DesktopShellService _shellService;
    private readonly XingeRemoteControlService _xingeRemoteControlService;
    private readonly IWorkflowInteractionService _interactionService;
    private readonly IWeixinBrowserSessionLauncher _weixinBrowserSessionLauncher;
    private readonly IFeishuNotificationService _feishuNotificationService;
    private readonly List<ActivityLogEntry> _allActivityLogs = [];
    private static readonly string[] DownloadVideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];
    private static readonly Regex DownloadEpisodeNameRegex = new(@"绗琝s*0*(\d+)\s*闆?, RegexOptions.Compiled);
    private static readonly Regex DownloadTrailingNumberRegex = new(@"(\d+)(?!.*\d)", RegexOptions.Compiled);
    private CancellationTokenSource? _currentOperationCts;
    private WorkflowInteractionRequest? _currentInteractionRequest;
    private string _searchMode = SearchModeKeyword;
    private string _lastSearchKeyword = string.Empty;
    private bool _startupScanTriggered;
    private string _costReportBaseImagePath = string.Empty;
    private string _costReportActorPayRatio = string.Empty;
    private string _costReportLegalRepresentative = string.Empty;
    private readonly List<DramaSearchItem> _loadedSearchItems = [];

    public MainWindowViewModel(
        IProjectScanner projectScanner,
        IProjectArchiveService projectArchiveService,
        IArchivedProjectDeleteService archivedProjectDeleteService,
        IMaterialValidationService materialValidationService,
        IWorkService workService,
        IDramaSearchService dramaSearchService,
        IDramaProjectBootstrapper projectBootstrapper,
        IDramaDownloader dramaDownloader,
        DesktopConfigService configService,
        DesktopStateService stateService,
        DesktopShellService shellService,
        XingeRemoteControlService xingeRemoteControlService,
        IWorkflowInteractionService interactionService,
        IWeixinBrowserSessionLauncher weixinBrowserSessionLauncher,
        IFeishuNotificationService feishuNotificationService)
    {
        _projectScanner = projectScanner;
        _projectArchiveService = projectArchiveService;
        _archivedProjectDeleteService = archivedProjectDeleteService;
        _materialValidationService = materialValidationService;
        _workService = workService;
        _dramaSearchService = dramaSearchService;
        _projectBootstrapper = projectBootstrapper;
        _dramaDownloader = dramaDownloader;
        _configService = configService;
        _stateService = stateService;
        _shellService = shellService;
        _xingeRemoteControlService = xingeRemoteControlService;
        _interactionService = interactionService;
        _weixinBrowserSessionLauncher = weixinBrowserSessionLauncher;
        _feishuNotificationService = feishuNotificationService;

        ScanCommand = new AsyncRelayCommand(ScanAsync, CanScan);
        RunSelectedProjectCommand = new AsyncRelayCommand(RunSelectedProjectAsync, CanRunSelectedProject);
        RunRootWorkflowCommand = new AsyncRelayCommand(RunRootWorkflowAsync, CanRunRootWorkflow);
        SearchDramaCommand = new AsyncRelayCommand(SearchDramaAsync, CanSearchDrama);
        LoadTodayDramaCommand = new AsyncRelayCommand(LoadTodayDramaAsync, CanLoadTodayDrama);
        LoadMangaTodayDramaCommand = new AsyncRelayCommand(LoadMangaTodayDramaAsync, CanLoadTodayDrama);
        LoadAiTodayDramaCommand = new AsyncRelayCommand(LoadAiTodayDramaAsync, CanLoadTodayDrama);
        LoadHistoryDramaCommand = new AsyncRelayCommand(LoadHistoryDramaAsync, CanLoadTodayDrama);
        GoPreviousSearchPageCommand = new AsyncRelayCommand(GoPreviousSearchPageAsync, CanGoPreviousSearchPage);
        GoNextSearchPageCommand = new AsyncRelayCommand(GoNextSearchPageAsync, CanGoNextSearchPage);
        ImportCheckedDramaCommand = new AsyncRelayCommand(ImportCheckedDramaAsync, CanImportCheckedDrama);
        ImportAndRunCheckedDramaCommand = new AsyncRelayCommand(ImportAndRunCheckedDramaAsync, CanImportAndRunCheckedDrama);
        DownloadCheckedDramaCommand = new AsyncRelayCommand(DownloadCheckedDramaAsync, CanDownloadCheckedDrama);
        ApplySearchFiltersCommand = new RelayCommand(ApplySearchFilters, CanApplySearchFilters);
        ReloadConfigCommand = new RelayCommand(LoadConfig, CanOperateWithRootDir);
        SaveConfigCommand = new RelayCommand(SaveConfig, CanOperateWithRootDir);
        ValidateConfigCommand = new RelayCommand(ValidateConfig, CanOperateWithRootDir);
        RefreshArchivedProjectsCommand = new RelayCommand(LoadArchivedProjects, CanOperateWithRootDir);
        OpenSourceDirCommand = new RelayCommand(OpenSourceDir, CanOpenSourceDir);
        OpenWorkflowDirCommand = new RelayCommand(OpenWorkflowDir, CanOpenWorkflowDir);
        OpenArchivedProjectDirCommand = new RelayCommand(OpenArchivedProjectDir, CanOpenArchivedProjectDir);
        OpenArchivedSourceDirCommand = new RelayCommand(OpenArchivedSourceDir, CanOpenArchivedSourceDir);
        OpenArchivedWorkflowDirCommand = new RelayCommand(OpenArchivedWorkflowDir, CanOpenArchivedWorkflowDir);
        OpenConfigFileCommand = new RelayCommand(OpenConfigFile, CanOperateWithRootDir);
        OpenPosterCommand = new RelayCommand(OpenPoster, CanOpenPoster);
        OpenCostReportCommand = new RelayCommand(OpenCostReport, CanOpenCostReport);
        OpenProjectImageCommand = new RelayCommand(OpenProjectImage, CanOpenProjectImage);
        OpenWeixinBrowserCommand = new AsyncRelayCommand(OpenWeixinBrowserAsync, CanOpenWeixinBrowser);
        RunSelectedStepCommand = new AsyncRelayCommand(RunSelectedStepAsync, CanRunSelectedStep);
        RunSelectedTranscodeCommand = new AsyncRelayCommand(RunSelectedTranscodeAsync, CanRunSelectedProject);
        RunSelectedProjectMaterialCommand = new AsyncRelayCommand(RunSelectedProjectMaterialAsync, CanRunSelectedProject);
        ValidateSelectedProjectMaterialCommand = new AsyncRelayCommand(ValidateSelectedProjectMaterialAsync, CanRunSelectedProject);
        RunSelectedWeixinUploadCommand = new AsyncRelayCommand(RunSelectedWeixinUploadAsync, CanRunSelectedProject);
        RunSelectedWeixinMaterialUploadCommand = new AsyncRelayCommand(RunSelectedWeixinMaterialUploadAsync, CanRunSelectedProject);
        RunCheckedProjectsCommand = new AsyncRelayCommand(RunCheckedProjectsAsync, CanRunCheckedProjects);
        RunCheckedTranscodeCommand = new AsyncRelayCommand(RunCheckedTranscodeAsync, CanRunCheckedProjects);
        RunCheckedProjectMaterialCommand = new AsyncRelayCommand(RunCheckedProjectMaterialAsync, CanRunCheckedProjects);
        RunCheckedWeixinUploadCommand = new AsyncRelayCommand(RunCheckedWeixinUploadAsync, CanRunCheckedProjects);
        RunCheckedWeixinMaterialUploadCommand = new AsyncRelayCommand(RunCheckedWeixinMaterialUploadAsync, CanRunCheckedProjects);
        RunCheckedQueueCommand = new AsyncRelayCommand(RunCheckedQueueAsync, CanRunCheckedQueue);
        RunCurrentTaskCommand = new AsyncRelayCommand(RunCurrentTaskAsync, CanRunCurrentTask);
        SyncCheckedProjectsToXingeCommand = new AsyncRelayCommand(SyncCheckedProjectsToXingeAsync, CanSyncCheckedProjectsToXinge);
        ArchiveSelectedProjectCommand = new AsyncRelayCommand(ArchiveSelectedProjectAsync, CanArchiveSelectedProject);
        ArchiveCheckedProjectsCommand = new AsyncRelayCommand(ArchiveCheckedProjectsAsync, CanArchiveCheckedProjects);
        StopCurrentRunCommand = new RelayCommand(StopCurrentRun, CanStopCurrentRun);
        TakeoverInteractionCommand = new RelayCommand(() => ResolveInteraction("manual"), CanTakeoverInteraction);
        ResumeInteractionCommand = new RelayCommand(() => ResolveInteraction("resume"), CanResumeInteraction);
        SkipCurrentItemInteractionCommand = new RelayCommand(() => ResolveInteraction("skip_video"), CanSkipCurrentItemInteraction);
        SkipCurrentProjectInteractionCommand = new RelayCommand(() => ResolveInteraction("skip_project"), CanSkipCurrentProjectInteraction);
        StopInteractionCommand = new RelayCommand(() => ResolveInteraction("stop"), CanStopInteraction);

        foreach (var step in StepOptions)
        {
            StepLogFilters.Add(new LogFilterOption(step.Key, step.Label));
        }

        RootDir = ResolveInitialRootDir();
        LoadArchivedProjects();
        StatusMessage = "杈撳叆椤圭洰鏍圭洰褰曞悗鐐瑰嚮鈥滄壂鎻忛」鐩€濄€?;
        SelectedStepOption = StepOptions.FirstOrDefault();
        SelectedExecutionModeOption = ExecutionModeOptions.FirstOrDefault();
        SelectedDownloadEpisodeRangeOption = DownloadEpisodeRangeOptions.FirstOrDefault();
        SelectedProjectLogFilter = ProjectLogFilters.First();
        SelectedStepLogFilter = StepLogFilters.First();
        _interactionService.RequestChanged += OnInteractionRequestChanged;
        LoadConfig();
        QueueStartupScanIfNeeded();
    }

    public ObservableCollection<ProjectListItemViewModel> Projects { get; } = [];
    public ObservableCollection<ArchivedProjectItem> ArchivedProjects { get; } = [];
    public ObservableCollection<SearchResultRowViewModel> SearchResults { get; } = [];
    public ObservableCollection<ActivityLogEntry> ActivityLog { get; } = [];
    public ObservableCollection<ConfigValidationItem> ConfigIssues { get; } = [];
    public ObservableCollection<MaterialValidationIssueItem> MaterialValidationIssues { get; } = [];
    public ObservableCollection<ProjectMaterialStepItem> ProjectMaterialSteps { get; } = [];
    public ObservableCollection<ActivityLogEntry> ProjectMaterialStepLogs { get; } = [];
    public ObservableCollection<LogFilterOption> ProjectLogFilters { get; } = [new(AllProjectsFilterKey, "鍏ㄩ儴椤圭洰")];
    public ObservableCollection<LogFilterOption> StepLogFilters { get; } =
    [
        new(AllStepsFilterKey, "鍏ㄩ儴姝ラ"),
        new(SystemStepFilterKey, "绯荤粺浜嬩欢")
    ];
    public ObservableCollection<WorkflowStepOption> StepOptions { get; } =
    [
        new("download", "涓嬭浇鍓ч泦"),
        new("transcode", "瑙嗛杞爜"),
        new("rewrite", "浠垮啓鍓у悕绠€浠?),
        new("poster-rename", "鐢熸垚娴锋姤鍥剧墖"),
        new("project-image", "鐢熸垚宸ョ▼鍥?),
        new("cost-report", "鐢熸垚鎴愭湰鎶ヨ〃"),
        new("batch-file-rename", "閲嶅懡鍚嶈棰戞枃浠?),
        new("material-convert", "杞崲绱犳潗瑙嗛"),
        new("weixin-upload", "寰俊涓婁紶鍓ч泦"),
        new("weixin-material-upload", "寰俊涓婁紶绱犳潗")
    ];
    public ObservableCollection<WorkflowStepOption> ExecutionModeOptions { get; } =
    [
        new(ExecutionModeSerial, "涓茶"),
        new(ExecutionModeConcurrent2, "骞跺彂 2")
    ];
    public ObservableCollection<WorkflowStepOption> DownloadEpisodeRangeOptions { get; } =
    [
        new(EpisodeRangeAll, "鍏ㄩ儴"),
        new(EpisodeRangeFirst3, "鍓?闆?),
        new(EpisodeRangeCustom, "鑷畾涔?)
    ];
    public ObservableCollection<string> WeixinMonetizationTypeOptions { get; } =
    [
        "IAA骞垮憡鍙樼幇",
        "IAA骞垮憡",
        "IAP浠樿垂瑙傜湅",
        "娣峰悎鍙樼幇"
    ];
    public ObservableCollection<string> WeixinDramaTypeOptions { get; } =
    [
        "婕墽",
        "鐪熶汉",
        "鑷姩妫€娴?
    ];
    public ObservableCollection<string> WeixinDramaQualificationOptions { get; } =
    [
        "鍏朵粬寰煭鍓?,
        "閲嶇偣鏅€氬井鐭墽"
    ];
    public ObservableCollection<string> WeixinSubmitterIdentityOptions { get; } =
    [
        "鍓х洰鍒朵綔鏂?,
        "鐗堟潈鏂?,
        "骞冲彴鏂?
    ];

    public IAsyncRelayCommand ScanCommand { get; }
    public IAsyncRelayCommand RunSelectedProjectCommand { get; }
    public IAsyncRelayCommand RunRootWorkflowCommand { get; }
    public IAsyncRelayCommand SearchDramaCommand { get; }
    public IAsyncRelayCommand LoadTodayDramaCommand { get; }
    public IAsyncRelayCommand LoadMangaTodayDramaCommand { get; }
    public IAsyncRelayCommand LoadAiTodayDramaCommand { get; }
    public IAsyncRelayCommand LoadHistoryDramaCommand { get; }
    public IAsyncRelayCommand GoPreviousSearchPageCommand { get; }
    public IAsyncRelayCommand GoNextSearchPageCommand { get; }
    public IAsyncRelayCommand DownloadCheckedDramaCommand { get; }
    public IAsyncRelayCommand ImportCheckedDramaCommand { get; }
    public IAsyncRelayCommand ImportAndRunCheckedDramaCommand { get; }
    public IRelayCommand ApplySearchFiltersCommand { get; }
    public IRelayCommand ReloadConfigCommand { get; }
    public IRelayCommand SaveConfigCommand { get; }
    public IRelayCommand ValidateConfigCommand { get; }
    public IRelayCommand RefreshArchivedProjectsCommand { get; }
    public IRelayCommand OpenSourceDirCommand { get; }
    public IRelayCommand OpenWorkflowDirCommand { get; }
    public IRelayCommand OpenArchivedProjectDirCommand { get; }
    public IRelayCommand OpenArchivedSourceDirCommand { get; }
    public IRelayCommand OpenArchivedWorkflowDirCommand { get; }
    public IRelayCommand OpenConfigFileCommand { get; }
    public IRelayCommand OpenPosterCommand { get; }
    public IRelayCommand OpenCostReportCommand { get; }
    public IRelayCommand OpenProjectImageCommand { get; }
    public IAsyncRelayCommand OpenWeixinBrowserCommand { get; }
    public IAsyncRelayCommand RunSelectedStepCommand { get; }
    public IAsyncRelayCommand RunSelectedTranscodeCommand { get; }
    public IAsyncRelayCommand RunSelectedProjectMaterialCommand { get; }
    public IAsyncRelayCommand ValidateSelectedProjectMaterialCommand { get; }
    public IAsyncRelayCommand RunSelectedWeixinUploadCommand { get; }
    public IAsyncRelayCommand RunSelectedWeixinMaterialUploadCommand { get; }
    public IAsyncRelayCommand RunCheckedProjectsCommand { get; }
    public IAsyncRelayCommand RunCheckedTranscodeCommand { get; }
    public IAsyncRelayCommand RunCheckedProjectMaterialCommand { get; }
    public IAsyncRelayCommand RunCheckedWeixinUploadCommand { get; }
    public IAsyncRelayCommand RunCheckedWeixinMaterialUploadCommand { get; }
    public IAsyncRelayCommand RunCheckedQueueCommand { get; }
    public IAsyncRelayCommand RunCurrentTaskCommand { get; }
    public IAsyncRelayCommand SyncCheckedProjectsToXingeCommand { get; }
    public IAsyncRelayCommand ArchiveSelectedProjectCommand { get; }
    public IAsyncRelayCommand ArchiveCheckedProjectsCommand { get; }
    public IRelayCommand StopCurrentRunCommand { get; }
    public IRelayCommand TakeoverInteractionCommand { get; }
    public IRelayCommand ResumeInteractionCommand { get; }
    public IRelayCommand SkipCurrentItemInteractionCommand { get; }
    public IRelayCommand SkipCurrentProjectInteractionCommand { get; }
    public IRelayCommand StopInteractionCommand { get; }

    [ObservableProperty]
    private string rootDir = string.Empty;

    [ObservableProperty]
    private string backupRootDir = string.Empty;

    [ObservableProperty]
    private string searchKeyword = string.Empty;

    [ObservableProperty]
    private bool exactSearchEnabled;

    [ObservableProperty]
    private string searchMinEpisodeCount = "0";

    [ObservableProperty]
    private string searchMaxEpisodeCount = "0";

    [ObservableProperty]
    private string searchQueryDays = "1";

    [ObservableProperty]
    private int currentSearchPage = 1;

    [ObservableProperty]
    private SearchResultRowViewModel? selectedSearchResult;

    [ObservableProperty]
    private ProjectListItemViewModel? selectedProject;

    [ObservableProperty]
    private ArchivedProjectItem? selectedArchivedProject;

    [ObservableProperty]
    private EpisodeUploadItemViewModel? selectedEpisodeUploadEpisode;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isSearchBusy;

    [ObservableProperty]
    private int totalProjects;

    [ObservableProperty]
    private int pendingProjects;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private string searchSummary = "杈撳叆鍓у悕鍚庢悳绱紝鎴栨煡鐪嬩笂鏂扮粨鏋溿€?;

    [ObservableProperty]
    private string activityTitle = "杩愯鏃ュ織";

    [ObservableProperty]
    private string configFilePath = string.Empty;

    [ObservableProperty]
    private string configValidationSummary = "鏈牎楠?;

    [ObservableProperty]
    private WorkflowStepOption? selectedStepOption;

    [ObservableProperty]
    private WorkflowStepOption? selectedExecutionModeOption;

    [ObservableProperty]
    private bool isTaskQueueDetailOpen;

    [ObservableProperty]
    private string taskQueueDetailMode = TaskQueueDetailDownload;

    [ObservableProperty]
    private LogFilterOption? selectedProjectLogFilter;

    [ObservableProperty]
    private LogFilterOption? selectedStepLogFilter;

    [ObservableProperty]
    private bool onlyShowFailedLogs;

    [ObservableProperty]
    private string companyName = string.Empty;

    [ObservableProperty]
    private ProjectMaterialStepItem? selectedProjectMaterialStep;

    [ObservableProperty]
    private string searchPageSize = DefaultSearchPageSize.ToString();

    [ObservableProperty]
    private WorkflowStepOption? selectedDownloadEpisodeRangeOption;

    [ObservableProperty]
    private string customDownloadEpisodeRange = string.Empty;

    public string CheckedProjectsSummary
    {
        get
        {
            var checkedCount = Projects.Count(item => item.IsChecked);
            return checkedCount <= 0 ? "鏈嬀閫変换鍔? : $"宸插嬀閫?{checkedCount} 涓换鍔?;
        }
    }

    public bool HasTaskQueueDetail => IsTaskQueueDetailOpen && SelectedProject is not null;
    public bool IsTaskQueueOverviewVisible => !HasTaskQueueDetail;
    public bool IsTaskQueueDownloadDetailVisible => HasTaskQueueDetail && string.Equals(TaskQueueDetailMode, TaskQueueDetailDownload, StringComparison.Ordinal);
    public bool IsTaskQueueProjectMaterialDetailVisible => HasTaskQueueDetail && string.Equals(TaskQueueDetailMode, TaskQueueDetailProjectMaterial, StringComparison.Ordinal);
    public bool IsTaskQueueEpisodeUploadDetailVisible => HasTaskQueueDetail && string.Equals(TaskQueueDetailMode, TaskQueueDetailEpisodeUpload, StringComparison.Ordinal);
    public bool IsTaskQueueMaterialUploadDetailVisible => HasTaskQueueDetail && string.Equals(TaskQueueDetailMode, TaskQueueDetailMaterialUpload, StringComparison.Ordinal);
    public string TaskQueueDetailTitle => TaskQueueDetailMode switch
    {
        TaskQueueDetailDownload => $"涓嬭浇鍓ч泦 路 {SelectedProjectTitle}",
        TaskQueueDetailProjectMaterial => $"鐢熸垚椤圭洰绱犳潗 路 {SelectedProjectTitle}",
        TaskQueueDetailEpisodeUpload => $"鍓ч泦涓婁紶 路 {SelectedProjectTitle}",
        TaskQueueDetailMaterialUpload => $"绱犳潗涓婁紶 路 {SelectedProjectTitle}",
        _ => SelectedProjectTitle
    };

    [ObservableProperty]
    private string templateDocxPath = string.Empty;

    [ObservableProperty]
    private string chatModelId = string.Empty;

    [ObservableProperty]
    private string chatModelApiKey = string.Empty;

    [ObservableProperty]
    private string chatModelEndpoint = string.Empty;

    [ObservableProperty]
    private string aiTextEndpoint = string.Empty;

    [ObservableProperty]
    private string aiTextApiKey = string.Empty;

    [ObservableProperty]
    private string aiTextModel = string.Empty;

    [ObservableProperty]
    private string aiTextTimeoutSeconds = "120";

    [ObservableProperty]
    private string aiTextMaxBatchSize = "10";

    [ObservableProperty]
    private string aiTextSystemPrompt = string.Empty;

    [ObservableProperty]
    private string aiTextBatchPrompt = string.Empty;

    [ObservableProperty]
    private string aiTextRetryPrompt = string.Empty;

    [ObservableProperty]
    private bool weixinHeadless;

    [ObservableProperty]
    private string weixinSlowMoMs = "50";

    [ObservableProperty]
    private string weixinKeepOpenSeconds = "0";

    [ObservableProperty]
    private string weixinLoginTimeoutSeconds = "300";

    [ObservableProperty]
    private bool weixinSubmitEnabled;

    [ObservableProperty]
    private bool weixinPauseOnError = true;

    [ObservableProperty]
    private bool weixinSaveHtml = true;

    [ObservableProperty]
    private bool weixinSaveText = true;

    [ObservableProperty]
    private string weixinMonetizationType = "IAA骞垮憡鍙樼幇";

    [ObservableProperty]
    private string weixinDramaType = "婕墽";

    [ObservableProperty]
    private string weixinDramaQualification = "鍏朵粬寰煭鍓?;

    [ObservableProperty]
    private string weixinSubmitterIdentity = "鍓х洰鍒朵綔鏂?;

    [ObservableProperty]
    private string weixinTrialEpisodes = "3";

    [ObservableProperty]
    private bool weixinFillRecommendation = true;

    [ObservableProperty]
    private string weixinSubmissionReportDir = string.Empty;

    [ObservableProperty]
    private string imageModelId = string.Empty;

    [ObservableProperty]
    private string imageModelApiKey = string.Empty;

    [ObservableProperty]
    private string imageModelEndpoint = string.Empty;

    [ObservableProperty]
    private string imageEditModelId = string.Empty;

    [ObservableProperty]
    private string imageEditApiKey = string.Empty;

    [ObservableProperty]
    private string imageEditEndpoint = string.Empty;

    [ObservableProperty]
    private string imageEditPath = string.Empty;

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
    private string videoRes = string.Empty;

    [ObservableProperty]
    private string videoBitrateBps = string.Empty;

    [ObservableProperty]
    private string videoBitrateMode = string.Empty;

    [ObservableProperty]
    private string videoAudioBitrateBps = string.Empty;

    [ObservableProperty]
    private string videoFps = string.Empty;

    [ObservableProperty]
    private string videoConcurrentCount = "1";

    [ObservableProperty]
    private bool videoUseHardwareEncoder;

    [ObservableProperty]
    private string videoNameTemplate = string.Empty;

    [ObservableProperty]
    private bool materialConvertEnabled = true;

    [ObservableProperty]
    private string materialTrimHeadSeconds = "4";

    [ObservableProperty]
    private string materialTrimTailSeconds = "2";

    [ObservableProperty]
    private string materialSpeedPercent = "10";

    [ObservableProperty]
    private string materialDropEveryNFrames = "20";

    [ObservableProperty]
    private string materialDropCount = "1";

    [ObservableProperty]
    private string materialCropWidthPercent = "2";

    [ObservableProperty]
    private string materialCropHeightPercent = "2";

    [ObservableProperty]
    private string projectImageCount = string.Empty;

    [ObservableProperty]
    private string projectImageTemplateDir = string.Empty;

    [ObservableProperty]
    private bool hasInteractionRequest;

    [ObservableProperty]
    private string interactionTitle = "浜哄伐浠嬪叆";

    [ObservableProperty]
    private string interactionMessage = string.Empty;

    private Bitmap? _posterPreviewBitmap;
    private Bitmap? _costPreviewBitmap;
    private Bitmap? _projectImagePreviewBitmap;
    private string _posterPreviewPath = string.Empty;
    private string _costPreviewPath = string.Empty;
    private string _projectImagePreviewPath = string.Empty;

    public string SelectedProjectTitle => SelectedProject?.DisplayName ?? "鏈€夋嫨椤圭洰";
    public string SearchPageText => $"绗?{CurrentSearchPage} 椤?;
    public string CheckedSearchResultsSummary => $"宸查€?{SearchResults.Count(item => item.IsChecked)} 椤?;
    public bool IsCustomDownloadEpisodeRange =>
        string.Equals(SelectedDownloadEpisodeRangeOption?.Key, EpisodeRangeCustom, StringComparison.Ordinal);
    public string WorkspaceSummary => string.IsNullOrWhiteSpace(RootDir) ? "鏈缃伐浣滅洰褰? : RootDir;
    public Bitmap? PosterPreviewBitmap
    {
        get => _posterPreviewBitmap;
        private set => SetProperty(ref _posterPreviewBitmap, value);
    }

    public Bitmap? CostPreviewBitmap
    {
        get => _costPreviewBitmap;
        private set => SetProperty(ref _costPreviewBitmap, value);
    }

    public Bitmap? ProjectImagePreviewBitmap
    {
        get => _projectImagePreviewBitmap;
        private set => SetProperty(ref _projectImagePreviewBitmap, value);
    }

    public string PosterPreviewPath
    {
        get => _posterPreviewPath;
        private set => SetProperty(ref _posterPreviewPath, value);
    }

    public string CostPreviewPath
    {
        get => _costPreviewPath;
        private set => SetProperty(ref _costPreviewPath, value);
    }

    public string ProjectImagePreviewPath
    {
        get => _projectImagePreviewPath;
        private set => SetProperty(ref _projectImagePreviewPath, value);
    }

    partial void OnRootDirChanged(string value)
    {
        OnPropertyChanged(nameof(WorkspaceSummary));
        RefreshCommandStates();
        LoadConfig();
    }

    partial void OnSearchKeywordChanged(string value) => RefreshCommandStates();
    partial void OnExactSearchEnabledChanged(bool value) => RefreshCommandStates();
    partial void OnSearchMinEpisodeCountChanged(string value) => RefreshCommandStates();
    partial void OnSearchMaxEpisodeCountChanged(string value) => RefreshCommandStates();
    partial void OnSearchQueryDaysChanged(string value) => RefreshCommandStates();
    partial void OnSelectedDownloadEpisodeRangeOptionChanged(WorkflowStepOption? value)
    {
        OnPropertyChanged(nameof(IsCustomDownloadEpisodeRange));
        RefreshCommandStates();
    }
    partial void OnCustomDownloadEpisodeRangeChanged(string value) => RefreshCommandStates();

    partial void OnCurrentSearchPageChanged(int value)
    {
        OnPropertyChanged(nameof(SearchPageText));
        RefreshCommandStates();
    }

    partial void OnSelectedSearchResultChanged(SearchResultRowViewModel? value) => RefreshCommandStates();

    partial void OnIsBusyChanged(bool value) => RefreshCommandStates();
    partial void OnIsSearchBusyChanged(bool value) => RefreshCommandStates();

    partial void OnSearchPageSizeChanged(string value) => RefreshCommandStates();
    partial void OnSelectedProjectMaterialStepChanged(ProjectMaterialStepItem? value) => ApplyProjectMaterialStepLogs();

    partial void OnSelectedStepOptionChanged(WorkflowStepOption? value) => RefreshCommandStates();

    partial void OnSelectedExecutionModeOptionChanged(WorkflowStepOption? value) => RefreshCommandStates();

    partial void OnSelectedProjectLogFilterChanged(LogFilterOption? value)
    {
        SyncRunLogSelectionToCurrentFilter();
        ApplyActivityLogFilter();
    }

    partial void OnSelectedStepLogFilterChanged(LogFilterOption? value) => ApplyActivityLogFilter();

    partial void OnOnlyShowFailedLogsChanged(bool value) => ApplyActivityLogFilter();

    partial void OnSelectedProjectChanged(ProjectListItemViewModel? value)
    {
        ClearMaterialValidationIssues();
        OnPropertyChanged(nameof(SelectedProjectTitle));
        OnPropertyChanged(nameof(HasTaskQueueDetail));
        OnPropertyChanged(nameof(IsTaskQueueOverviewVisible));
        OnPropertyChanged(nameof(IsTaskQueueDownloadDetailVisible));
        OnPropertyChanged(nameof(IsTaskQueueProjectMaterialDetailVisible));
        OnPropertyChanged(nameof(IsTaskQueueEpisodeUploadDetailVisible));
        OnPropertyChanged(nameof(IsTaskQueueMaterialUploadDetailVisible));
        OnPropertyChanged(nameof(TaskQueueDetailTitle));
        RefreshCommandStates();
        RefreshSelectedProjectPreview();
        RefreshProjectMaterialSteps();
    }

    partial void OnSelectedArchivedProjectChanged(ArchivedProjectItem? value)
    {
        RefreshCommandStates();
    }

    partial void OnIsTaskQueueDetailOpenChanged(bool value)
    {
        if (!value)
        {
            ClearMaterialValidationIssues();
        }

        OnPropertyChanged(nameof(HasTaskQueueDetail));
        OnPropertyChanged(nameof(IsTaskQueueOverviewVisible));
        OnPropertyChanged(nameof(IsTaskQueueDownloadDetailVisible));
        OnPropertyChanged(nameof(IsTaskQueueProjectMaterialDetailVisible));
        OnPropertyChanged(nameof(IsTaskQueueEpisodeUploadDetailVisible));
        OnPropertyChanged(nameof(IsTaskQueueMaterialUploadDetailVisible));
        OnPropertyChanged(nameof(TaskQueueDetailTitle));
    }

    partial void OnTaskQueueDetailModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsTaskQueueDownloadDetailVisible));
        OnPropertyChanged(nameof(IsTaskQueueOverviewVisible));
        OnPropertyChanged(nameof(IsTaskQueueProjectMaterialDetailVisible));
        OnPropertyChanged(nameof(IsTaskQueueEpisodeUploadDetailVisible));
        OnPropertyChanged(nameof(IsTaskQueueMaterialUploadDetailVisible));
        OnPropertyChanged(nameof(TaskQueueDetailTitle));
        RefreshProjectMaterialSteps();
        ApplyProjectMaterialStepLogs();
        if (!string.Equals(value, TaskQueueDetailProjectMaterial, StringComparison.Ordinal))
        {
            MaterialValidationIssues.Clear();
        }
    }

    private bool CanScan() => !IsBusy && CanOperateWithRootDir();

    private bool CanRunSelectedProject() => !IsBusy && SelectedProject is not null && CanOperateWithRootDir();

    private bool CanRunRootWorkflow() => !IsBusy && CanOperateWithRootDir() && Projects.Count > 0;

    private bool CanSearchDrama() =>
        !IsSearchBusy &&
        CanOperateWithRootDir() &&
        Directory.Exists(RootDir) &&
        !string.IsNullOrWhiteSpace(SearchKeyword);

    private bool CanLoadTodayDrama() =>
        !IsSearchBusy &&
        CanOperateWithRootDir() &&
        Directory.Exists(RootDir);

    private bool CanGoPreviousSearchPage() =>
        !IsSearchBusy &&
        string.Equals(_searchMode, SearchModeKeyword, StringComparison.Ordinal) &&
        CurrentSearchPage > 1 &&
        !string.IsNullOrWhiteSpace(_lastSearchKeyword);

    private bool CanGoNextSearchPage() =>
        !IsSearchBusy &&
        string.Equals(_searchMode, SearchModeKeyword, StringComparison.Ordinal) &&
        SearchResults.Count > 0 &&
        !string.IsNullOrWhiteSpace(_lastSearchKeyword);

    private bool CanApplySearchFilters() =>
        !IsSearchBusy &&
        _loadedSearchItems.Count > 0;

    private bool CanImportCheckedDrama() =>
        !IsSearchBusy &&
        CanOperateWithRootDir() &&
        Directory.Exists(RootDir) &&
        SearchResults.Any(item => item.IsChecked);

    private bool CanImportAndRunCheckedDrama() =>
        !IsBusy &&
        !IsSearchBusy &&
        CanOperateWithRootDir() &&
        Directory.Exists(RootDir) &&
        SearchResults.Any(item => item.IsChecked);

    private bool CanDownloadCheckedDrama() =>
        !IsSearchBusy &&
        CanOperateWithRootDir() &&
        Directory.Exists(RootDir) &&
        SearchResults.Any(item => item.IsChecked) &&
        TryBuildDownloadEpisodeSelection(out _);

    private bool CanRunSelectedStep() =>
        !IsBusy &&
        SelectedProject is not null &&
        SelectedStepOption is not null &&
        CanOperateWithRootDir();

    private bool CanRunCheckedProjects() =>
        !IsBusy &&
        CanOperateWithRootDir() &&
        Projects.Any(item => item.IsChecked);

    private bool CanRunCheckedQueue() =>
        !IsBusy &&
        CanOperateWithRootDir() &&
        Projects.Any(item => item.IsChecked) &&
        HasAnyTaskQueueStepSelected();

    private bool CanRunCurrentTask() =>
        !IsBusy &&
        CanOperateWithRootDir() &&
        SelectedProject is not null &&
        HasAnyTaskQueueStepSelected();

    private bool CanSyncCheckedProjectsToXinge() =>
        !IsBusy &&
        CanOperateWithRootDir() &&
        Directory.Exists(RootDir) &&
        Projects.Any(item => item.IsChecked);

    private bool CanArchiveSelectedProject() =>
        CanOperateWithRootDir() &&
        SelectedProject is not null &&
        CanArchiveProject(SelectedProject);

    private bool CanArchiveCheckedProjects() =>
        CanOperateWithRootDir() &&
        Projects.Any(item => item.IsChecked && CanArchiveProject(item));

    private bool CanStopCurrentRun() =>
        IsBusy &&
        _currentOperationCts is not null &&
        !_currentOperationCts.IsCancellationRequested;

    private bool CanResumeInteraction() =>
        HasInteractionRequest &&
        _currentInteractionRequest?.Options.Contains("resume", StringComparer.Ordinal) == true;

    private bool CanTakeoverInteraction() =>
        HasInteractionRequest &&
        _currentInteractionRequest?.Options.Contains("manual", StringComparer.Ordinal) == true;

    private bool CanSkipCurrentItemInteraction() =>
        HasInteractionRequest &&
        _currentInteractionRequest?.Options.Contains("skip_video", StringComparer.Ordinal) == true;

    private bool CanSkipCurrentProjectInteraction() =>
        HasInteractionRequest &&
        _currentInteractionRequest?.Options.Contains("skip_project", StringComparer.Ordinal) == true;

    private bool CanStopInteraction() =>
        HasInteractionRequest &&
        _currentInteractionRequest?.Options.Contains("stop", StringComparer.Ordinal) == true;

    private bool CanOperateWithRootDir() => !string.IsNullOrWhiteSpace(RootDir);

    private bool CanOpenSourceDir()
    {
        return SelectedProject is not null && !string.IsNullOrWhiteSpace(SelectedProject.SourceProjectDir);
    }

    private bool CanOpenWorkflowDir()
        => SelectedProject is not null && !string.IsNullOrWhiteSpace(SelectedProject.WorkflowProjectDir);

    private bool CanOpenArchivedProjectDir()
        => SelectedArchivedProject is not null && !string.IsNullOrWhiteSpace(SelectedArchivedProject.ArchiveProjectDir);

    private bool CanOpenArchivedSourceDir()
        => SelectedArchivedProject is not null && !string.IsNullOrWhiteSpace(SelectedArchivedProject.ArchivedSourceDir);

    private bool CanOpenArchivedWorkflowDir()
        => SelectedArchivedProject is not null && !string.IsNullOrWhiteSpace(SelectedArchivedProject.ArchivedWorkflowDir);

    private bool CanOpenPoster() => !string.IsNullOrWhiteSpace(PosterPreviewPath);

    private bool CanOpenCostReport() => !string.IsNullOrWhiteSpace(CostPreviewPath);

    private bool CanOpenProjectImage() => !string.IsNullOrWhiteSpace(ProjectImagePreviewPath);

    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(TaskQueueSummary));
        ScanCommand.NotifyCanExecuteChanged();
        RunSelectedProjectCommand.NotifyCanExecuteChanged();
        RunRootWorkflowCommand.NotifyCanExecuteChanged();
        SearchDramaCommand.NotifyCanExecuteChanged();
        LoadTodayDramaCommand.NotifyCanExecuteChanged();
        LoadMangaTodayDramaCommand.NotifyCanExecuteChanged();
        LoadAiTodayDramaCommand.NotifyCanExecuteChanged();
        LoadHistoryDramaCommand.NotifyCanExecuteChanged();
        GoPreviousSearchPageCommand.NotifyCanExecuteChanged();
        GoNextSearchPageCommand.NotifyCanExecuteChanged();
        ImportCheckedDramaCommand.NotifyCanExecuteChanged();
        ImportAndRunCheckedDramaCommand.NotifyCanExecuteChanged();
        DownloadCheckedDramaCommand.NotifyCanExecuteChanged();
        ApplySearchFiltersCommand.NotifyCanExecuteChanged();
        ReloadConfigCommand.NotifyCanExecuteChanged();
        SaveConfigCommand.NotifyCanExecuteChanged();
        ValidateConfigCommand.NotifyCanExecuteChanged();
        RefreshArchivedProjectsCommand.NotifyCanExecuteChanged();
        OpenSourceDirCommand.NotifyCanExecuteChanged();
        OpenWorkflowDirCommand.NotifyCanExecuteChanged();
        OpenArchivedProjectDirCommand.NotifyCanExecuteChanged();
        OpenArchivedSourceDirCommand.NotifyCanExecuteChanged();
        OpenArchivedWorkflowDirCommand.NotifyCanExecuteChanged();
        OpenConfigFileCommand.NotifyCanExecuteChanged();
        OpenPosterCommand.NotifyCanExecuteChanged();
        OpenCostReportCommand.NotifyCanExecuteChanged();
        OpenProjectImageCommand.NotifyCanExecuteChanged();
        OpenWeixinBrowserCommand.NotifyCanExecuteChanged();
        RunSelectedStepCommand.NotifyCanExecuteChanged();
        RunSelectedTranscodeCommand.NotifyCanExecuteChanged();
        RunSelectedProjectMaterialCommand.NotifyCanExecuteChanged();
        ValidateSelectedProjectMaterialCommand.NotifyCanExecuteChanged();
        RunSelectedWeixinUploadCommand.NotifyCanExecuteChanged();
        RunSelectedWeixinMaterialUploadCommand.NotifyCanExecuteChanged();
        RunCheckedProjectsCommand.NotifyCanExecuteChanged();
        RunCheckedTranscodeCommand.NotifyCanExecuteChanged();
        RunCheckedProjectMaterialCommand.NotifyCanExecuteChanged();
        RunCheckedWeixinUploadCommand.NotifyCanExecuteChanged();
        RunCheckedWeixinMaterialUploadCommand.NotifyCanExecuteChanged();
        RunCheckedQueueCommand.NotifyCanExecuteChanged();
        RunCurrentTaskCommand.NotifyCanExecuteChanged();
        SyncCheckedProjectsToXingeCommand.NotifyCanExecuteChanged();
        ArchiveSelectedProjectCommand.NotifyCanExecuteChanged();
        ArchiveCheckedProjectsCommand.NotifyCanExecuteChanged();
        StopCurrentRunCommand.NotifyCanExecuteChanged();
        TakeoverInteractionCommand.NotifyCanExecuteChanged();
        ResumeInteractionCommand.NotifyCanExecuteChanged();
        SkipCurrentItemInteractionCommand.NotifyCanExecuteChanged();
        SkipCurrentProjectInteractionCommand.NotifyCanExecuteChanged();
        StopInteractionCommand.NotifyCanExecuteChanged();
    }

    public void SetRootDir(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            RootDir = path;
            PersistState();
            LoadArchivedProjects();
        }
    }

    private void QueueStartupScanIfNeeded()
    {
        if (_startupScanTriggered ||
            string.IsNullOrWhiteSpace(RootDir) ||
            !Directory.Exists(RootDir))
        {
            return;
        }

        _startupScanTriggered = true;
        Dispatcher.UIThread.Post(() => _ = TryStartupScanAsync(), DispatcherPriority.Background);
    }

    private async Task TryStartupScanAsync()
    {
        if (IsBusy ||
            string.IsNullOrWhiteSpace(RootDir) ||
            !Directory.Exists(RootDir))
        {
            return;
        }

        await ScanAsync();
    }

    public void PersistState()
    {
        _stateService.SaveLastRootDir(RootDir);
    }

    public void SetTemplateDocxPath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            TemplateDocxPath = path;
            ValidateConfig();
        }
    }

    public void SetProjectImageTemplateDir(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            ProjectImageTemplateDir = path;
            ValidateConfig();
        }
    }

    public void SetWeixinSubmissionReportDir(string path)
    {
        WeixinSubmissionReportDir = path;
        ValidateConfig();
    }

    private async Task ScanAsync()
    {
        if (!Directory.Exists(RootDir))
        {
            StatusMessage = $"鏍圭洰褰曚笉瀛樺湪: {RootDir}";
            AppendLog($"鎵弿澶辫触锛岀洰褰曚笉瀛樺湪: {RootDir}");
            return;
        }

        await RunBusyAsync("姝ｅ湪鎵弿椤圭洰...", async cancellationToken =>
        {
            var result = await _projectScanner.ScanAsync(RootDir, null, cancellationToken);
            BackupRootDir = result.BackupRootDir ?? string.Empty;
            TotalProjects = result.TotalProjects;
            PendingProjects = result.PendingProjects;
            PersistState();

            ReplaceProjects(result.Projects);
            LoadArchivedProjects();

            RefreshProjectLogFilters();

            SelectedProject = null;
            TaskQueueDetailMode = TaskQueueDetailDownload;
            StatusMessage = $"鎵弿瀹屾垚锛屽叡 {result.TotalProjects} 涓」鐩紝寰呭鐞?{result.PendingProjects} 涓€?;
            AppendLog(StatusMessage);
            LoadConfig();
        });
    }

    private string ResolveInitialRootDir()
    {
        var persistedRootDir = _stateService.LoadLastRootDir();
        if (!string.IsNullOrWhiteSpace(persistedRootDir))
        {
            return persistedRootDir;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "demo-vedio");
    }

    private async Task RunSelectedProjectAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        await ExecuteProjectAsync(
            SelectedProject.ProjectKey,
            SelectedProject.DisplayName,
            SelectedProject.SourceProjectDir);
    }

    private async Task RunRootWorkflowAsync()
    {
        ClearAllLogs();
        ActivityTitle = $"鎵瑰鐞嗘棩蹇?路 {Path.GetFileName(RootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}";
        await RunBusyAsync("姝ｅ湪鎵ц鏁翠釜鏍圭洰褰曞伐浣滄祦...", async cancellationToken =>
        {
            var progress = CreateBufferedProgress();
            var result = await _workService.RunAsync(
                RootDir,
                null,
                force: false,
                progress,
                cancellationToken);

            AppendLog($"鎵瑰鐞嗗畬鎴愶細鎴愬姛 {result.SucceededProjects} 涓紝澶辫触 {result.FailedProjects} 涓紝璺宠繃 {result.SkippedProjects} 涓€?);
            await ScanAsync();
        });
    }

    private async Task RunSelectedStepAsync()
    {
        if (SelectedProject is null || SelectedStepOption is null)
        {
            return;
        }

        await ExecuteSelectedProjectStepAsync(SelectedStepOption.Key, SelectedStepOption.Label);
    }

    private async Task RunSelectedTranscodeAsync()
    {
        await ExecuteSelectedProjectStepAsync("transcode", "瑙嗛杞爜");
    }

    private async Task RunSelectedProjectMaterialAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        await ExecuteProjectMaterialPipelineAsync(SelectedProject, "椤圭洰绱犳潗鏃ュ織");
    }

    private async Task RunSelectedWeixinUploadAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        if (IsTaskQueueEpisodeUploadDetailVisible)
        {
            await ExecuteEpisodeUploadStepAsync(SelectedProject, null, "寰俊涓婁紶鍓ч泦");
            return;
        }

        await ExecuteSelectedProjectStepAsync("weixin-upload", "寰俊涓婁紶鍓ч泦");
    }

    private async Task RunSelectedWeixinMaterialUploadAsync()
    {
        await ExecuteSelectedProjectStepAsync("weixin-material-upload", "寰俊涓婁紶绱犳潗");
    }

    private Task RunCheckedProjectsAsync() =>
        ExecuteCheckedProjectsAsync(stepKey: null, stepLabel: "鍏ㄦ祦绋?);

    private Task RunCheckedTranscodeAsync() =>
        ExecuteCheckedProjectsAsync("transcode", "瑙嗛杞爜");

    private Task RunCheckedProjectMaterialAsync() =>
        ExecuteCheckedProjectsAsync("__project-material__", "涓€閿敓鎴愰」鐩礌鏉?);

    private Task RunCheckedWeixinUploadAsync() =>
        ExecuteCheckedProjectsAsync("weixin-upload", "寰俊涓婁紶鍓ч泦");

    private Task RunCheckedWeixinMaterialUploadAsync() =>
        ExecuteCheckedProjectsAsync("weixin-material-upload", "寰俊涓婁紶绱犳潗");

    private async Task RunCheckedQueueAsync()
    {
        var selectedProjects = Projects.Where(item => item.IsChecked).ToArray();
        if (selectedProjects.Length == 0)
        {
            return;
        }

        ActivityTitle = "浠诲姟闃熷垪鏃ュ織";
        await RunBusyAsync($"姝ｅ湪鎵ц鍕鹃€夐槦鍒楋紝鍏?{selectedProjects.Length} 涓」鐩?..", async cancellationToken =>
        {
            foreach (var project in selectedProjects)
            {
                project.MarkQueued();
            }

            for (var index = 0; index < selectedProjects.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteQueueSelectionForProjectAsync(selectedProjects[index], index + 1, selectedProjects.Length, cancellationToken);
            }

            await RefreshProjectListAsync();
            StatusMessage = $"鍕鹃€夐槦鍒楁墽琛屽畬鎴愶紝鍏卞鐞?{selectedProjects.Length} 涓」鐩€?;
            AppendLog(StatusMessage);
            await TryNotifyFeishuQueueSummaryAsync(selectedProjects, "鍕鹃€夐槦鍒?, cancellationToken);
        });
    }

    private async Task RunCurrentTaskAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        ActivityTitle = $"浠诲姟璇︽儏鏃ュ織 路 {SelectedProject.DisplayName}";
        await RunBusyAsync($"姝ｅ湪鎵ц褰撳墠浠诲姟锛歿SelectedProject.DisplayName}", async cancellationToken =>
        {
            await ExecuteQueueSelectionForProjectAsync(SelectedProject, 1, 1, cancellationToken);
            await RefreshAfterExecutionAsync(SelectedProject.ProjectKey);
            await TryNotifyFeishuQueueSummaryAsync([SelectedProject], "褰撳墠浠诲姟", cancellationToken);
        });
    }

    private async Task ArchiveSelectedProjectAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        if (!CanArchiveProject(SelectedProject))
        {
            return;
        }

        await ExecuteArchiveAsync([SelectedProject.ProjectKey], $"姝ｅ湪褰掓。椤圭洰锛歿SelectedProject.DisplayName}");
    }

    public async Task ArchiveSelectedProjectWithOptionsAsync(IReadOnlyCollection<int>? preserveWorkflowEpisodes)
    {
        if (SelectedProject is null)
        {
            return;
        }

        if (!CanArchiveProject(SelectedProject))
        {
            return;
        }

        await ExecuteArchiveAsync(
            [SelectedProject.ProjectKey],
            $"姝ｅ湪褰掓。椤圭洰锛歿SelectedProject.DisplayName}",
            preserveWorkflowEpisodes);
    }

    private async Task ArchiveCheckedProjectsAsync()
    {
        var checkedKeys = Projects
            .Where(item => item.IsChecked && CanArchiveProject(item))
            .Select(item => item.ProjectKey)
            .ToArray();
        if (checkedKeys.Length == 0)
        {
            return;
        }

        await ExecuteArchiveAsync(checkedKeys, $"姝ｅ湪褰掓。鍕鹃€夐」鐩紝鍏?{checkedKeys.Length} 涓?..");
    }

    public async Task ArchiveCheckedProjectsWithOptionsAsync(IReadOnlyCollection<int>? preserveWorkflowEpisodes)
    {
        var checkedKeys = Projects
            .Where(item => item.IsChecked && CanArchiveProject(item))
            .Select(item => item.ProjectKey)
            .ToArray();
        if (checkedKeys.Length == 0)
        {
            return;
        }

        await ExecuteArchiveAsync(
            checkedKeys,
            $"姝ｅ湪褰掓。鍕鹃€夐」鐩紝鍏?{checkedKeys.Length} 涓?..",
            preserveWorkflowEpisodes);
    }

    private async Task ExecuteArchiveAsync(
        IReadOnlyCollection<string> projectKeys,
        string message,
        IReadOnlyCollection<int>? preserveWorkflowEpisodes = null)
    {
        StatusMessage = message;
        AppendLog(message);

        try
        {
            await ArchiveProjectsCoreAsync(projectKeys, preserveWorkflowEpisodes, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            AppendLog($"褰掓。澶辫触: {ex.Message}", string.Empty, string.Empty, "archive", "褰掓。椤圭洰", isFailure: true);
        }
        finally
        {
            RefreshCommandStates();
        }
    }

    private static bool CanArchiveProject(ProjectListItemViewModel project)
    {
        return !string.Equals(project.SchedulingStatus, "杩愯涓?, StringComparison.Ordinal) &&
               !string.Equals(project.SchedulingStatus, "鎺掗槦涓?, StringComparison.Ordinal);
    }

    private async Task ExecuteSelectedProjectStepAsync(string stepKey, string stepLabel)
    {
        if (SelectedProject is null)
        {
            return;
        }

        ClearMaterialValidationIssues();
        ClearLogsForProject(SelectedProject.ProjectKey);
        SelectedStepOption = StepOptions.FirstOrDefault(item =>
            string.Equals(item.Key, stepKey, StringComparison.Ordinal)) ?? SelectedStepOption;
        SyncStepLogFilterToSelection();
        ActivityTitle = $"姝ラ鏃ュ織 路 {SelectedProject.DisplayName} 路 {stepLabel}";
        await RunBusyAsync($"姝ｅ湪鎵ц姝ラ锛歿stepLabel}", async cancellationToken =>
        {
            await TryNotifyFeishuStepAsync(SelectedProject, stepKey, stepLabel, "before", null, null, cancellationToken);
            var progress = CreateBufferedProgress();
            var result = await _workService.RunProjectStepAsync(
                SelectedProject.SourceProjectDir,
                null,
                stepKey,
                force: true,
                progress,
                cancellationToken);

            AppendLog($"姝ラ瀹屾垚: {stepLabel}锛岀粨鏋?{(result.Ok ? "鎴愬姛" : "澶辫触")}");
            await TryNotifyFeishuStepAsync(SelectedProject, stepKey, stepLabel, "after", result.Ok, result.Message, cancellationToken);
            await RefreshAfterExecutionAsync(result.ProjectKey);
        });
    }

    private async Task ValidateSelectedProjectMaterialAsync()
    {
        if (SelectedProject is null || string.IsNullOrWhiteSpace(SelectedProject.WorkflowProjectDir))
        {
            return;
        }

        var project = SelectedProject;
        ActivityTitle = $"椤圭洰绱犳潗鏍￠獙 路 {project.DisplayName}";
        ClearMaterialValidationIssues();

        await RunSearchBusyAsync($"姝ｅ湪鏍￠獙椤圭洰绱犳潗锛歿project.DisplayName}", async cancellationToken =>
        {
            await RefreshMaterialValidationIssuesAsync(project, cancellationToken, appendLogs: true);
        });
    }

    public async Task FixMaterialValidationIssueAsync(MaterialValidationIssueItem? issue)
    {
        if (SelectedProject is null || issue is null || !issue.CanAutoFix)
        {
            return;
        }

        var project = SelectedProject;
        await RunBusyAsync($"姝ｅ湪淇绱犳潗闂锛歿issue.Message}", async cancellationToken =>
        {
            ShowMaterialValidationInProgress(issue.Message);

            switch (issue.Code)
            {
                case "info-missing":
                case "info-invalid":
                    await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "rewrite", true, CreateBufferedProgress(), cancellationToken);
                    break;
                case "poster-missing":
                    await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "poster-rename", true, CreateBufferedProgress(), cancellationToken);
                    break;
                case "project-images-missing":
                    await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "project-image", true, CreateBufferedProgress(), cancellationToken);
                    break;
                case "material-video-title-mismatch":
                    await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "material-convert", true, CreateBufferedProgress(), cancellationToken);
                    break;
                case "cost-missing":
                    await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "cost-report", true, CreateBufferedProgress(), cancellationToken);
                    break;
                case "video-title-mismatch":
                    await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "batch-file-rename", true, CreateBufferedProgress(), cancellationToken);
                    break;
                case "weixin-upload-config-missing":
                    await _workService.EnsureWeixinUploadConfigAsync(project.SourceProjectDir, null, cancellationToken);
                    break;
                case "weixin-title-mismatch":
                    await _workService.RefreshWeixinConfigsAsync(project.SourceProjectDir, null, cancellationToken);
                    break;
                case "video-bitrate-low":
                case "videos-dir-missing":
                case "video-bitrate-unreadable":
                    PrepareTranscodeRepairOutputs([issue]);
                    await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "transcode", false, CreateBufferedProgress(), cancellationToken);
                    break;
                default:
                    return;
            }

            await RefreshMaterialValidationIssuesAsync(project, cancellationToken, appendLogs: true);
        });
    }

    public Task FixAllMaterialValidationIssuesForSelectedProjectAsync()
    {
        return FixAllMaterialValidationIssuesAsync();
    }

    private async Task FixAllMaterialValidationIssuesAsync()
    {
        if (SelectedProject is null)
        {
            StatusMessage = "鏈€夋嫨椤圭洰锛屾棤娉曟墽琛屼竴閿慨澶嶃€?;
            return;
        }

        var project = SelectedProject;

        await RunBusyAsync($"姝ｅ湪涓€閿慨澶嶇礌鏉愰棶棰橈細{project.DisplayName}", async cancellationToken =>
        {
            var fixableIssues = await RefreshMaterialValidationIssuesAsync(project, cancellationToken, appendLogs: false);
            if (fixableIssues.Length == 0)
            {
                StatusMessage = $"褰撳墠娌℃湁鍙嚜鍔ㄤ慨澶嶇殑绱犳潗闂锛歿project.DisplayName}";
                AppendLog(StatusMessage);
                return;
            }

            ShowMaterialValidationInProgress($"鍏?{fixableIssues.Length} 椤瑰緟淇");
            var codes = fixableIssues.Select(item => item.Code).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

            if (codes.Contains("info-missing") || codes.Contains("info-invalid"))
            {
                await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "rewrite", true, CreateBufferedProgress(), cancellationToken);
            }

            if (codes.Contains("video-bitrate-low") || codes.Contains("videos-dir-missing") || codes.Contains("video-bitrate-unreadable"))
            {
                PrepareTranscodeRepairOutputs(fixableIssues.Where(item =>
                    string.Equals(item.Code, "video-bitrate-low", StringComparison.Ordinal) ||
                    string.Equals(item.Code, "video-bitrate-unreadable", StringComparison.Ordinal) ||
                    string.Equals(item.Code, "videos-dir-missing", StringComparison.Ordinal)));
                await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "transcode", false, CreateBufferedProgress(), cancellationToken);
            }

            if (codes.Contains("poster-missing"))
            {
                await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "poster-rename", true, CreateBufferedProgress(), cancellationToken);
            }

            if (codes.Contains("project-images-missing"))
            {
                await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "project-image", true, CreateBufferedProgress(), cancellationToken);
            }

            if (codes.Contains("material-video-title-mismatch"))
            {
                await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "material-convert", true, CreateBufferedProgress(), cancellationToken);
            }

            if (codes.Contains("cost-missing"))
            {
                await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "cost-report", true, CreateBufferedProgress(), cancellationToken);
            }

            if (codes.Contains("video-title-mismatch"))
            {
                await _workService.RunProjectStepAsync(project.SourceProjectDir, null, "batch-file-rename", true, CreateBufferedProgress(), cancellationToken);
            }

            if (codes.Contains("weixin-upload-config-missing"))
            {
                await _workService.EnsureWeixinUploadConfigAsync(project.SourceProjectDir, null, cancellationToken);
            }

            if (codes.Contains("weixin-title-mismatch"))
            {
                await _workService.RefreshWeixinConfigsAsync(project.SourceProjectDir, null, cancellationToken);
            }

            await RefreshMaterialValidationIssuesAsync(project, cancellationToken, appendLogs: true);
        });
    }

    private void ShowMaterialValidationInProgress(string message)
    {
        ClearMaterialValidationIssues();
        MaterialValidationIssues.Add(new MaterialValidationIssueItem(
            "repair-running",
            "澶勭悊涓?,
            $"姝ｅ湪淇锛歿message}锛屽畬鎴愬悗灏嗚嚜鍔ㄩ噸鏂版牎楠屻€?,
            null,
            false));
    }

    private static void PrepareTranscodeRepairOutputs(IEnumerable<MaterialValidationIssueItem> issues)
    {
        foreach (var issue in issues)
        {
            if (string.IsNullOrWhiteSpace(issue.RelatedPath))
            {
                continue;
            }

            if (!File.Exists(issue.RelatedPath))
            {
                continue;
            }

            try
            {
                File.Delete(issue.RelatedPath);
            }
            catch
            {
                // Best effort: if delete fails, the subsequent transcode run will surface the failure.
            }
        }
    }

    private async Task<MaterialValidationIssueItem[]> RefreshMaterialValidationIssuesAsync(
        ProjectListItemViewModel project,
        CancellationToken cancellationToken,
        bool appendLogs)
    {
        var result = await _materialValidationService.ValidateAsync(project.WorkflowProjectDir!, cancellationToken);
        var items = result.Issues
            .Select(issue => new MaterialValidationIssueItem(
                issue.Code,
                issue.Severity,
                issue.Message,
                issue.RelatedPath,
                issue.CanAutoFix))
            .ToArray();

        ClearMaterialValidationIssues();
        if (items.Length == 0)
        {
            MaterialValidationIssues.Add(new MaterialValidationIssueItem(
                "validation-ok",
                "閫氳繃",
                "绱犳潗鏍￠獙閫氳繃銆?,
                null,
                false));
        }
        else
        {
            foreach (var item in items)
            {
                MaterialValidationIssues.Add(item);
            }
        }

        if (appendLogs)
        {
            foreach (var issue in result.Issues)
            {
                AppendLog(
                    $"[{issue.Severity}] {issue.Message}",
                    project.ProjectKey,
                    project.DisplayName,
                    "project-material-validate",
                    "绱犳潗鏍￠獙",
                    isFailure: string.Equals(issue.Severity, "閿欒", StringComparison.Ordinal));
            }

            if (result.Issues.Count == 0)
            {
                AppendLog(
                    "绱犳潗鏍￠獙閫氳繃銆?,
                    project.ProjectKey,
                    project.DisplayName,
                    "project-material-validate",
                    "绱犳潗鏍￠獙");
            }
        }

        return items.Where(item => item.CanAutoFix).ToArray();
    }

    public Task RunProjectFromQueueAsync(ProjectListItemViewModel? project)
    {
        if (project is null)
        {
            return Task.CompletedTask;
        }

        SelectedProject = project;
        return ExecuteProjectAsync(project.ProjectKey, project.DisplayName, project.SourceProjectDir);
    }

    public Task RunProjectStepFromQueueAsync(ProjectListItemViewModel? project, string stepKey, string stepLabel)
    {
        if (project is null)
        {
            return Task.CompletedTask;
        }

        SelectedProject = project;
        return ExecuteSelectedProjectStepAsync(stepKey, stepLabel);
    }

    public Task RunProjectMaterialFromQueueAsync(ProjectListItemViewModel? project)
    {
        if (project is null)
        {
            return Task.CompletedTask;
        }

        SelectedProject = project;
        return ExecuteProjectMaterialPipelineAsync(project, "椤圭洰绱犳潗鏃ュ織");
    }

    private async Task ArchiveProjectsCoreAsync(
        IReadOnlyCollection<string> projectKeys,
        IReadOnlyCollection<int>? preserveWorkflowEpisodes,
        CancellationToken cancellationToken)
    {
        var result = await _projectScanner.ScanAsync(RootDir, null, cancellationToken);
        var selectedKey = SelectedProject?.ProjectKey;
        var archivedAnySelected = selectedKey is not null && projectKeys.Contains(selectedKey);
        var archivedCount = 0;

        foreach (var projectKey in projectKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scannedProject = result.Projects.FirstOrDefault(item => string.Equals(item.ProjectKey, projectKey, StringComparison.Ordinal));
            if (scannedProject is null)
            {
                AppendLog($"褰掓。璺宠繃锛氭湭鎵惧埌椤圭洰 {projectKey}", projectKey, projectKey, "archive", "褰掓。椤圭洰", isFailure: true);
                continue;
            }

            var archiveResult = await _projectArchiveService.ArchiveAsync(
                RootDir,
                scannedProject,
                new ProjectArchiveOptions(preserveWorkflowEpisodes),
                cancellationToken);
            archivedCount++;
            ClearLogsForProject(projectKey);
            AppendLog(
                $"褰掓。瀹屾垚锛歿scannedProject.DisplayName}锛寋archiveResult.Message}",
                projectKey,
                scannedProject.DisplayName,
                "archive",
                "褰掓。椤圭洰");
        }

        if (archivedAnySelected)
        {
            IsTaskQueueDetailOpen = false;
        }

        await RefreshProjectListAsync();
        StatusMessage = archivedCount > 0
            ? $"褰掓。瀹屾垚锛屽叡澶勭悊 {archivedCount} 涓」鐩€?
            : "鏈綊妗ｄ换浣曢」鐩€?;
        AppendLog(StatusMessage);
    }

    public void OpenTaskQueueNodeDetail(ProjectListItemViewModel? project, string detailMode)
    {
        if (project is null)
        {
            return;
        }

        ClearMaterialValidationIssues();
        SelectedProject = project;
        IsTaskQueueDetailOpen = true;
        TaskQueueDetailMode = detailMode;
        SyncProjectLogFilterToSelection();

        if (string.Equals(detailMode, TaskQueueDetailDownload, StringComparison.Ordinal))
        {
            SelectedStepOption = StepOptions.FirstOrDefault(item => string.Equals(item.Key, "download", StringComparison.Ordinal))
                ?? SelectedStepOption;
            SelectedStepLogFilter = StepLogFilters.FirstOrDefault(item => string.Equals(item.Key, "download", StringComparison.Ordinal))
                ?? SelectedStepLogFilter;
            ActivityTitle = $"涓嬭浇鍓ч泦鏃ュ織 路 {project.DisplayName}";
        }
        else if (string.Equals(detailMode, TaskQueueDetailProjectMaterial, StringComparison.Ordinal))
        {
            ClearMaterialValidationIssues();
            SelectedStepOption = StepOptions.FirstOrDefault(item => string.Equals(item.Key, "transcode", StringComparison.Ordinal))
                ?? SelectedStepOption;
            SelectedStepLogFilter = StepLogFilters.FirstOrDefault(item => string.Equals(item.Key, AllStepsFilterKey, StringComparison.Ordinal))
                ?? SelectedStepLogFilter;
            ActivityTitle = $"椤圭洰绱犳潗鏃ュ織 路 {project.DisplayName}";
        }
        else if (string.Equals(detailMode, TaskQueueDetailEpisodeUpload, StringComparison.Ordinal))
        {
            SelectedStepLogFilter = StepLogFilters.FirstOrDefault(item => string.Equals(item.Key, "weixin-upload", StringComparison.Ordinal))
                ?? SelectedStepLogFilter;
            ActivityTitle = $"鍓ч泦涓婁紶鏃ュ織 路 {project.DisplayName}";
        }
        else if (string.Equals(detailMode, TaskQueueDetailMaterialUpload, StringComparison.Ordinal))
        {
            SelectedStepLogFilter = StepLogFilters.FirstOrDefault(item => string.Equals(item.Key, "weixin-material-upload", StringComparison.Ordinal))
                ?? SelectedStepLogFilter;
            ActivityTitle = $"绱犳潗涓婁紶鏃ュ織 路 {project.DisplayName}";
        }
    }

    public void CloseTaskQueueNodeDetail()
    {
        ClearMaterialValidationIssues();
        IsTaskQueueDetailOpen = false;
    }

    public async Task RetryDownloadEpisodeAsync(DownloadEpisodeItemViewModel? episode)
    {
        if (SelectedProject is null || episode is null)
        {
            return;
        }

        var project = SelectedProject;
        var metadata = ReadDownloadMetadata(project.SourceProjectDir);
        if (string.IsNullOrWhiteSpace(metadata.BookId))
        {
            AppendLog(
                $"涓嬭浇鍓ч泦 閲嶈瘯澶辫触锛氱己灏?book_id锛屾棤娉曚笅杞界{episode.EpisodeNumber}闆嗐€?,
                project.ProjectKey,
                project.DisplayName,
                "download",
                "涓嬭浇鍓ч泦",
                isFailure: true);
            return;
        }

        await RunBusyAsync($"姝ｅ湪閲嶈瘯涓嬭浇锛歿project.DisplayName} 路 绗瑊episode.EpisodeNumber}闆?, async cancellationToken =>
        {
            HandleProgress(new WorkRunEvent(project.ProjectKey, project.DisplayName, "step-started", "download", $"閲嶈瘯绗瑊episode.EpisodeNumber}闆?, true));

            var result = await _dramaDownloader.DownloadAsync(
                new DramaDownloadRequest(
                    ProjectDir: project.SourceProjectDir,
                    OutputDir: project.SourceProjectDir,
                    DisplayName: project.DisplayName,
                    BookId: metadata.BookId,
                    Episodes: episode.EpisodeNumber.ToString(),
                    Quality: metadata.Quality,
                    Concurrent: 1),
                new Progress<string>(message =>
                {
                    HandleProgress(new WorkRunEvent(project.ProjectKey, project.DisplayName, "step-output", "download", message, true));
                }),
                cancellationToken);

            HandleProgress(new WorkRunEvent(
                project.ProjectKey,
                project.DisplayName,
                result.Ok ? "step-completed" : "step-failed",
                "download",
                result.Message,
                result.Ok));

            await RefreshAfterExecutionAsync(project.ProjectKey);
        });
    }

    public async Task RetryEpisodeUploadAsync(EpisodeUploadItemViewModel? episode)
    {
        if (SelectedProject is null || episode is null)
        {
            return;
        }

        if (IsBusy)
        {
            AppendLog(
                "璇峰厛鍋滄褰撳墠涓婁紶锛屽啀鎵ц閫愯閲嶈瘯銆?,
                SelectedProject.ProjectKey,
                SelectedProject.DisplayName,
                "weixin-upload",
                "寰俊涓婁紶鍓ч泦",
                isFailure: true);
            return;
        }

        SelectedProject.ClearEpisodeUploadSkipped(episode.EpisodeNumber);
        await ExecuteEpisodeUploadStepAsync(SelectedProject, [episode.EpisodeNumber], $"閲嶈瘯绗瑊episode.EpisodeNumber}闆?);
    }

    public void SkipEpisodeUpload(EpisodeUploadItemViewModel? episode)
    {
        if (SelectedProject is null || episode is null)
        {
            return;
        }

        if (IsBusy)
        {
            AppendLog(
                "璇峰厛鍋滄褰撳墠涓婁紶锛屽啀鎵ц閫愯璺宠繃銆?,
                SelectedProject.ProjectKey,
                SelectedProject.DisplayName,
                "weixin-upload",
                "寰俊涓婁紶鍓ч泦",
                isFailure: true);
            return;
        }

        SelectedProject.MarkEpisodeUploadSkipped(episode.EpisodeNumber);
        AppendLog(
            $"鍓ч泦涓婁紶 宸插皢绗瑊episode.EpisodeNumber}闆嗘爣璁颁负璺宠繃锛屼笅涓€娆″紑濮?缁х画涓婁紶鏃跺皢涓嶅寘鍚闆嗐€?,
            SelectedProject.ProjectKey,
            SelectedProject.DisplayName,
            "weixin-upload",
            "寰俊涓婁紶鍓ч泦");
    }

    public void MarkEpisodeUploadCompleted(EpisodeUploadItemViewModel? episode)
    {
        if (SelectedProject is null || episode is null)
        {
            return;
        }

        SelectedProject.MarkEpisodeUploadCompleted(episode.EpisodeNumber);
        AppendLog(
            $"鍓ч泦涓婁紶 宸插皢绗瑊episode.EpisodeNumber}闆嗘墜鍔ㄦ爣璁颁负瀹屾垚銆?,
            SelectedProject.ProjectKey,
            SelectedProject.DisplayName,
            "weixin-upload",
            "寰俊涓婁紶鍓ч泦");
    }

    public void MarkMaterialUploadCompleted()
    {
        if (SelectedProject is null)
        {
            return;
        }

        SelectedProject.MaterialUploadStepStatus = "宸插畬鎴?;
        AppendLog(
            "绱犳潗涓婁紶 宸叉墜鍔ㄦ爣璁颁负瀹屾垚銆?,
            SelectedProject.ProjectKey,
            SelectedProject.DisplayName,
            "weixin-material-upload",
            "寰俊涓婁紶绱犳潗");
    }

    public async Task UpdateSelectedProjectTitleAsync(string newTitle)
    {
        if (SelectedProject is null)
        {
            return;
        }

        if (IsBusy)
        {
            AppendLog(
                "褰撳墠鏈変换鍔¤繍琛岋紝璇峰厛鍋滄鍚庡啀淇敼鏂板墽鍚嶃€?,
                SelectedProject.ProjectKey,
                SelectedProject.DisplayName,
                "rewrite",
                "淇敼鏂板墽鍚?,
                isFailure: true);
            return;
        }

        var project = SelectedProject;
        var trimmedTitle = newTitle?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedTitle))
        {
            return;
        }

        await RunSearchBusyAsync($"姝ｅ湪淇敼鏂板墽鍚嶏細{project.DisplayName} -> {trimmedTitle}", async cancellationToken =>
        {
            var result = await _workService.UpdateProjectTitleAsync(
                project.SourceProjectDir,
                null,
                trimmedTitle,
                cancellationToken);

            AppendLog(
                $"宸叉洿鏂版柊鍓у悕锛歿result.OriginalTitle} -> {result.NewTitle}",
                result.ProjectKey,
                result.NewTitle,
                "rewrite",
                "淇敼鏂板墽鍚?);

            AppendLog(
                $"宸插悓姝ラ噸鍛藉悕瑙嗛鏂囦欢 {result.RenamedVideoCount} 涓€?,
                result.ProjectKey,
                result.NewTitle,
                "batch-file-rename",
                "閲嶅懡鍚嶈棰戞枃浠?);

            AppendLog(
                $"宸插悓姝ュ埛鏂板井淇′笂浼犻厤缃?{result.UpdatedWeixinConfigCount} 涓€?,
                result.ProjectKey,
                result.NewTitle,
                "weixin-upload",
                "寰俊涓婁紶鍓ч泦");

            if (result.RegeneratedSteps.Count > 0)
            {
                AppendLog(
                    $"宸茶嚜鍔ㄩ噸鏂扮敓鎴愭楠わ細{string.Join("銆?, result.RegeneratedSteps)}",
                    result.ProjectKey,
                    result.NewTitle,
                    "project-material",
                    "鐢熸垚椤圭洰绱犳潗");
            }

            AppendLog(
                $"宸插け鏁堟楠わ細{string.Join("銆?, result.InvalidatedSteps)}",
                result.ProjectKey,
                result.NewTitle,
                "rewrite",
                "淇敼鏂板墽鍚?);

            await RefreshAfterExecutionAsync(result.ProjectKey);
            if (SelectedProject is not null)
            {
                OpenTaskQueueNodeDetail(SelectedProject, TaskQueueDetailProjectMaterial);
            }
        });
    }

    public async Task RemoveDownloadEpisodeAsync(DownloadEpisodeItemViewModel? episode)
    {
        if (SelectedProject is null || episode is null)
        {
            return;
        }

        var project = SelectedProject;
        DeleteDownloadedEpisodeFiles(project.SourceProjectDir, episode.EpisodeNumber);

        AppendLog(
            $"涓嬭浇鍓ч泦 宸茬Щ闄ょ{episode.EpisodeNumber}闆嗙殑婧愯棰戞枃浠躲€?,
            project.ProjectKey,
            project.DisplayName,
            "download",
            "涓嬭浇鍓ч泦");

        await RefreshAfterExecutionAsync(project.ProjectKey);
    }

    private async Task ExecuteEpisodeUploadStepAsync(ProjectListItemViewModel project, IReadOnlyCollection<int>? onlyEpisodes, string stepLabel)
    {
        ClearLogsForProject(project.ProjectKey);
        ActivityTitle = $"鍓ч泦涓婁紶鏃ュ織 路 {project.DisplayName}";
        AppendLog(
            $"寮€濮嬫墽琛? {stepLabel}",
            project.ProjectKey,
            project.DisplayName,
            "weixin-upload",
            "寰俊涓婁紶鍓ч泦");

        await RunBusyAsync($"姝ｅ湪鎵ц姝ラ锛歿stepLabel}", async cancellationToken =>
        {
            await TryNotifyFeishuStepAsync(project, "weixin-upload", stepLabel, "before", null, null, cancellationToken);
            string? overrideConfigPath = null;
            try
            {
                try
                {
                    var ensuredConfigPath = await _workService.EnsureWeixinUploadConfigAsync(
                        project.SourceProjectDir,
                        null,
                        cancellationToken);
                    AppendLog(
                        $"宸茬‘璁ゅ井淇″墽闆嗕笂浼犻厤缃細{ensuredConfigPath}",
                        project.ProjectKey,
                        project.DisplayName,
                        "weixin-upload",
                        "寰俊涓婁紶鍓ч泦");

                    overrideConfigPath = CreateEpisodeUploadOverrideConfig(project, onlyEpisodes);
                }
                catch (Exception ex)
                {
                    AppendLog(
                        $"鍚姩澶辫触: {ex.Message}",
                        project.ProjectKey,
                        project.DisplayName,
                        "weixin-upload",
                        "寰俊涓婁紶鍓ч泦",
                        isFailure: true);
                    throw;
                }

                var progress = CreateBufferedProgress();
                ProjectWorkResult result;
                try
                {
                    result = await _workService.RunProjectStepAsync(
                        project.SourceProjectDir,
                        null,
                        "weixin-upload",
                        force: true,
                        progress,
                        cancellationToken,
                        overrideConfigPath);
                }
                catch (Exception ex)
                {
                    AppendLog(
                        $"鎵ц寮傚父: {ex.Message}",
                        project.ProjectKey,
                        project.DisplayName,
                        "weixin-upload",
                        "寰俊涓婁紶鍓ч泦",
                        isFailure: true);
                    throw;
                }

                AppendLog(
                    $"姝ラ瀹屾垚: {stepLabel}锛岀粨鏋?{(result.Ok ? "鎴愬姛" : "澶辫触")}",
                    project.ProjectKey,
                    project.DisplayName,
                    "weixin-upload",
                    "寰俊涓婁紶鍓ч泦",
                    !result.Ok);
                await TryNotifyFeishuStepAsync(project, "weixin-upload", stepLabel, "after", result.Ok, result.Message, cancellationToken);

                await RefreshAfterExecutionAsync(result.ProjectKey);
                if (SelectedProject is not null)
                {
                    OpenTaskQueueNodeDetail(SelectedProject, TaskQueueDetailEpisodeUpload);
                }
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(overrideConfigPath) && File.Exists(overrideConfigPath))
                {
                    File.Delete(overrideConfigPath);
                }
            }
        });
    }

    private static string CreateEpisodeUploadOverrideConfig(ProjectListItemViewModel project, IReadOnlyCollection<int>? onlyEpisodes)
    {
        var configPath = ResolveWeixinUploadConfigPath(project);
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            throw new FileNotFoundException("鏈壘鍒板井淇″墽闆嗕笂浼犻厤缃枃浠躲€?, configPath);
        }

        var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
            ?? throw new InvalidOperationException("寰俊鍓ч泦涓婁紶閰嶇疆鏂囦欢鏍煎紡鏃犳晥銆?);
        var secondPage = root["second_page"] as JsonObject ?? new JsonObject();
        root["second_page"] = secondPage;
        var upload = secondPage["upload"] as JsonObject ?? new JsonObject();
        secondPage["upload"] = upload;
        var uploadQueue = secondPage["upload_queue"] as JsonObject ?? new JsonObject();
        secondPage["upload_queue"] = uploadQueue;

        var selectedEpisodes = onlyEpisodes?.Where(item => item > 0).ToHashSet() ??
            project.EpisodeUploadEpisodes
                .Where(item => !project.SkippedEpisodeUploadEpisodes.Contains(item.EpisodeNumber))
                .Select(item => item.EpisodeNumber)
                .ToHashSet();
        if (selectedEpisodes.Count == 0)
        {
            throw new InvalidOperationException("褰撳墠娌℃湁鍙墽琛屼笂浼犵殑鍓ч泦銆?);
        }

        var uploadItems = ResolveEpisodeUploadQueueItems(uploadQueue, upload)
            .Where(item => item.EpisodeNumber > 0 && selectedEpisodes.Contains(item.EpisodeNumber))
            .ToArray();
        if (uploadItems.Length == 0)
        {
            throw new InvalidOperationException("鏈湪涓婁紶閰嶇疆涓壘鍒板彲鎵ц鐨勫墽闆嗚棰戙€?);
        }

        upload["paths"] = new JsonArray(uploadItems.Select(item => (JsonNode)item.Path).ToArray());
        uploadQueue["mode"] = "sequential";
        uploadQueue["items"] = new JsonArray(uploadItems.Select(item => (JsonNode)new JsonObject
        {
            ["path"] = item.Path,
            ["enabled"] = true
        }).ToArray());

        var tempPath = Path.Combine(project.WorkflowProjectDir, $".weixin-upload-override-{Guid.NewGuid():N}.json");
        File.WriteAllText(tempPath, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        return tempPath;
    }

    private static string? ResolveWeixinUploadConfigPath(ProjectListItemViewModel project)
    {
        foreach (var name in WeixinUploadConfigNames)
        {
            var candidate = Path.Combine(project.WorkflowProjectDir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<(int EpisodeNumber, string Path)> ResolveEpisodeUploadQueueItems(JsonObject uploadQueue, JsonObject upload)
    {
        var items = new List<(int EpisodeNumber, string Path)>();
        if (uploadQueue["items"] is JsonArray queueItems)
        {
            foreach (var node in queueItems.OfType<JsonObject>())
            {
                var path = node["path"]?.GetValue<string>()?.Trim();
                var enabled = node["enabled"]?.GetValue<bool?>() ?? true;
                if (!enabled || string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                items.Add((TryExtractDownloadedEpisodeNumber(path) ?? 0, path));
            }
        }

        if (items.Count > 0)
        {
            return items;
        }

        if (upload["paths"] is JsonArray uploadPaths)
        {
            foreach (var pathNode in uploadPaths)
            {
                var path = pathNode?.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                items.Add((TryExtractDownloadedEpisodeNumber(path) ?? 0, path));
            }
        }

        return items;
    }

    public void SetAllProjectsChecked(bool isChecked)
    {
        var targets = string.IsNullOrWhiteSpace(TaskQueueFilterText) ? Projects : FilteredProjects;
        foreach (var project in targets)
        {
            project.IsChecked = isChecked;
        }

        OnPropertyChanged(nameof(CheckedProjectsSummary));
        OnPropertyChanged(nameof(TaskQueueSummary));
        RefreshCommandStates();
    }

    private async Task ExecuteCheckedProjectsAsync(string? stepKey, string stepLabel)
    {
        var selectedProjects = Projects.Where(item => item.IsChecked).ToArray();
        if (selectedProjects.Length == 0)
        {
            return;
        }

        ActivityTitle = stepKey is null
            ? $"鎵归噺椤圭洰鏃ュ織 路 {stepLabel}"
            : $"鎵归噺姝ラ鏃ュ織 路 {stepLabel}";

        await RunBusyAsync(
            stepKey is null
                ? $"姝ｅ湪鎵归噺鎵ц{stepLabel}锛屽叡 {selectedProjects.Length} 涓」鐩?.."
                : $"姝ｅ湪鎵归噺鎵ц{stepLabel}锛屽叡 {selectedProjects.Length} 涓」鐩?..",
            async cancellationToken =>
            {
                foreach (var project in selectedProjects)
                {
                    project.MarkQueued();
                }

                var mode = SelectedExecutionModeOption?.Key ?? ExecutionModeSerial;
                if (string.Equals(mode, ExecutionModeConcurrent2, StringComparison.Ordinal))
                {
                    await ExecuteCheckedProjectsConcurrentAsync(selectedProjects, stepKey, stepLabel, cancellationToken);
                }
                else
                {
                    await ExecuteCheckedProjectsSerialAsync(selectedProjects, stepKey, stepLabel, cancellationToken);
                }

                await RefreshProjectListAsync();
                StatusMessage = stepKey is null
                    ? $"鎵归噺鍏ㄦ祦绋嬫墽琛屽畬鎴愶紝鍏卞鐞?{selectedProjects.Length} 涓」鐩€?
                    : $"鎵归噺{stepLabel}瀹屾垚锛屽叡澶勭悊 {selectedProjects.Length} 涓」鐩€?;
                AppendLog(StatusMessage);
                await TryNotifyFeishuQueueSummaryAsync(selectedProjects, stepKey is null ? "鎵归噺鍏ㄦ祦绋? : $"鎵归噺{stepLabel}", cancellationToken);
            });
    }

    private async Task ExecuteQueueSelectionForProjectAsync(
        ProjectListItemViewModel project,
        int index,
        int total,
        CancellationToken cancellationToken)
    {
        project.MarkRunning("浠诲姟闃熷垪");
        ClearLogsForProject(project.ProjectKey);

        var selectedSteps = GetTaskQueueSelectedSteps();
        var prefix = total > 1 ? $"[{index}/{total}] " : string.Empty;

        foreach (var (stepKey, stepLabel) in selectedSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            project.MarkRunning(stepLabel);

            if (string.Equals(stepKey, "__project-material__", StringComparison.Ordinal))
            {
                var pipelineOk = await RunProjectMaterialPipelineCoreAsync(project, cancellationToken, index, total);
                if (!pipelineOk)
                {
                    project.MarkFailed();
                    return;
                }

                continue;
            }

            await TryNotifyFeishuStepAsync(project, stepKey, stepLabel, "before", null, null, cancellationToken);
            var progress = CreateBufferedProgress();
            var result = await _workService.RunProjectStepAsync(
                project.SourceProjectDir,
                null,
                stepKey,
                force: false,
                progress,
                cancellationToken);

            AppendLog(
                $"{prefix}鑺傜偣瀹屾垚: {project.DisplayName} 路 {stepLabel}锛岀粨鏋?{(result.Ok ? "鎴愬姛" : "澶辫触")}",
                project.ProjectKey,
                project.DisplayName,
                stepKey,
                stepLabel,
                !result.Ok);
            await TryNotifyFeishuStepAsync(project, stepKey, stepLabel, "after", result.Ok, result.Message, cancellationToken);

            if (!result.Ok)
            {
                project.MarkFailed();
                return;
            }
        }

        project.MarkCompleted();
    }

    private async Task ExecuteCheckedProjectsSerialAsync(
        ProjectListItemViewModel[] selectedProjects,
        string? stepKey,
        string stepLabel,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < selectedProjects.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = selectedProjects[index];
            await ExecuteProjectBatchItemAsync(project, stepKey, stepLabel, index + 1, selectedProjects.Length, cancellationToken);
        }
    }

    private async Task ExecuteCheckedProjectsConcurrentAsync(
        ProjectListItemViewModel[] selectedProjects,
        string? stepKey,
        string stepLabel,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(2);
        var tasks = selectedProjects.Select((project, index) => RunConcurrentProjectAsync(
            project,
            stepKey,
            stepLabel,
            index + 1,
            selectedProjects.Length,
            gate,
            cancellationToken));
        await Task.WhenAll(tasks);
    }

    private async Task RunConcurrentProjectAsync(
        ProjectListItemViewModel project,
        string? stepKey,
        string stepLabel,
        int index,
        int total,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await ExecuteProjectBatchItemAsync(project, stepKey, stepLabel, index, total, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ExecuteProjectBatchItemAsync(
        ProjectListItemViewModel project,
        string? stepKey,
        string stepLabel,
        int index,
        int total,
        CancellationToken cancellationToken)
    {
        project.MarkRunning(stepKey is null ? "鍏ㄦ祦绋? : stepLabel);
        ClearLogsForProject(project.ProjectKey);

        try
        {
            if (stepKey is null)
            {
                var result = await RunProjectWithSmartResumeAsync(project, cancellationToken, index, total);
                if (result.Ok) project.MarkCompleted();
                else project.MarkFailed();
                return;
            }

            if (string.Equals(stepKey, "__project-material__", StringComparison.Ordinal))
            {
                var pipelineOk = await RunProjectMaterialPipelineCoreAsync(project, cancellationToken, index, total);
                if (pipelineOk) project.MarkCompleted();
                else project.MarkFailed();
                return;
            }

            await TryNotifyFeishuStepAsync(project, stepKey, stepLabel, "before", null, null, cancellationToken);
            var progressForStep = CreateBufferedProgress();
            var stepResult = await _workService.RunProjectStepAsync(
                project.SourceProjectDir,
                null,
                stepKey,
                force: true,
                progressForStep,
                cancellationToken);

            if (stepResult.Ok)
            {
                project.MarkCompleted();
            }
            else
            {
                project.MarkFailed();
            }

            await TryNotifyFeishuStepAsync(project, stepKey, stepLabel, "after", stepResult.Ok, stepResult.Message, cancellationToken);
            AppendLog($"[{index}/{total}] 姝ラ瀹屾垚: {project.DisplayName} 路 {stepLabel}锛岀粨鏋?{(stepResult.Ok ? "鎴愬姛" : "澶辫触")}");
        }
        catch (OperationCanceledException)
        {
            project.MarkStopped();
            throw;
        }
        catch
        {
            project.MarkFailed();
            throw;
        }
    }

    private async Task ExecuteProjectMaterialPipelineAsync(ProjectListItemViewModel project, string titlePrefix)
    {
        ClearMaterialValidationIssues();
        ClearLogsForProject(project.ProjectKey);
        ActivityTitle = $"{titlePrefix} 路 {project.DisplayName}";
        await RunBusyAsync($"姝ｅ湪鐢熸垚椤圭洰绱犳潗锛歿project.DisplayName}", async cancellationToken =>
        {
            project.MarkRunning("涓€閿敓鎴愰」鐩礌鏉?);
            var ok = await RunProjectMaterialPipelineCoreAsync(project, cancellationToken);
            if (ok) project.MarkCompleted();
            else project.MarkFailed();
            await RefreshAfterExecutionAsync(project.ProjectKey);
        });
    }

    private async Task<bool> RunProjectMaterialPipelineCoreAsync(
        ProjectListItemViewModel project,
        CancellationToken cancellationToken,
        int? batchIndex = null,
        int? batchTotal = null)
    {
        var prefix = batchIndex.HasValue && batchTotal.HasValue
            ? $"[{batchIndex}/{batchTotal}] "
            : string.Empty;

        for (var index = 0; index < ProjectMaterialPipelineSteps.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (stepKey, stepLabel) = ProjectMaterialPipelineSteps[index];
            project.MarkRunning(stepLabel);
            await TryNotifyFeishuStepAsync(project, stepKey, stepLabel, "before", null, null, cancellationToken);
            AppendLog(
                $"{prefix}椤圭洰绱犳潗娴佺▼ {index + 1}/{ProjectMaterialPipelineSteps.Length}: {stepLabel}",
                project.ProjectKey,
                project.DisplayName,
                stepKey,
                stepLabel);

            var progress = CreateBufferedProgress();
            var result = await _workService.RunProjectStepAsync(
                project.SourceProjectDir,
                null,
                stepKey,
                force: false,
                progress,
                cancellationToken);

            AppendLog(
                $"{prefix}姝ラ瀹屾垚: {project.DisplayName} 路 {stepLabel}锛岀粨鏋?{(result.Ok ? "鎴愬姛" : "澶辫触")}",
                project.ProjectKey,
                project.DisplayName,
                stepKey,
                stepLabel,
                !result.Ok);
            await TryNotifyFeishuStepAsync(project, stepKey, stepLabel, "after", result.Ok, result.Message, cancellationToken);

            if (!result.Ok)
            {
                return false;
            }
        }

        AppendLog(
            $"{prefix}椤圭洰绱犳潗鐢熸垚瀹屾垚: {project.DisplayName}",
            project.ProjectKey,
            project.DisplayName,
            string.Empty,
            "涓€閿敓鎴愰」鐩礌鏉?);
        return true;
    }

    private async Task SearchDramaAsync()
    {
        var keyword = SearchKeyword.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            SearchSummary = "璇疯緭鍏ュ墽鍚嶅叧閿瘝銆?;
            return;
        }

        _lastSearchKeyword = keyword;
        _searchMode = SearchModeKeyword;
        CurrentSearchPage = 1;
        await LoadSearchResultsAsync();
    }

    private async Task LoadTodayDramaAsync()
    {
        _searchMode = SearchModeToday;
        _lastSearchKeyword = string.Empty;
        CurrentSearchPage = 1;
        await LoadSearchResultsAsync();
    }

    private async Task LoadMangaTodayDramaAsync()
    {
        _searchMode = SearchModeMangaToday;
        _lastSearchKeyword = string.Empty;
        CurrentSearchPage = 1;
        await LoadSearchResultsAsync();
    }

    private async Task LoadAiTodayDramaAsync()
    {
        _searchMode = SearchModeAiToday;
        _lastSearchKeyword = string.Empty;
        CurrentSearchPage = 1;
        await LoadSearchResultsAsync();
    }

    private async Task LoadHistoryDramaAsync()
    {
        _searchMode = SearchModeHistory;
        _lastSearchKeyword = string.Empty;
        CurrentSearchPage = 1;
        await LoadSearchResultsAsync();
    }

    private async Task GoPreviousSearchPageAsync()
    {
        if (CurrentSearchPage <= 1)
        {
            return;
        }

        CurrentSearchPage--;
        await LoadSearchResultsAsync();
    }

    private async Task GoNextSearchPageAsync()
    {
        CurrentSearchPage++;
        await LoadSearchResultsAsync();
    }

    private async Task LoadSearchResultsAsync()
    {
        var busyMessage = _searchMode switch
        {
            SearchModeToday => "姝ｅ湪鑾峰彇浠婃棩涓婃柊...",
            SearchModeMangaToday => $"姝ｅ湪鑾峰彇婕墽涓婃柊锛屾煡璇㈣繎 {ParseSearchQueryDays()} 澶?..",
            SearchModeAiToday => $"姝ｅ湪鑾峰彇 AI 鐭墽涓婃柊锛屾煡璇㈣繎 {ParseSearchQueryDays()} 澶?..",
            SearchModeHistory => $"姝ｅ湪鑾峰彇鍘嗗彶涓婃柊锛屾煡璇㈣繎 {ParseSearchQueryDays()} 澶?..",
            _ => $"姝ｅ湪鎼滅储鐭墽锛歿_lastSearchKeyword}"
        };

        await RunSearchBusyAsync(busyMessage, async cancellationToken =>
        {
            var queryDays = ParseSearchQueryDays();
            IReadOnlyList<DramaSearchItem> results = _searchMode switch
            {
                SearchModeToday => await _dramaSearchService.GetTodayAsync(cancellationToken),
                SearchModeMangaToday => await LoadRouterSearchResultsAsync(
                    router => router.GetMangaTodayAsync(queryDays, cancellationToken),
                    fallback: () => _dramaSearchService.GetTodayAsync(cancellationToken)),
                SearchModeAiToday => await LoadRouterSearchResultsAsync(
                    router => router.GetAiTodayAsync(queryDays, cancellationToken),
                    fallback: () => _dramaSearchService.GetTodayAsync(cancellationToken)),
                SearchModeHistory => await LoadRouterSearchResultsAsync(
                    router => router.GetHistoryAsync(queryDays, cancellationToken),
                    fallback: () => _dramaSearchService.GetTodayAsync(cancellationToken)),
                _ => await _dramaSearchService.SearchAsync(_lastSearchKeyword, CurrentSearchPage, cancellationToken)
            };

            _loadedSearchItems.Clear();
            _loadedSearchItems.AddRange(results);
            ApplyLoadedSearchResults(appendLog: true);
        });
    }

    private async Task RunSearchBusyAsync(string busyMessage, Func<CancellationToken, Task> action)
    {
        IsSearchBusy = true;
        StatusMessage = busyMessage;
        AppendLog(busyMessage);
        RefreshCommandStates();

        try
        {
            await action(CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            AppendLog($"鎼滅储澶辫触: {ex.Message}", string.Empty, string.Empty, "search", "鐭墽鎼滅储", isFailure: true);
        }
        finally
        {
            IsSearchBusy = false;
            RefreshCommandStates();
        }
    }

    private string BuildSearchSummary(int totalCount, int filteredCount, int visibleCount, int pageSize)
    {
        if (string.Equals(_searchMode, SearchModeKeyword, StringComparison.Ordinal))
        {
            return totalCount == 0
                ? $"绗?{CurrentSearchPage} 椤垫湭鎵惧埌鈥渰_lastSearchKeyword}鈥濈殑鍖归厤缁撴灉銆?
                : filteredCount == 0
                    ? $"鈥渰_lastSearchKeyword}鈥濈 {CurrentSearchPage} 椤靛師濮?{totalCount} 鏉★紝绛涢€夊悗 0 鏉°€?
                    : $"鈥渰_lastSearchKeyword}鈥濈 {CurrentSearchPage} 椤靛師濮?{totalCount} 鏉★紝绛涢€夊悗 {filteredCount} 鏉★紝褰撳墠灞曠ず {visibleCount} 鏉★紝鍒嗛〉澶у皬 {pageSize}銆?;
        }

        var modeLabel = ResolveSearchModeLabel();
        return totalCount == 0
            ? $"{modeLabel}鏆傛棤缁撴灉銆?
            : filteredCount == 0
                ? $"{modeLabel}鍘熷 {totalCount} 鏉★紝绛涢€夊悗 0 鏉°€?
                : $"{modeLabel}鍘熷 {totalCount} 鏉★紝绛涢€夊悗 {filteredCount} 鏉★紝褰撳墠灞曠ず {visibleCount} 鏉★紝鍒嗛〉澶у皬 {pageSize}銆?;
    }

    private void ApplySearchFilters()
    {
        if (_loadedSearchItems.Count == 0)
        {
            return;
        }

        ApplyLoadedSearchResults(appendLog: true);
    }

    private async Task<IReadOnlyList<DramaSearchItem>> LoadRouterSearchResultsAsync(
        Func<DramaSourceRouter, Task<IReadOnlyList<DramaSearchItem>>> loadWithRouter,
        Func<Task<IReadOnlyList<DramaSearchItem>>> fallback)
    {
        if (_dramaSearchService is DramaSourceRouter router)
        {
            return await loadWithRouter(router);
        }

        return await fallback();
    }

    private void ApplyLoadedSearchResults(bool appendLog)
    {
        var filteredResults = FilterLoadedSearchResults().ToArray();
        var pageSize = ParseSearchPageSize();
        var visibleResults = filteredResults.Take(pageSize).ToArray();

        UnsubscribeFromSearchResultRows();
        SearchResults.Clear();
        foreach (var item in visibleResults)
        {
            var row = new SearchResultRowViewModel(item);
            row.CheckedChanged += OnSearchResultRowCheckedChanged;
            SearchResults.Add(row);
        }

        SelectedSearchResult = SearchResults.FirstOrDefault();
        SearchSummary = BuildSearchSummary(_loadedSearchItems.Count, filteredResults.Length, visibleResults.Length, pageSize);
        StatusMessage = SearchSummary;
        OnPropertyChanged(nameof(SearchPageText));
        OnPropertyChanged(nameof(CheckedSearchResultsSummary));
        if (appendLog)
        {
            AppendLog(SearchSummary);
        }
    }

    private IEnumerable<DramaSearchItem> FilterLoadedSearchResults()
    {
        var filtered = _loadedSearchItems.AsEnumerable();

        var keyword = SearchKeyword.Trim();
        if (ExactSearchEnabled && !string.IsNullOrWhiteSpace(keyword))
        {
            filtered = filtered.Where(item => string.Equals(item.Title.Trim(), keyword, StringComparison.Ordinal));
        }

        var minEpisodes = ParseOptionalNonNegativeInt(SearchMinEpisodeCount);
        var maxEpisodes = ParseOptionalNonNegativeInt(SearchMaxEpisodeCount);
        if (minEpisodes.HasValue && maxEpisodes.HasValue && maxEpisodes.Value < minEpisodes.Value)
        {
            (minEpisodes, maxEpisodes) = (maxEpisodes, minEpisodes);
        }

        if (minEpisodes is > 0)
        {
            filtered = filtered.Where(item => item.EpisodeTotal >= minEpisodes.Value);
        }

        if (maxEpisodes is > 0)
        {
            filtered = filtered.Where(item => item.EpisodeTotal <= maxEpisodes.Value);
        }

        return filtered;
    }

    private static int? ParseOptionalNonNegativeInt(string? value)
    {
        return int.TryParse(value, out var parsed) && parsed >= 0
            ? parsed
            : null;
    }

    private int ParseSearchQueryDays()
    {
        return int.TryParse(SearchQueryDays, out var days)
            ? Math.Clamp(days, 1, 30)
            : 1;
    }

    private string ResolveSearchModeLabel()
    {
        return _searchMode switch
        {
            SearchModeToday => "浠婃棩涓婃柊",
            SearchModeMangaToday => $"婕墽涓婃柊锛堣繎 {ParseSearchQueryDays()} 澶╋級",
            SearchModeAiToday => $"AI 鐭墽涓婃柊锛堣繎 {ParseSearchQueryDays()} 澶╋級",
            SearchModeHistory => $"鍘嗗彶涓婃柊锛堣繎 {ParseSearchQueryDays()} 澶╋級",
            _ => "鐭墽鎼滅储"
        };
    }

    private Task ImportCheckedDramaAsync() => ImportCheckedDramaCoreAsync(runWorkflow: false);

    private Task ImportAndRunCheckedDramaAsync() => ImportCheckedDramaCoreAsync(runWorkflow: true);

    private async Task DownloadCheckedDramaAsync()
    {
        var selectedRows = SearchResults.Where(item => item.IsChecked).ToArray();
        if (selectedRows.Length == 0)
        {
            return;
        }

        if (!TryBuildDownloadEpisodeSelection(out var episodes))
        {
            StatusMessage = "涓嬭浇闆嗘暟鑼冨洿鏃犳晥锛岃杈撳叆濡?1-5 鎴?1,3,5銆?;
            AppendLog(StatusMessage, string.Empty, string.Empty, "search", "鐭墽鎼滅储", isFailure: true);
            return;
        }

        var selectionLabel = string.Equals(episodes, "all", StringComparison.OrdinalIgnoreCase) ? "鍏ㄩ儴" : episodes;
        await RunSearchBusyAsync($"姝ｅ湪涓嬭浇鍕鹃€夐」鐩紝鍏?{selectedRows.Length} 涓」鐩紝鑼冨洿锛歿selectionLabel}...", async cancellationToken =>
        {
            var processed = 0;

            foreach (var row in selectedRows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var bootstrap = await _projectBootstrapper.BootstrapAsync(
                    new DramaProjectBootstrapRequest(
                        RootDir: RootDir,
                        Drama: row.Drama,
                        CompanyName: CompanyName,
                        Episodes: episodes),
                    cancellationToken);

                processed++;
                AppendLog(
                    $"[{processed}/{selectedRows.Length}] 宸插啓鍏ヤ笅杞借寖鍥村苟鍑嗗涓嬭浇锛歿bootstrap.DisplayName}锛坽selectionLabel}锛?,
                    bootstrap.ProjectKey,
                    bootstrap.DisplayName,
                    "download",
                    "涓嬭浇鍓ч泦");

                var result = await _workService.RunProjectStepAsync(
                    bootstrap.SourceProjectDir,
                    null,
                    "download",
                    force: true,
                    progress: null,
                    cancellationToken);

                AppendLog(
                    $"[{processed}/{selectedRows.Length}] 涓嬭浇瀹屾垚锛歿result.DisplayName}锛岀粨鏋?{(result.Ok ? "鎴愬姛" : "澶辫触")}",
                    result.ProjectKey,
                    result.DisplayName,
                    "download",
                    "涓嬭浇鍓ч泦",
                    !result.Ok);
            }

            await RefreshProjectListAsync();
            StatusMessage = $"鍕鹃€夐」鐩笅杞藉畬鎴愶紝鍏卞鐞?{selectedRows.Length} 涓紝鑼冨洿锛歿selectionLabel}銆?;
            AppendLog(StatusMessage);
        });
    }

    private async Task ImportCheckedDramaCoreAsync(bool runWorkflow)
    {
        var selectedRows = SearchResults.Where(item => item.IsChecked).ToArray();
        if (selectedRows.Length == 0)
        {
            return;
        }

        var actionText = runWorkflow ? "瀵煎叆骞舵墽琛屽嬀閫夐」鐩叏娴佺▼" : "瀵煎叆鍕鹃€夐」鐩埌宸ヤ綔鐩綍";
        Func<string, Func<CancellationToken, Task>, Task> runner = runWorkflow
            ? RunBusyAsync
            : RunSearchBusyAsync;

        await runner($"姝ｅ湪{actionText}锛屽叡 {selectedRows.Length} 涓」鐩?..", async cancellationToken =>
        {
            var processed = 0;

            foreach (var row in selectedRows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var bootstrap = await _projectBootstrapper.BootstrapAsync(
                    new DramaProjectBootstrapRequest(
                        RootDir: RootDir,
                        Drama: row.Drama,
                        CompanyName: CompanyName),
                    cancellationToken);

                processed++;
                AppendLog(bootstrap.Created
                    ? $"[{processed}/{selectedRows.Length}] 宸插鍏ラ」鐩細{bootstrap.DisplayName}"
                    : $"[{processed}/{selectedRows.Length}] 椤圭洰宸插瓨鍦紝澶嶇敤宸ヤ綔鐩綍锛歿bootstrap.DisplayName}");

                if (!runWorkflow)
                {
                    continue;
                }

                ClearLogsForProject(bootstrap.ProjectKey);
                ActivityTitle = $"椤圭洰鏃ュ織 路 {bootstrap.DisplayName}";
                var progress = CreateBufferedProgress();
                var result = await _workService.RunProjectAsync(
                    bootstrap.SourceProjectDir,
                    null,
                    force: false,
                    progress,
                    cancellationToken);

                AppendLog($"椤圭洰瀹屾垚: {result.DisplayName}锛岀粨鏋?{(result.Ok ? "鎴愬姛" : "澶辫触")}");
            }

            await RefreshProjectListAsync();
            StatusMessage = runWorkflow
                ? $"鍕鹃€夐」鐩凡鎵ц瀹屾垚锛屽叡澶勭悊 {selectedRows.Length} 涓€?
                : $"鍕鹃€夐」鐩凡瀵煎叆瀹屾垚锛屽叡澶勭悊 {selectedRows.Length} 涓€?;
            AppendLog(StatusMessage);
        });
    }

    private bool TryBuildDownloadEpisodeSelection(out string selection)
    {
        var mode = SelectedDownloadEpisodeRangeOption?.Key ?? EpisodeRangeAll;
        switch (mode)
        {
            case EpisodeRangeAll:
                selection = "all";
                return true;
            case EpisodeRangeFirst3:
                selection = "1-3";
                return true;
            case EpisodeRangeCustom:
                selection = NormalizeEpisodeSelection(CustomDownloadEpisodeRange);
                return !string.IsNullOrWhiteSpace(selection);
            default:
                selection = "all";
                return true;
        }
    }

    private static string NormalizeEpisodeSelection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalized = new List<string>();
        foreach (var part in parts)
        {
            if (part.Contains('-', StringComparison.Ordinal))
            {
                var range = part.Split('-', 2, StringSplitOptions.TrimEntries);
                if (range.Length != 2 ||
                    !int.TryParse(range[0], out var start) ||
                    !int.TryParse(range[1], out var end) ||
                    start <= 0 ||
                    end <= 0)
                {
                    return string.Empty;
                }

                normalized.Add(start <= end ? $"{start}-{end}" : $"{end}-{start}");
                continue;
            }

            if (!int.TryParse(part, out var single) || single <= 0)
            {
                return string.Empty;
            }

            normalized.Add(single.ToString());
        }

        return string.Join(",", normalized);
    }

    private async Task ExecuteProjectAsync(string projectKey, string displayName, string sourceProjectDir)
    {
        ClearLogsForProject(projectKey);
        ActivityTitle = $"椤圭洰鏃ュ織 路 {displayName}";
        await RunBusyAsync($"姝ｅ湪鎵ц椤圭洰锛歿displayName}", async cancellationToken =>
        {
            var project = Projects.FirstOrDefault(item => string.Equals(item.ProjectKey, projectKey, StringComparison.Ordinal));
            var result = project is null
                ? await RunProjectNormallyAsync(sourceProjectDir, cancellationToken)
                : await RunProjectWithSmartResumeAsync(project, cancellationToken);

            AppendLog($"椤圭洰瀹屾垚: {result.DisplayName}锛岀粨鏋?{(result.Ok ? "鎴愬姛" : "澶辫触")}", projectKey, displayName, string.Empty, string.Empty, !result.Ok);
            await RefreshAfterExecutionAsync(result.ProjectKey);
        });
    }

    private async Task<ProjectWorkResult> RunProjectWithSmartResumeAsync(
        ProjectListItemViewModel project,
        CancellationToken cancellationToken,
        int? batchIndex = null,
        int? batchTotal = null)
    {
        var smartStep = ResolveSmartResumeStep(project);
        if (string.IsNullOrWhiteSpace(smartStep))
        {
            var projectResult = await RunProjectNormallyAsync(project.SourceProjectDir, cancellationToken);
            var prefix = batchIndex.HasValue && batchTotal.HasValue ? $"[{batchIndex}/{batchTotal}] " : string.Empty;
            AppendLog($"{prefix}椤圭洰瀹屾垚: {projectResult.DisplayName}锛岀粨鏋?{(projectResult.Ok ? "鎴愬姛" : "澶辫触")}");
            return projectResult;
        }

        var stepLabel = ResolveStepLabel(smartStep);
        var prefixMessage = batchIndex.HasValue && batchTotal.HasValue ? $"[{batchIndex}/{batchTotal}] " : string.Empty;
        AppendLog($"{prefixMessage}妫€娴嬪埌椤圭洰缂哄皯涓嬭浇鍏冩暟鎹紝鑷姩浠庘€渰stepLabel}鈥濈户缁€?, project.ProjectKey, project.DisplayName, smartStep, stepLabel);

        var progress = CreateBufferedProgress();
        var stepResult = await _workService.RunProjectStepAsync(
            project.SourceProjectDir,
            null,
            smartStep,
            force: true,
            progress,
            cancellationToken);

        AppendLog($"{prefixMessage}姝ラ瀹屾垚: {project.DisplayName} 路 {stepLabel}锛岀粨鏋?{(stepResult.Ok ? "鎴愬姛" : "澶辫触")}");
        return stepResult;
    }

    private async Task<ProjectWorkResult> RunProjectNormallyAsync(string sourceProjectDir, CancellationToken cancellationToken)
    {
        var progress = CreateBufferedProgress();
        return await _workService.RunProjectAsync(
            sourceProjectDir,
            null,
            force: false,
            progress,
            cancellationToken);
    }

    private string? ResolveSmartResumeStep(ProjectListItemViewModel project)
    {
        var downloadInspection = LocalProjectDownloadInspector.Inspect(project.SourceProjectDir);
        if (HasBookIdMetadata(project.SourceProjectDir) || !downloadInspection.IsComplete)
        {
            return null;
        }

        var step = NormalizeStepKey(project.FailedStep) ?? NormalizeStepKey(project.ResumeFrom);
        return step switch
        {
            null => "transcode",
            "download" => "transcode",
            _ => step
        };
    }

    private static string? NormalizeStepKey(string? stepKey)
    {
        return string.IsNullOrWhiteSpace(stepKey)
            ? null
            : stepKey.Trim().ToLowerInvariant() switch
            {
                "download" => "download",
                "transcode" => "transcode",
                "rewrite" => "rewrite",
                "poster-rename" => "poster-rename",
                "project-image" => "project-image",
                "cost-report" => "cost-report",
                "batch-file-rename" => "batch-file-rename",
                "material-convert" => "material-convert",
                "weixin-upload" => "weixin-upload",
                "weixin-material-upload" => "weixin-material-upload",
                _ => stepKey
            };
    }

    private static bool HasBookIdMetadata(string projectDir)
    {
        var bookIdPath = Path.Combine(projectDir, "book_id.txt");
        if (File.Exists(bookIdPath) && !string.IsNullOrWhiteSpace(File.ReadAllText(bookIdPath).Trim()))
        {
            return true;
        }

        var metadataPath = Path.Combine(projectDir, "shortdrama-project.json");
        if (!File.Exists(metadataPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var root = document.RootElement;
            return TryGetNonEmptyString(root, "bookId") is not null ||
                   TryGetNonEmptyString(root, "book_id") is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetNonEmptyString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int? TryGetInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out intValue))
        {
            return intValue;
        }

        return null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private async Task RunBusyAsync(string busyMessage, Func<CancellationToken, Task> action)
    {
        IsBusy = true;
        StatusMessage = busyMessage;
        AppendLog(busyMessage);
        _currentOperationCts = new CancellationTokenSource();
        RefreshCommandStates();

        try
        {
            await action(_currentOperationCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "宸插仠姝㈠綋鍓嶈繍琛岋紝杩涘害宸蹭繚瀛橈紝鍙户缁繍琛屻€?;
            AppendLog(StatusMessage);
            await RefreshProjectListAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            AppendLog($"閿欒: {ex.Message}", string.Empty, string.Empty, string.Empty, string.Empty, isFailure: true);
        }
        finally
        {
            _currentOperationCts.Dispose();
            _currentOperationCts = null;
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    private void StopCurrentRun()
    {
        if (_currentOperationCts is null || _currentOperationCts.IsCancellationRequested)
        {
            return;
        }

        _currentOperationCts.Cancel();
        StatusMessage = "姝ｅ湪鍋滄褰撳墠杩愯骞朵繚瀛樿繘搴?..";
        AppendLog(StatusMessage);
        RefreshCommandStates();
    }

    private void ResolveInteraction(string decision)
    {
        if (!_interactionService.TryResolve(decision))
        {
            return;
        }

        var actionLabel = decision switch
        {
            "resume" => "缁х画鎵ц",
            "skip_video" => "璺宠繃褰撳墠绱犳潗",
            "skip_project" => "璺宠繃褰撳墠椤圭洰",
            "stop" => "鍋滄浠诲姟",
            _ => decision
        };
        StatusMessage = $"宸叉彁浜や汉宸ヤ粙鍏ュ喅绛栵細{actionLabel}";
        AppendLog(StatusMessage);
    }

    private IProgress<WorkRunEvent> CreateBufferedProgress()
    {
        return new BufferedUiProgress<WorkRunEvent>(events =>
        {
            foreach (var evt in events)
            {
                HandleProgress(evt);
            }
        });
    }

    private void HandleProgress(WorkRunEvent evt)
    {
        var project = Projects.FirstOrDefault(item => string.Equals(item.ProjectKey, evt.ProjectKey, StringComparison.Ordinal));
        project?.ApplyProgress(evt);

        var label = string.IsNullOrWhiteSpace(evt.StepType) ? evt.Kind : evt.StepType;
        AppendLog(
            $"{label} {evt.Message}".Trim(),
            evt.ProjectKey,
            evt.DisplayName,
            evt.StepType ?? string.Empty,
            ResolveStepLabel(evt.StepType),
            evt.Ok == false || string.Equals(evt.Kind, "step-failed", StringComparison.Ordinal));

        RefreshProjectMaterialSteps();
        ApplyProjectMaterialStepLogs();
    }

    private void RefreshProjectMaterialSteps()
    {
        ProjectMaterialSteps.Clear();
        if (SelectedProject is null)
        {
            SelectedProjectMaterialStep = null;
            return;
        }

        var previousKey = SelectedProjectMaterialStep?.Key;
        var tempSteps = ProjectMaterialPipelineSteps
            .Select((item, idx) => (
                Index: idx + 1,
                Key: item.Key,
                Label: item.Label,
                Status: SelectedProject.GetProjectMaterialStepStatus(item.Key),
                Summary: SelectedProject.GetProjectMaterialStepSummary(item.Key)))
            .ToArray();

        var selectedKey = !string.IsNullOrWhiteSpace(previousKey) &&
                          tempSteps.Any(item => string.Equals(item.Key, previousKey, StringComparison.Ordinal))
            ? previousKey
            : ResolveDefaultProjectMaterialStepKey(tempSteps);

        foreach (var step in tempSteps)
        {
            var isSelected = string.Equals(step.Key, selectedKey, StringComparison.Ordinal);
            var (background, accent, title, sub) = ResolveProjectMaterialStepBrushes(step.Status, isSelected);
            ProjectMaterialSteps.Add(new ProjectMaterialStepItem(
                step.Index,
                step.Key,
                step.Label,
                step.Status,
                step.Summary,
                isSelected,
                ShowConnector: step.Index < tempSteps.Length,
                background,
                accent,
                title,
                sub));
        }

        SelectedProjectMaterialStep = ProjectMaterialSteps.FirstOrDefault(item => string.Equals(item.Key, selectedKey, StringComparison.Ordinal))
            ?? ProjectMaterialSteps.FirstOrDefault();
    }

    private static string? ResolveDefaultProjectMaterialStepKey(IEnumerable<(int Index, string Key, string Label, string Status, string Summary)> steps)
    {
        var materialSteps = steps.ToArray();
        var running = materialSteps.FirstOrDefault(item => string.Equals(item.Status, "杩涜涓?, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(running.Key))
        {
            return running.Key;
        }

        var blocked = materialSteps.FirstOrDefault(item =>
            string.Equals(item.Status, "澶辫触", StringComparison.Ordinal) ||
            string.Equals(item.Status, "寰呯户缁?, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(blocked.Key))
        {
            return blocked.Key;
        }

        var pending = materialSteps.FirstOrDefault(item => !string.Equals(item.Status, "宸插畬鎴?, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(pending.Key))
        {
            return pending.Key;
        }

        return materialSteps.Length > 0 ? materialSteps[0].Key : null;
    }

    private static (IBrush Background, IBrush Accent, IBrush Title, IBrush Summary) ResolveProjectMaterialStepBrushes(string status, bool isSelected)
    {
        return status switch
        {
            "宸插畬鎴? => (isSelected ? new SolidColorBrush(Color.Parse("#112F1D")) : Brushes.Transparent, Brushes.LimeGreen, Brushes.LimeGreen, Brushes.LightGreen),
            "杩涜涓? => (isSelected ? new SolidColorBrush(Color.Parse("#102A44")) : new SolidColorBrush(Color.Parse("#0A1A2A")), Brushes.DodgerBlue, Brushes.DodgerBlue, Brushes.LightBlue),
            "澶辫触" => (isSelected ? new SolidColorBrush(Color.Parse("#3A1616")) : Brushes.Transparent, Brushes.IndianRed, Brushes.IndianRed, Brushes.LightCoral),
            "寰呯户缁? => (isSelected ? new SolidColorBrush(Color.Parse("#3A2A12")) : Brushes.Transparent, Brushes.Orange, Brushes.Orange, Brushes.Wheat),
            _ => (isSelected ? new SolidColorBrush(Color.Parse("#1A2432")) : Brushes.Transparent, Brushes.Gray, Brushes.Gainsboro, Brushes.DarkGray)
        };
    }

    private void ApplyProjectMaterialStepLogs()
    {
        ProjectMaterialStepLogs.Clear();
        if (SelectedProject is null || SelectedProjectMaterialStep is null)
        {
            return;
        }

        var filtered = _allActivityLogs
            .Where(item => string.Equals(item.ProjectKey, SelectedProject.ProjectKey, StringComparison.Ordinal))
            .Where(item => string.Equals(item.StepKey, SelectedProjectMaterialStep.Key, StringComparison.Ordinal))
            .Take(200)
            .ToList();

        foreach (var item in filtered)
        {
            ProjectMaterialStepLogs.Add(item);
        }
    }

    private void ClearMaterialValidationIssues()
    {
        MaterialValidationIssues.Clear();
    }

    private int ParseSearchPageSize()
    {
        return int.TryParse(SearchPageSize, out var pageSize) && pageSize > 0
            ? pageSize
            : DefaultSearchPageSize;
    }

    private void UnsubscribeFromSearchResultRows()
    {
        foreach (var row in SearchResults)
        {
            row.CheckedChanged -= OnSearchResultRowCheckedChanged;
        }
    }

    private void OnSearchResultRowCheckedChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CheckedSearchResultsSummary));
        RefreshCommandStates();
    }

    private void OnProjectRowCheckedChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CheckedProjectsSummary));
        OnPropertyChanged(nameof(TaskQueueSummary));
        RefreshCommandStates();
    }

    private void OnInteractionRequestChanged(WorkflowInteractionRequest? request)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _currentInteractionRequest = request;
            HasInteractionRequest = request is not null;
            InteractionTitle = request is null
                ? "浜哄伐浠嬪叆"
                : $"浜哄伐浠嬪叆 路 {ResolveInteractionScopeLabel(request.Scope)}";
            InteractionMessage = request?.Message ?? string.Empty;
            if (request is not null)
            {
                StatusMessage = $"绛夊緟浜哄伐澶勭悊锛歿request.DisplayName}";
                AppendLog(
                    $"绛夊緟浜哄伐澶勭悊: {request.Message}",
                    request.ProjectKey,
                    request.DisplayName,
                    request.StepType,
                    ResolveStepLabel(request.StepType));
            }
            RefreshCommandStates();
        });
    }

    private void ClearLogsForProject(string projectKey)
    {
        _allActivityLogs.RemoveAll(entry => string.Equals(entry.ProjectKey, projectKey, StringComparison.Ordinal));
        ApplyActivityLogFilter();
    }

    private void ClearAllLogs()
    {
        _allActivityLogs.Clear();
        ApplyActivityLogFilter();
    }

    private async Task RefreshAfterExecutionAsync(string projectKey)
    {
        var checkedKeys = GetCheckedProjectKeys();
        var result = await _projectScanner.ScanAsync(RootDir, null, CancellationToken.None);
        TotalProjects = result.TotalProjects;
        PendingProjects = result.PendingProjects;
        BackupRootDir = result.BackupRootDir ?? string.Empty;

        ReplaceProjects(result.Projects, checkedKeys);
        LoadArchivedProjects();

        RefreshProjectLogFilters();

        SelectedProject = Projects.FirstOrDefault(item => string.Equals(item.ProjectKey, projectKey, StringComparison.Ordinal))
            ?? Projects.FirstOrDefault();
        StatusMessage = $"鍒锋柊瀹屾垚锛屽叡 {result.TotalProjects} 涓」鐩€?;
        RefreshSelectedProjectPreview();
    }

    private async Task RefreshProjectListAsync()
    {
        var selectedProjectKey = SelectedProject?.ProjectKey;
        var checkedKeys = GetCheckedProjectKeys();
        var result = await _projectScanner.ScanAsync(RootDir, null, CancellationToken.None);
        TotalProjects = result.TotalProjects;
        PendingProjects = result.PendingProjects;
        BackupRootDir = result.BackupRootDir ?? string.Empty;

        ReplaceProjects(result.Projects, checkedKeys);
        LoadArchivedProjects();

        RefreshProjectLogFilters();
        SelectedProject = selectedProjectKey is null
            ? Projects.FirstOrDefault()
            : Projects.FirstOrDefault(item => string.Equals(item.ProjectKey, selectedProjectKey, StringComparison.Ordinal))
                ?? Projects.FirstOrDefault();
        RefreshSelectedProjectPreview();
    }

    private void LoadArchivedProjects()
    {
        var selectedArchiveKey = SelectedArchivedProject?.ProjectKey;
        ArchivedProjects.Clear();

        if (string.IsNullOrWhiteSpace(RootDir))
        {
            SelectedArchivedProject = null;
            RefreshCommandStates();
            return;
        }

        var archiveRoot = Path.Combine(RootDir, "archive");
        if (!Directory.Exists(archiveRoot))
        {
            SelectedArchivedProject = null;
            RefreshCommandStates();
            return;
        }

        var items = new List<ArchivedProjectItem>();
        foreach (var archiveDir in Directory.EnumerateDirectories(archiveRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var metadataPath = Path.Combine(archiveDir, "archive-meta.json");
            if (!File.Exists(metadataPath))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
                var root = document.RootElement;
                var item = new ArchivedProjectItem(
                    ProjectKey: TryGetNonEmptyString(root, "ProjectKey") ?? Path.GetFileName(archiveDir),
                    DisplayName: TryGetNonEmptyString(root, "DisplayName") ?? Path.GetFileName(archiveDir),
                    ArchiveProjectDir: archiveDir,
                    ArchivedSourceDir: TryGetNonEmptyString(root, "archivedSourceDir") ?? Path.Combine(archiveDir, "source"),
                    ArchivedWorkflowDir: TryGetNonEmptyString(root, "archivedWorkflowDir") ?? Path.Combine(archiveDir, "workflow"),
                    ArchivedBackupDir: TryGetNonEmptyString(root, "archivedBackupDir") ?? Path.Combine(archiveDir, "backup"),
                    DeletedVideoFileCount: TryGetInt(root, "deletedVideoFileCount") ?? 0,
                    ArchivedAt: TryGetDateTimeOffset(root, "ArchivedAt"));
                items.Add(item);
            }
            catch
            {
                // Ignore malformed archive metadata files.
            }
        }

        foreach (var item in items.OrderByDescending(item => item.ArchivedAt))
        {
            ArchivedProjects.Add(item);
        }

        SelectedArchivedProject = selectedArchiveKey is null
            ? ArchivedProjects.FirstOrDefault()
            : ArchivedProjects.FirstOrDefault(item => string.Equals(item.ProjectKey, selectedArchiveKey, StringComparison.Ordinal))
                ?? ArchivedProjects.FirstOrDefault();
        RefreshCommandStates();
    }

    private HashSet<string> GetCheckedProjectKeys()
    {
        return Projects
            .Where(item => item.IsChecked)
            .Select(item => item.ProjectKey)
            .ToHashSet(StringComparer.Ordinal);
    }

    private void ReplaceProjects(IEnumerable<ScannedProject> scannedProjects, IReadOnlySet<string>? checkedKeys = null)
    {
        UnsubscribeFromProjectRows();
        Projects.Clear();

        foreach (var project in scannedProjects
                     .OrderByDescending(item => item.CreatedAt ?? DateTimeOffset.MinValue)
                     .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var item = new ProjectListItemViewModel(project)
            {
                IsChecked = checkedKeys?.Contains(project.ProjectKey) == true
            };
            item.CheckedChanged += OnProjectRowCheckedChanged;
            item.PropertyChanged += OnProjectRowStatusChanged;
            Projects.Add(item);
        }

        OnPropertyChanged(nameof(CheckedProjectsSummary));
        ApplyTaskQueueFilter();
        RefreshRunLogViewState();
        RefreshCommandStates();
    }

    private void UnsubscribeFromProjectRows()
    {
        foreach (var project in Projects)
        {
            project.CheckedChanged -= OnProjectRowCheckedChanged;
            project.PropertyChanged -= OnProjectRowStatusChanged;
        }
    }

    private void LoadConfig()
    {
        if (string.IsNullOrWhiteSpace(RootDir))
        {
            return;
        }

        var config = _configService.Load(RootDir);
        ApplyConfig(config);
        StatusMessage = $"宸插姞杞介厤缃細{config.ConfigFilePath}";
        ValidateConfig();
    }

    private void SaveConfig()
    {
        if (string.IsNullOrWhiteSpace(RootDir))
        {
            return;
        }

        var snapshot = BuildConfigSnapshot();
        _configService.Save(snapshot);
        SyncWeixinProjectConfigs(snapshot);
        ConfigFilePath = snapshot.ConfigFilePath;
        StatusMessage = $"閰嶇疆宸蹭繚瀛橈細{snapshot.ConfigFilePath}";
        AppendLog(StatusMessage);
        ValidateConfig();
    }

    private void ApplyConfig(DesktopConfigSnapshot config)
    {
        ConfigFilePath = config.ConfigFilePath;
        CompanyName = config.CompanyName;
        SearchPageSize = string.IsNullOrWhiteSpace(config.SearchPageSize) ? DefaultSearchPageSize.ToString() : config.SearchPageSize;
        TemplateDocxPath = config.TemplateDocxPath;
        _costReportBaseImagePath = config.CostReportBaseImagePath;
        _costReportActorPayRatio = config.CostReportActorPayRatio;
        _costReportLegalRepresentative = config.CostReportLegalRepresentative;
        ChatModelId = config.ChatModelId;
        ChatModelApiKey = config.ChatModelApiKey;
        ChatModelEndpoint = config.ChatModelEndpoint;
        AiTextEndpoint = string.IsNullOrWhiteSpace(config.AiTextEndpoint) ? config.ChatModelEndpoint : config.AiTextEndpoint;
        AiTextApiKey = string.IsNullOrWhiteSpace(config.AiTextApiKey) ? config.ChatModelApiKey : config.AiTextApiKey;
        AiTextModel = string.IsNullOrWhiteSpace(config.AiTextModel) ? config.ChatModelId : config.AiTextModel;
        AiTextTimeoutSeconds = string.IsNullOrWhiteSpace(config.AiTextTimeoutSeconds) ? "120" : config.AiTextTimeoutSeconds;
        AiTextMaxBatchSize = string.IsNullOrWhiteSpace(config.AiTextMaxBatchSize) ? "10" : config.AiTextMaxBatchSize;
        AiTextSystemPrompt = config.AiTextSystemPrompt;
        AiTextBatchPrompt = config.AiTextBatchPrompt;
        AiTextRetryPrompt = config.AiTextRetryPrompt;
        WeixinHeadless = config.WeixinHeadless;
        WeixinSlowMoMs = string.IsNullOrWhiteSpace(config.WeixinSlowMoMs) ? "50" : config.WeixinSlowMoMs;
        WeixinKeepOpenSeconds = string.IsNullOrWhiteSpace(config.WeixinKeepOpenSeconds) ? "0" : config.WeixinKeepOpenSeconds;
        WeixinLoginTimeoutSeconds = string.IsNullOrWhiteSpace(config.WeixinLoginTimeoutSeconds) ? "300" : config.WeixinLoginTimeoutSeconds;
        WeixinSubmitEnabled = config.WeixinSubmitEnabled;
        WeixinPauseOnError = config.WeixinPauseOnError;
        WeixinSaveHtml = config.WeixinSaveHtml;
        WeixinSaveText = config.WeixinSaveText;
        WeixinMonetizationType = string.IsNullOrWhiteSpace(config.WeixinMonetizationType) ? "IAA骞垮憡鍙樼幇" : config.WeixinMonetizationType;
        WeixinDramaType = string.IsNullOrWhiteSpace(config.WeixinDramaType) ? "婕墽" : config.WeixinDramaType;
        WeixinDramaQualification = string.IsNullOrWhiteSpace(config.WeixinDramaQualification) ? "鍏朵粬寰煭鍓? : config.WeixinDramaQualification;
        WeixinSubmitterIdentity = string.IsNullOrWhiteSpace(config.WeixinSubmitterIdentity) ? "鍓х洰鍒朵綔鏂? : config.WeixinSubmitterIdentity;
        WeixinTrialEpisodes = string.IsNullOrWhiteSpace(config.WeixinTrialEpisodes) ? "3" : config.WeixinTrialEpisodes;
        WeixinFillRecommendation = config.WeixinFillRecommendation;
        WeixinSubmissionReportDir = config.WeixinSubmissionReportDir;
        ImageModelId = config.ImageModelId;
        ImageModelApiKey = config.ImageModelApiKey;
        ImageModelEndpoint = config.ImageModelEndpoint;
        ImageEditModelId = config.ImageEditModelId;
        ImageEditApiKey = config.ImageEditApiKey;
        ImageEditEndpoint = config.ImageEditEndpoint;
        ImageEditPath = config.ImageEditPath;
        PosterLayoutDetectPrompt = config.PosterLayoutDetectPrompt;
        PosterInpaintPrompt = config.PosterInpaintPrompt;
        PosterInpaintSafeRetryPrompt = config.PosterInpaintSafeRetryPrompt;
        PosterGenerationPrompt = config.PosterGenerationPrompt;
        PosterGenerationSafeRetryPrompt = config.PosterGenerationSafeRetryPrompt;
        PosterNameSystemPrompt = config.PosterNameSystemPrompt;
        PosterNameUserPrompt = config.PosterNameUserPrompt;
        VideoRes = config.VideoRes;
        VideoBitrateBps = config.VideoBitrateBps;
        VideoBitrateMode = config.VideoBitrateMode;
        VideoAudioBitrateBps = config.VideoAudioBitrateBps;
        VideoFps = config.VideoFps;
        VideoConcurrentCount = string.IsNullOrWhiteSpace(config.VideoConcurrentCount) ? "1" : config.VideoConcurrentCount;
        VideoUseHardwareEncoder = config.VideoUseHardwareEncoder;
        VideoNameTemplate = config.VideoNameTemplate;
        MaterialConvertEnabled = config.MaterialConvertEnabled;
        MaterialTrimHeadSeconds = string.IsNullOrWhiteSpace(config.MaterialTrimHeadSeconds) ? "4" : config.MaterialTrimHeadSeconds;
        MaterialTrimTailSeconds = string.IsNullOrWhiteSpace(config.MaterialTrimTailSeconds) ? "2" : config.MaterialTrimTailSeconds;
        MaterialSpeedPercent = string.IsNullOrWhiteSpace(config.MaterialSpeedPercent) ? "10" : config.MaterialSpeedPercent;
        MaterialDropEveryNFrames = string.IsNullOrWhiteSpace(config.MaterialDropEveryNFrames) ? "20" : config.MaterialDropEveryNFrames;
        MaterialDropCount = string.IsNullOrWhiteSpace(config.MaterialDropCount) ? "1" : config.MaterialDropCount;
        MaterialCropWidthPercent = string.IsNullOrWhiteSpace(config.MaterialCropWidthPercent) ? "2" : config.MaterialCropWidthPercent;
        MaterialCropHeightPercent = string.IsNullOrWhiteSpace(config.MaterialCropHeightPercent) ? "2" : config.MaterialCropHeightPercent;
        ProjectImageCount = config.ProjectImageCount;
        ProjectImageTemplateDir = config.ProjectImageTemplateDir;
    }

    private void ValidateConfig()
    {
        ConfigIssues.Clear();

        if (string.IsNullOrWhiteSpace(RootDir))
        {
            AddConfigIssue("閿欒", "椤圭洰鏍圭洰褰曚负绌恒€?);
            FinalizeConfigValidation();
            return;
        }

        if (!Directory.Exists(RootDir))
        {
            AddConfigIssue("閿欒", $"椤圭洰鏍圭洰褰曚笉瀛樺湪: {RootDir}");
            FinalizeConfigValidation();
            return;
        }

        var configDir = DesktopConfigService.GetConfigDirectoryPath(RootDir);
        var configPath = DesktopConfigService.GetConfigFilePath(RootDir);

        if (!Directory.Exists(configDir))
        {
            AddConfigIssue("閿欒", $"缂哄皯 config 鐩綍: {configDir}");
        }

        if (!File.Exists(configPath))
        {
            AddConfigIssue("閿欒", $"缂哄皯閰嶇疆鏂囦欢: {configPath}");
        }

        var templatePath = ResolveConfigPath(TemplateDocxPath);
        if (string.IsNullOrWhiteSpace(templatePath))
        {
            AddConfigIssue("鎻愮ず", "鏈厤缃垚鏈姤琛ㄦā鏉匡紝灏嗗彧鐢熸垚 PNG锛屼笉淇濈暀鍘熷 docx銆?);
        }
        else if (!File.Exists(templatePath))
        {
            AddConfigIssue("璀﹀憡", $"鎴愭湰鎶ヨ〃妯℃澘涓嶅瓨鍦? {templatePath}锛汸NG 浠嶅彲鐢熸垚锛屼絾涓嶄細杈撳嚭鍘熷 docx銆?);
        }

        if (!string.IsNullOrWhiteSpace(_costReportBaseImagePath))
        {
            var baseImagePath = ResolveConfigPath(_costReportBaseImagePath);
            if (!File.Exists(baseImagePath))
            {
                AddConfigIssue("璀﹀憡", $"鎴愭湰鎶ヨ〃搴曞浘涓嶅瓨鍦? {baseImagePath}锛涘皢鍥為€€鍒板唴缃簳鍥俱€?);
            }
        }

        var projectImageTemplatePath = ResolveConfigPath(ProjectImageTemplateDir);
        if (string.IsNullOrWhiteSpace(projectImageTemplatePath))
        {
            AddConfigIssue("閿欒", "鏈厤缃伐绋嬪浘妯℃澘鐩綍锛屽伐绋嬪浘姝ラ鏃犳硶鎵ц銆?);
        }
        else if (!Directory.Exists(projectImageTemplatePath))
        {
            AddConfigIssue("閿欒", $"宸ョ▼鍥炬ā鏉跨洰褰曚笉瀛樺湪: {projectImageTemplatePath}");
        }
        else
        {
            ValidateProjectImageTemplates(projectImageTemplatePath);
        }

        var signPath = Path.Combine(configDir, "sign.png");
        var sealPath = Path.Combine(configDir, "seal.png");
        if (!File.Exists(signPath))
        {
            AddConfigIssue("閿欒", $"缂哄皯绛惧瓧鍥剧墖: {signPath}");
        }

        if (!File.Exists(sealPath))
        {
            AddConfigIssue("閿欒", $"缂哄皯鐩栫珷鍥剧墖: {sealPath}");
        }

        if (string.IsNullOrWhiteSpace(CompanyName))
        {
            AddConfigIssue("璀﹀憡", "CompanyName 涓虹┖锛岄儴鍒嗙敓鎴愬唴瀹逛細缂哄皯鍏徃鍚嶇О銆?);
        }

        if (!int.TryParse(SearchPageSize, out var searchPageSize) || searchPageSize <= 0)
        {
            AddConfigIssue("閿欒", "SearchPageSize 蹇呴』鏄ぇ浜?0 鐨勬暣鏁般€?);
        }

        if (string.IsNullOrWhiteSpace(ChatModelId))
        {
            AddConfigIssue("閿欒", "鏈厤缃?ChatModelId锛屼豢鍐欏墽鍚嶇畝浠嬫棤娉曟墽琛屻€?);
        }

        if (string.IsNullOrWhiteSpace(ChatModelApiKey))
        {
            AddConfigIssue("閿欒", "鏈厤缃?ChatModelApiKey銆?);
        }

        if (string.IsNullOrWhiteSpace(ChatModelEndpoint))
        {
            AddConfigIssue("璀﹀憡", "鏈厤缃?ChatModelEndpoint锛屽彲鑳芥棤娉曡闂枃鏈ā鍨嬨€?);
        }

        if (string.IsNullOrWhiteSpace(AiTextEndpoint))
        {
            AddConfigIssue("璀﹀憡", "鏈厤缃?AiTextEndpoint锛屽皢鍥為€€鍒版枃鏈ā鍨嬪湴鍧€銆?);
        }

        if (string.IsNullOrWhiteSpace(AiTextApiKey))
        {
            AddConfigIssue("璀﹀憡", "鏈厤缃?AiTextApiKey锛屽皢鍥為€€鍒版枃鏈ā鍨嬪瘑閽ャ€?);
        }

        if (string.IsNullOrWhiteSpace(AiTextModel))
        {
            AddConfigIssue("璀﹀憡", "鏈厤缃?AiTextModel锛屽皢鍥為€€鍒版枃鏈ā鍨?ID銆?);
        }

        if (!int.TryParse(AiTextTimeoutSeconds, out var aiTextTimeout) || aiTextTimeout <= 0)
        {
            AddConfigIssue("閿欒", "AiTextTimeoutSeconds 蹇呴』鏄ぇ浜?0 鐨勬暣鏁般€?);
        }

        if (!int.TryParse(AiTextMaxBatchSize, out var aiTextBatchSize) || aiTextBatchSize <= 0)
        {
            AddConfigIssue("閿欒", "AiTextMaxBatchSize 蹇呴』鏄ぇ浜?0 鐨勬暣鏁般€?);
        }

        if (string.IsNullOrWhiteSpace(AiTextBatchPrompt))
        {
            AddConfigIssue("璀﹀憡", "AiTextBatchPrompt 涓虹┖锛屽皢浣跨敤鍐呯疆榛樿鎻愮ず璇嶃€?);
        }

        if (string.IsNullOrWhiteSpace(AiTextRetryPrompt))
        {
            AddConfigIssue("璀﹀憡", "AiTextRetryPrompt 涓虹┖锛屽皢浣跨敤鍐呯疆榛樿绾犲亸鎻愮ず璇嶃€?);
        }

        if (!int.TryParse(WeixinSlowMoMs, out var weixinSlowMoValue) || weixinSlowMoValue < 0)
        {
            AddConfigIssue("閿欒", "WeixinSlowMoMs 蹇呴』鏄ぇ浜庣瓑浜?0 鐨勬暣鏁般€?);
        }

        if (!int.TryParse(WeixinKeepOpenSeconds, out var weixinKeepOpenValue) || weixinKeepOpenValue < 0)
        {
            AddConfigIssue("閿欒", "WeixinKeepOpenSeconds 蹇呴』鏄ぇ浜庣瓑浜?0 鐨勬暣鏁般€?);
        }

        if (!int.TryParse(WeixinLoginTimeoutSeconds, out var weixinLoginTimeoutValue) || weixinLoginTimeoutValue <= 0)
        {
            AddConfigIssue("閿欒", "WeixinLoginTimeoutSeconds 蹇呴』鏄ぇ浜?0 鐨勬暣鏁般€?);
        }

        if (string.IsNullOrWhiteSpace(WeixinMonetizationType))
        {
            AddConfigIssue("璀﹀憡", "WeixinMonetizationType 涓虹┖锛屽皢鍥為€€鍒伴粯璁ゅ€?IAA骞垮憡鍙樼幇銆?);
        }

        if (string.IsNullOrWhiteSpace(WeixinDramaType))
        {
            AddConfigIssue("璀﹀憡", "WeixinDramaType 涓虹┖锛屽皢鍥為€€鍒伴粯璁ゅ€?婕墽銆?);
        }

        if (string.IsNullOrWhiteSpace(WeixinDramaQualification))
        {
            AddConfigIssue("璀﹀憡", "WeixinDramaQualification 涓虹┖锛屽皢鍥為€€鍒伴粯璁ゅ€?鍏朵粬寰煭鍓с€?);
        }

        if (string.IsNullOrWhiteSpace(WeixinSubmitterIdentity))
        {
            AddConfigIssue("璀﹀憡", "WeixinSubmitterIdentity 涓虹┖锛屽皢鍥為€€鍒伴粯璁ゅ€?鍓х洰鍒朵綔鏂广€?);
        }

        if (!int.TryParse(WeixinTrialEpisodes, out var weixinTrialEpisodesValue) || weixinTrialEpisodesValue <= 0)
        {
            AddConfigIssue("閿欒", "WeixinTrialEpisodes 蹇呴』鏄ぇ浜?0 鐨勬暣鏁般€?);
        }

        var submissionReportDir = ResolveConfigPath(WeixinSubmissionReportDir);
        if (!string.IsNullOrWhiteSpace(submissionReportDir) && !Directory.Exists(submissionReportDir))
        {
            AddConfigIssue("閿欒", $"WeixinSubmissionReportDir 涓嶅瓨鍦? {submissionReportDir}");
        }

        if (string.IsNullOrWhiteSpace(ImageModelId))
        {
            AddConfigIssue("閿欒", "鏈厤缃?ImageModelId锛屾捣鎶ョ敓鎴愭棤娉曟墽琛屻€?);
        }

        if (string.IsNullOrWhiteSpace(ImageModelApiKey))
        {
            AddConfigIssue("閿欒", "鏈厤缃?ImageModelApiKey銆?);
        }

        if (string.IsNullOrWhiteSpace(ImageModelEndpoint))
        {
            AddConfigIssue("璀﹀憡", "鏈厤缃?ImageModelEndpoint锛屽浘鐗囨ā鍨嬭姹傚彲鑳藉け璐ャ€?);
        }

        if (string.IsNullOrWhiteSpace(PosterLayoutDetectPrompt))
        {
            AddConfigIssue("璀﹀憡", "PosterLayoutDetectPrompt 涓虹┖锛屽皢浣跨敤鍐呯疆娴锋姤甯冨眬妫€娴嬫彁绀鸿瘝銆?);
        }

        if (string.IsNullOrWhiteSpace(PosterNameUserPrompt))
        {
            AddConfigIssue("璀﹀憡", "PosterNameUserPrompt 涓虹┖锛屽皢浣跨敤鍐呯疆娴锋姤鍚嶆彁绀鸿瘝銆?);
        }

        if (!int.TryParse(ProjectImageCount, out var projectImageCount) || projectImageCount <= 0)
        {
            AddConfigIssue("閿欒", "ProjectImageCount 蹇呴』鏄ぇ浜?0 鐨勬暣鏁般€?);
        }

        if (!int.TryParse(VideoBitrateBps, out var videoBitrate) || videoBitrate <= 0)
        {
            AddConfigIssue("閿欒", "VideoBitrateBps 蹇呴』鏄湁鏁堟暣鏁般€?);
        }
        else if (videoBitrate < MinimumUploadVideoBitrate)
        {
            AddConfigIssue("璀﹀憡", $"VideoBitrateBps 褰撳墠浣庝簬瑙嗛鍙峰缓璁槇鍊?{MinimumUploadVideoBitrate}銆?);
        }

        if (!int.TryParse(VideoAudioBitrateBps, out var audioBitrate) || audioBitrate <= 0)
        {
            AddConfigIssue("璀﹀憡", "VideoAudioBitrateBps 涓虹┖鎴栨棤鏁堬紝灏嗕娇鐢ㄩ粯璁ら煶棰戠爜鐜囥€?);
        }

        if (!int.TryParse(VideoFps, out var fps) || fps <= 0)
        {
            AddConfigIssue("璀﹀憡", "VideoFps 涓虹┖鎴栨棤鏁堬紝灏嗕娇鐢ㄩ粯璁ゅ抚鐜囥€?);
        }

        if (!int.TryParse(VideoConcurrentCount, out var concurrentCount) || concurrentCount <= 0)
        {
            AddConfigIssue("閿欒", "VideoConcurrentCount 蹇呴』鏄ぇ浜?0 鐨勬暣鏁般€?);
        }
        else if (concurrentCount > 4)
        {
            AddConfigIssue("璀﹀憡", "VideoConcurrentCount 褰撳墠澶т簬 4锛屽彲鑳藉鑷存満鍣ㄨ礋杞借繃楂樸€?);
        }

        if (string.IsNullOrWhiteSpace(VideoRes))
        {
            AddConfigIssue("璀﹀憡", "VideoRes 涓虹┖锛屽皢浣跨敤榛樿鍒嗚鲸鐜囥€?);
        }

        if (string.IsNullOrWhiteSpace(VideoBitrateMode))
        {
            AddConfigIssue("璀﹀憡", "VideoBitrateMode 涓虹┖锛屽皢浣跨敤榛樿鐮佺巼妯″紡銆?);
        }

        if (!double.TryParse(MaterialTrimHeadSeconds, out var materialTrimHead) || materialTrimHead < 0)
        {
            AddConfigIssue("閿欒", "MaterialTrimHeadSeconds 蹇呴』鏄ぇ浜庣瓑浜?0 鐨勬暟瀛椼€?);
        }

        if (!double.TryParse(MaterialTrimTailSeconds, out var materialTrimTail) || materialTrimTail < 0)
        {
            AddConfigIssue("閿欒", "MaterialTrimTailSeconds 蹇呴』鏄ぇ浜庣瓑浜?0 鐨勬暟瀛椼€?);
        }

        if (!double.TryParse(MaterialSpeedPercent, out var materialSpeed) || materialSpeed < -50 || materialSpeed > 50)
        {
            AddConfigIssue("閿欒", "MaterialSpeedPercent 蹇呴』鍦?-50 鍒?50 涔嬮棿銆?);
        }

        if (!int.TryParse(MaterialDropEveryNFrames, out var materialDropEvery) || materialDropEvery <= 1)
        {
            AddConfigIssue("閿欒", "MaterialDropEveryNFrames 蹇呴』鏄ぇ浜?1 鐨勬暣鏁般€?);
        }

        if (!int.TryParse(MaterialDropCount, out var materialDropCount) || materialDropCount <= 0)
        {
            AddConfigIssue("閿欒", "MaterialDropCount 蹇呴』鏄ぇ浜?0 鐨勬暣鏁般€?);
        }
        else if (int.TryParse(MaterialDropEveryNFrames, out materialDropEvery) && materialDropCount >= materialDropEvery)
        {
            AddConfigIssue("閿欒", "MaterialDropCount 蹇呴』灏忎簬 MaterialDropEveryNFrames銆?);
        }

        if (!double.TryParse(MaterialCropWidthPercent, out var materialCropWidth) || materialCropWidth < 0 || materialCropWidth >= 100)
        {
            AddConfigIssue("閿欒", "MaterialCropWidthPercent 蹇呴』鍦?0 鍒?100 涔嬮棿銆?);
        }

        if (!double.TryParse(MaterialCropHeightPercent, out var materialCropHeight) || materialCropHeight < 0 || materialCropHeight >= 100)
        {
            AddConfigIssue("閿欒", "MaterialCropHeightPercent 蹇呴』鍦?0 鍒?100 涔嬮棿銆?);
        }

        FinalizeConfigValidation();
    }

    private DesktopConfigSnapshot BuildConfigSnapshot()
    {
        return new DesktopConfigSnapshot(
            ConfigFilePath: DesktopConfigService.GetConfigFilePath(RootDir),
            CompanyName: CompanyName,
            SearchPageSize: SearchPageSize,
            TemplateDocxPath: TemplateDocxPath,
            CostReportBaseImagePath: _costReportBaseImagePath,
            CostReportActorPayRatio: _costReportActorPayRatio,
            CostReportLegalRepresentative: _costReportLegalRepresentative,
            ChatModelId: ChatModelId,
            ChatModelApiKey: ChatModelApiKey,
            ChatModelEndpoint: ChatModelEndpoint,
            AiTextEndpoint: AiTextEndpoint,
            AiTextApiKey: AiTextApiKey,
            AiTextModel: AiTextModel,
            AiTextTimeoutSeconds: AiTextTimeoutSeconds,
            AiTextMaxBatchSize: AiTextMaxBatchSize,
            AiTextSystemPrompt: AiTextSystemPrompt,
            AiTextBatchPrompt: AiTextBatchPrompt,
            AiTextRetryPrompt: AiTextRetryPrompt,
            WeixinHeadless: WeixinHeadless,
            WeixinSlowMoMs: WeixinSlowMoMs,
            WeixinKeepOpenSeconds: WeixinKeepOpenSeconds,
            WeixinLoginTimeoutSeconds: WeixinLoginTimeoutSeconds,
            WeixinSubmitEnabled: WeixinSubmitEnabled,
            WeixinPauseOnError: WeixinPauseOnError,
            WeixinSaveHtml: WeixinSaveHtml,
            WeixinSaveText: WeixinSaveText,
            WeixinMonetizationType: WeixinMonetizationType,
            WeixinDramaType: WeixinDramaType,
            WeixinDramaQualification: WeixinDramaQualification,
            WeixinSubmitterIdentity: WeixinSubmitterIdentity,
            WeixinTrialEpisodes: WeixinTrialEpisodes,
            WeixinFillRecommendation: WeixinFillRecommendation,
            WeixinSubmissionReportDir: WeixinSubmissionReportDir,
            ImageModelId: ImageModelId,
            ImageModelApiKey: ImageModelApiKey,
            ImageModelEndpoint: ImageModelEndpoint,
            ImageEditModelId: ImageEditModelId,
            ImageEditApiKey: ImageEditApiKey,
            ImageEditEndpoint: ImageEditEndpoint,
            ImageEditPath: ImageEditPath,
            PosterLayoutDetectPrompt: PosterLayoutDetectPrompt,
            PosterInpaintPrompt: PosterInpaintPrompt,
            PosterInpaintSafeRetryPrompt: PosterInpaintSafeRetryPrompt,
            PosterGenerationPrompt: PosterGenerationPrompt,
            PosterGenerationSafeRetryPrompt: PosterGenerationSafeRetryPrompt,
            PosterNameSystemPrompt: PosterNameSystemPrompt,
            PosterNameUserPrompt: PosterNameUserPrompt,
            VideoRes: VideoRes,
            VideoBitrateBps: VideoBitrateBps,
            VideoBitrateMode: VideoBitrateMode,
            VideoAudioBitrateBps: VideoAudioBitrateBps,
            VideoFps: VideoFps,
            VideoConcurrentCount: VideoConcurrentCount,
            VideoUseHardwareEncoder: VideoUseHardwareEncoder,
            VideoNameTemplate: VideoNameTemplate,
            MaterialConvertEnabled: MaterialConvertEnabled,
            MaterialTrimHeadSeconds: MaterialTrimHeadSeconds,
            MaterialTrimTailSeconds: MaterialTrimTailSeconds,
            MaterialSpeedPercent: MaterialSpeedPercent,
            MaterialDropEveryNFrames: MaterialDropEveryNFrames,
            MaterialDropCount: MaterialDropCount,
            MaterialCropWidthPercent: MaterialCropWidthPercent,
            MaterialCropHeightPercent: MaterialCropHeightPercent,
            ProjectImageCount: ProjectImageCount,
            ProjectImageTemplateDir: ProjectImageTemplateDir);
    }

    private void SyncWeixinProjectConfigs(DesktopConfigSnapshot snapshot)
    {
        var updatedCount = 0;
        foreach (var project in Projects)
        {
            var workflowDir = project.WorkflowProjectDir;
            if (string.IsNullOrWhiteSpace(workflowDir) || !Directory.Exists(workflowDir))
            {
                continue;
            }

            foreach (var configPath in EnumerateWeixinProjectConfigPaths(workflowDir))
            {
                if (UpdateWeixinProjectConfig(configPath, snapshot))
                {
                    updatedCount++;
                }
            }
        }

        if (updatedCount > 0)
        {
            AppendLog($"宸插悓姝ユ洿鏂?{updatedCount} 涓井淇′笂浼犻厤缃枃浠躲€?);
        }
    }

    private static IEnumerable<string> EnumerateWeixinProjectConfigPaths(string workflowDir)
    {
        foreach (var fileName in new[]
                 {
                     "weixin-channel-autogen.json",
                     "weixin-channel-submit.json",
                     "weixin-channel-config.json"
                 })
        {
            var path = Path.Combine(workflowDir, fileName);
            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    private static bool UpdateWeixinProjectConfig(string configPath, DesktopConfigSnapshot snapshot)
    {
        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(File.ReadAllText(configPath));
        }
        catch
        {
            return false;
        }

        if (rootNode is not JsonObject root)
        {
            return false;
        }

        var browser = EnsureObject(root, "browser");
        browser["headless"] = snapshot.WeixinHeadless;
        browser["slow_mo_ms"] = ParseIntOrDefault(snapshot.WeixinSlowMoMs, 50);
        browser["keep_open_seconds"] = ParseIntOrDefault(snapshot.WeixinKeepOpenSeconds, 0);

        var login = EnsureObject(root, "login");
        login["timeout_seconds"] = ParseIntOrDefault(snapshot.WeixinLoginTimeoutSeconds, 300);

        root["pause_on_error"] = snapshot.WeixinPauseOnError;

        var debug = EnsureObject(root, "debug");
        debug["save_html"] = snapshot.WeixinSaveHtml;
        debug["save_text"] = snapshot.WeixinSaveText;

        var submit = EnsureObject(root, "submit");
        submit["enabled"] = snapshot.WeixinSubmitEnabled;

        var firstPage = EnsureObject(root, "first_page");
        if (firstPage["actions"] is JsonArray actions)
        {
            UpdateFirstPageActions(actions, snapshot);
        }

        File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    private static void UpdateFirstPageActions(JsonArray actions, DesktopConfigSnapshot snapshot)
    {
        for (var i = actions.Count - 1; i >= 0; i--)
        {
            if (actions[i] is not JsonObject action)
            {
                continue;
            }

            var type = action["type"]?.GetValue<string>()?.Trim();
            var label = action["label"]?.GetValue<string>()?.Trim();
            var fieldLabel = action["field_label"]?.GetValue<string>()?.Trim();

            if (string.Equals(type, "fill", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(label, "鎺ㄨ崘璇?, StringComparison.Ordinal) &&
                !snapshot.WeixinFillRecommendation)
            {
                actions.RemoveAt(i);
                continue;
            }

            if (string.Equals(type, "choose", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(fieldLabel, "鍙樼幇绫诲瀷", StringComparison.Ordinal))
                {
                    action["option_text"] = snapshot.WeixinMonetizationType;
                }
                else if (string.Equals(fieldLabel, "鍓х洰绫诲瀷", StringComparison.Ordinal))
                {
                    action["option_text"] = snapshot.WeixinDramaType;
                }
                else if (string.Equals(fieldLabel, "鍓х洰璧勮川", StringComparison.Ordinal))
                {
                    action["option_text"] = snapshot.WeixinDramaQualification;
                }
                else if (string.Equals(fieldLabel, "鎻愬韬唤", StringComparison.Ordinal))
                {
                    action["option_text"] = snapshot.WeixinSubmitterIdentity;
                }
            }
            else if (string.Equals(type, "fill", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(label, "璇曠湅闆嗘暟", StringComparison.Ordinal))
            {
                action["value"] = ParseIntOrDefault(snapshot.WeixinTrialEpisodes, 3).ToString();
            }
        }
    }

    private static JsonObject EnsureObject(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        root[propertyName] = created;
        return created;
    }

    private static int ParseIntOrDefault(string value, int fallback)
    {
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private void RefreshSelectedProjectPreview()
    {
        ReplacePosterPreview(FindPreviewPath("娴锋姤鍥剧墖.jpg"));
        ReplaceCostPreview(FindPreviewPath("鎴愭湰鎶ヨ〃.png"));
        ReplaceProjectImagePreview(FindProjectImagePreviewPath());
        RefreshCommandStates();
    }

    private string? FindPreviewPath(string fileName)
    {
        var baseDir = SelectedProject?.WorkflowProjectDir;
        if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir))
        {
            return null;
        }

        var candidate = Path.Combine(baseDir, fileName);
        return File.Exists(candidate) ? candidate : null;
    }

    private string? FindProjectImagePreviewPath()
    {
        var baseDir = SelectedProject?.WorkflowProjectDir;
        if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir))
        {
            return null;
        }

        return Directory.EnumerateFiles(baseDir, "宸ョ▼鍥綺*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private void ReplacePosterPreview(string? path)
    {
        PosterPreviewBitmap?.Dispose();
        PosterPreviewBitmap = null;
        PosterPreviewPath = path ?? string.Empty;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            PosterPreviewBitmap = new Bitmap(path);
        }
        catch
        {
            PosterPreviewBitmap = null;
        }
    }

    private void ReplaceCostPreview(string? path)
    {
        CostPreviewBitmap?.Dispose();
        CostPreviewBitmap = null;
        CostPreviewPath = path ?? string.Empty;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            CostPreviewBitmap = new Bitmap(path);
        }
        catch
        {
            CostPreviewBitmap = null;
        }
    }

    private void ReplaceProjectImagePreview(string? path)
    {
        ProjectImagePreviewBitmap?.Dispose();
        ProjectImagePreviewBitmap = null;
        ProjectImagePreviewPath = path ?? string.Empty;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            ProjectImagePreviewBitmap = new Bitmap(path);
        }
        catch
        {
            ProjectImagePreviewBitmap = null;
        }
    }

    private void AppendLog(string message)
    {
        AppendLog(message, string.Empty, string.Empty, string.Empty, string.Empty, isFailure: false);
    }

    private void AddConfigIssue(string severity, string message)
    {
        ConfigIssues.Add(new ConfigValidationItem(severity, message));
    }

    private void FinalizeConfigValidation()
    {
        var errorCount = ConfigIssues.Count(item => string.Equals(item.Severity, "閿欒", StringComparison.Ordinal));
        var warningCount = ConfigIssues.Count(item => string.Equals(item.Severity, "璀﹀憡", StringComparison.Ordinal));

        ConfigValidationSummary = errorCount == 0 && warningCount == 0
            ? "閰嶇疆鏍￠獙閫氳繃"
            : $"閰嶇疆鏍￠獙锛氶敊璇?{errorCount} 椤癸紝璀﹀憡 {warningCount} 椤?;
    }

    private void ValidateProjectImageTemplates(string templateDir)
    {
        if (!int.TryParse(ProjectImageCount, out var count) || count <= 0)
        {
            return;
        }

        for (var index = 1; index <= count; index++)
        {
            var templatePath = Path.Combine(templateDir, $"宸ョ▼鍥綺{index}.png");
            if (!File.Exists(templatePath))
            {
                AddConfigIssue("閿欒", $"缂哄皯宸ョ▼鍥炬ā鏉? {templatePath}");
            }
        }
    }

    private string ResolveConfigPath(string configuredPath)
    {
        return string.IsNullOrWhiteSpace(configuredPath)
            ? string.Empty
            : DesktopConfigService.ResolveConfiguredPath(DesktopConfigService.GetConfigDirectoryPath(RootDir), configuredPath);
    }

    private void OpenSourceDir() => OpenPath(SelectedProject?.SourceProjectDir);

    private void OpenWorkflowDir() => OpenPath(SelectedProject?.WorkflowProjectDir);

    public void OpenProjectFolder(ProjectListItemViewModel? project)
    {
        if (project is null)
        {
            return;
        }

        var targetPath = !string.IsNullOrWhiteSpace(project.WorkflowProjectDir)
            ? project.WorkflowProjectDir
            : project.SourceProjectDir;
        OpenPath(targetPath);
    }

    public void OpenProjectSourceFolder(ProjectListItemViewModel? project)
    {
        if (project is null)
        {
            return;
        }

        OpenPath(project.SourceProjectDir);
    }

    public void OpenProjectWorkflowFolder(ProjectListItemViewModel? project)
    {
        if (project is null)
        {
            return;
        }

        OpenPath(!string.IsNullOrWhiteSpace(project.WorkflowProjectDir)
            ? project.WorkflowProjectDir
            : project.SourceProjectDir);
    }

    private void OpenArchivedProjectDir() => OpenPath(SelectedArchivedProject?.ArchiveProjectDir);

    private void OpenArchivedSourceDir() => OpenPath(SelectedArchivedProject?.ArchivedSourceDir);

    private void OpenArchivedWorkflowDir() => OpenPath(SelectedArchivedProject?.ArchivedWorkflowDir);

    public void OpenArchivedProjectDir(ArchivedProjectItem? item) => OpenPath(item?.ArchiveProjectDir);

    public void OpenArchivedSourceDir(ArchivedProjectItem? item) => OpenPath(item?.ArchivedSourceDir);

    public void OpenArchivedWorkflowDir(ArchivedProjectItem? item) => OpenPath(item?.ArchivedWorkflowDir);

    public async Task DeleteSelectedArchivedProjectAsync()
    {
        if (SelectedArchivedProject is null)
        {
            return;
        }

        await DeleteArchivedProjectAsync(SelectedArchivedProject);
    }

    public async Task DeleteArchivedProjectAsync(ArchivedProjectItem? item)
    {
        if (item is null)
        {
            return;
        }

        var target = item;
        await RunSearchBusyAsync($"姝ｅ湪鍒犻櫎褰掓。椤圭洰锛歿target.DisplayName}", async cancellationToken =>
        {
            var result = await _archivedProjectDeleteService.DeleteAsync(
                RootDir,
                target.ArchiveProjectDir,
                cancellationToken);

            AppendLog(result.Message, target.ProjectKey, target.DisplayName, "archive-delete", "鍒犻櫎褰掓。椤圭洰");
            LoadArchivedProjects();
            StatusMessage = result.Message;
        });
    }

    public async Task DeleteCheckedArchivedProjectsAsync()
    {
        var targets = ArchivedProjects.Where(item => item.IsChecked).ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        await RunSearchBusyAsync($"姝ｅ湪鍒犻櫎鍕鹃€夊綊妗ｉ」鐩紝鍏?{targets.Length} 涓?..", async cancellationToken =>
        {
            var deleted = 0;
            foreach (var target in targets)
            {
                var result = await _archivedProjectDeleteService.DeleteAsync(
                    RootDir,
                    target.ArchiveProjectDir,
                    cancellationToken);
                deleted++;
                AppendLog(result.Message, target.ProjectKey, target.DisplayName, "archive-delete", "鍒犻櫎褰掓。椤圭洰");
            }

            LoadArchivedProjects();
            StatusMessage = $"宸插垹闄ゅ綊妗ｉ」鐩?{deleted} 涓€?;
            AppendLog(StatusMessage);
        });
    }

    private void OpenConfigFile() => RevealPath(ConfigFilePath);

    private void OpenPoster() => OpenPath(PosterPreviewPath);

    private void OpenCostReport() => OpenPath(CostPreviewPath);

    private void OpenProjectImage() => OpenPath(ProjectImagePreviewPath);

    private void OpenPath(string? path)
    {
        if (!_shellService.TryOpenPath(path ?? string.Empty, out var message))
        {
            AppendLog(message);
            StatusMessage = message;
            return;
        }

        AppendLog(message);
    }

    private void RevealPath(string? path)
    {
        if (!_shellService.TryRevealPath(path ?? string.Empty, out var message))
        {
            AppendLog(message);
            StatusMessage = message;
            return;
        }

        AppendLog(message);
    }

    private void AppendLog(
        string message,
        string projectKey,
        string projectLabel,
        string stepKey,
        string stepLabel,
        bool isFailure = false)
    {
        var timestamp = DateTime.Now;
        _allActivityLogs.Insert(0, new ActivityLogEntry(
            timestamp.ToString("HH:mm:ss"),
            message,
            projectKey,
            projectLabel,
            stepKey,
            stepLabel,
            BuildLogDisplayText(timestamp, message, projectLabel, stepLabel),
            isFailure));

        while (_allActivityLogs.Count > 500)
        {
            _allActivityLogs.RemoveAt(_allActivityLogs.Count - 1);
        }

        HandleRunLogActivityAppended(projectKey);
        ApplyActivityLogFilter();
    }

    private void ApplyActivityLogFilter()
    {
        var projectKey = SelectedProjectLogFilter?.Key ?? AllProjectsFilterKey;
        var stepKey = SelectedStepLogFilter?.Key ?? AllStepsFilterKey;

        var filtered = _allActivityLogs
            .Where(item => MatchesProjectFilter(item, projectKey))
            .Where(item => MatchesStepFilter(item, stepKey))
            .Where(item => !OnlyShowFailedLogs || item.IsFailure)
            .Take(200)
            .ToList();

        ActivityLog.Clear();
        foreach (var item in filtered)
        {
            ActivityLog.Add(item);
        }

        RefreshRunLogViewState();
    }

    private void RefreshProjectLogFilters()
    {
        var selectedKey = SelectedProjectLogFilter?.Key ?? AllProjectsFilterKey;
        ProjectLogFilters.Clear();
        ProjectLogFilters.Add(new LogFilterOption(AllProjectsFilterKey, "鍏ㄩ儴椤圭洰"));

        foreach (var project in Projects)
        {
            ProjectLogFilters.Add(new LogFilterOption(project.ProjectKey, project.DisplayName));
        }

        SelectedProjectLogFilter = ProjectLogFilters.FirstOrDefault(item => string.Equals(item.Key, selectedKey, StringComparison.Ordinal))
            ?? ProjectLogFilters.First();
    }

    private void SyncProjectLogFilterToSelection()
    {
        if (SelectedProject is null)
        {
            return;
        }

        SelectedProjectLogFilter = ProjectLogFilters.FirstOrDefault(item =>
            string.Equals(item.Key, SelectedProject.ProjectKey, StringComparison.Ordinal))
            ?? SelectedProjectLogFilter;
    }

    private void SyncStepLogFilterToSelection()
    {
        if (SelectedStepOption is null)
        {
            return;
        }

        SelectedStepLogFilter = StepLogFilters.FirstOrDefault(item =>
            string.Equals(item.Key, SelectedStepOption.Key, StringComparison.Ordinal))
            ?? SelectedStepLogFilter;
    }

    private string ResolveStepLabel(string? stepKey)
    {
        if (string.IsNullOrWhiteSpace(stepKey))
        {
            return string.Empty;
        }

        return StepOptions.FirstOrDefault(item => string.Equals(item.Key, stepKey, StringComparison.Ordinal))?.Label
            ?? stepKey;
    }

    private static string ResolveInteractionScopeLabel(string? scope)
    {
        return scope switch
        {
            "publish_video" => "褰撳墠绱犳潗",
            "project" => "褰撳墠椤圭洰",
            _ => "褰撳墠鑺傜偣"
        };
    }

    private static bool MatchesProjectFilter(ActivityLogEntry item, string filterKey)
    {
        return string.Equals(filterKey, AllProjectsFilterKey, StringComparison.Ordinal) ||
            string.Equals(item.ProjectKey, filterKey, StringComparison.Ordinal);
    }

    private static bool MatchesStepFilter(ActivityLogEntry item, string filterKey)
    {
        return filterKey switch
        {
            AllStepsFilterKey => true,
            SystemStepFilterKey => string.IsNullOrWhiteSpace(item.StepKey),
            _ => string.Equals(item.StepKey, filterKey, StringComparison.Ordinal)
        };
    }

    private static string BuildLogDisplayText(DateTime timestamp, string message, string projectLabel, string stepLabel)
    {
        var parts = new List<string> { timestamp.ToString("HH:mm:ss") };
        if (!string.IsNullOrWhiteSpace(projectLabel))
        {
            parts.Add($"[{projectLabel}]");
        }

        if (!string.IsNullOrWhiteSpace(stepLabel))
        {
            parts.Add(stepLabel);
        }

        parts.Add(message);
        return string.Join(' ', parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static (string? BookId, string Quality, int Concurrent) ReadDownloadMetadata(string sourceProjectDir)
    {
        var metadataPath = Path.Combine(sourceProjectDir, "shortdrama-project.json");
        if (!File.Exists(metadataPath))
        {
            return (null, "1080P+", 3);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var root = document.RootElement;
            return (
                TryGetNonEmptyString(root, "bookId") ?? TryGetNonEmptyString(root, "book_id"),
                TryGetNonEmptyString(root, "quality") ?? "1080P+",
                GetIntOrDefault(root, "concurrent", 3));
        }
        catch
        {
            return (null, "1080P+", 3);
        }
    }

    private static int GetIntOrDefault(JsonElement root, string propertyName, int defaultValue)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out intValue))
        {
            return intValue;
        }

        return defaultValue;
    }

    private static void DeleteDownloadedEpisodeFiles(string directory, int episodeNumber)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(path => DownloadVideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
        {
            if (TryExtractDownloadedEpisodeNumber(Path.GetFileNameWithoutExtension(path)) == episodeNumber)
            {
                File.Delete(path);
            }
        }
    }

    private static int? TryExtractDownloadedEpisodeNumber(string fileName)
    {
        var match = DownloadEpisodeNameRegex.Match(fileName);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var episodeFromName))
        {
            return episodeFromName;
        }

        match = DownloadTrailingNumberRegex.Match(fileName);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var trailingEpisode))
        {
            return trailingEpisode;
        }

        return null;
    }
}
