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
        ("transcode", "视频转码"),
        ("rewrite", "仿写剧名简介"),
        ("poster-rename", "生成海报图片"),
        ("project-image", "生成工程图"),
        ("cost-report", "生成成本报表"),
        ("batch-file-rename", "重命名视频文件"),
        ("material-convert", "转换素材视频")
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
    private readonly DesktopDependencyInspector _dependencyInspector;
    private readonly DesktopShellService _shellService;
    private readonly IWorkflowInteractionService _interactionService;
    private readonly List<ActivityLogEntry> _allActivityLogs = [];
    private static readonly string[] DownloadVideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];
    private static readonly Regex DownloadEpisodeNameRegex = new(@"第\s*0*(\d+)\s*集", RegexOptions.Compiled);
    private static readonly Regex DownloadTrailingNumberRegex = new(@"(\d+)(?!.*\d)", RegexOptions.Compiled);
    private CancellationTokenSource? _currentOperationCts;
    private WorkflowInteractionRequest? _currentInteractionRequest;
    private string _searchMode = SearchModeKeyword;
    private string _lastSearchKeyword = string.Empty;
    private bool _startupScanTriggered;
    private string _costReportBaseImagePath = string.Empty;
    private string _costReportActorPayRatio = string.Empty;
    private string _costReportLegalRepresentative = string.Empty;

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
        DesktopDependencyInspector dependencyInspector,
        DesktopShellService shellService,
        IWorkflowInteractionService interactionService)
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
        _dependencyInspector = dependencyInspector;
        _shellService = shellService;
        _interactionService = interactionService;

        ScanCommand = new AsyncRelayCommand(ScanAsync, CanScan);
        RunSelectedProjectCommand = new AsyncRelayCommand(RunSelectedProjectAsync, CanRunSelectedProject);
        RunRootWorkflowCommand = new AsyncRelayCommand(RunRootWorkflowAsync, CanRunRootWorkflow);
        SearchDramaCommand = new AsyncRelayCommand(SearchDramaAsync, CanSearchDrama);
        LoadTodayDramaCommand = new AsyncRelayCommand(LoadTodayDramaAsync, CanLoadTodayDrama);
        GoPreviousSearchPageCommand = new AsyncRelayCommand(GoPreviousSearchPageAsync, CanGoPreviousSearchPage);
        GoNextSearchPageCommand = new AsyncRelayCommand(GoNextSearchPageAsync, CanGoNextSearchPage);
        ImportCheckedDramaCommand = new AsyncRelayCommand(ImportCheckedDramaAsync, CanImportCheckedDrama);
        ImportAndRunCheckedDramaCommand = new AsyncRelayCommand(ImportAndRunCheckedDramaAsync, CanImportAndRunCheckedDrama);
        DownloadCheckedDramaCommand = new AsyncRelayCommand(DownloadCheckedDramaAsync, CanDownloadCheckedDrama);
        ReloadConfigCommand = new RelayCommand(LoadConfig, CanOperateWithRootDir);
        SaveConfigCommand = new RelayCommand(SaveConfig, CanOperateWithRootDir);
        ValidateConfigCommand = new RelayCommand(ValidateConfig, CanOperateWithRootDir);
        RefreshDependenciesCommand = new RelayCommand(RefreshDependencies);
        RefreshArchivedProjectsCommand = new RelayCommand(LoadArchivedProjects, CanOperateWithRootDir);
        TestDependenciesCommand = new AsyncRelayCommand(TestDependenciesAsync, CanOperateWithRootDir);
        OpenSourceDirCommand = new RelayCommand(OpenSourceDir, CanOpenSourceDir);
        OpenWorkflowDirCommand = new RelayCommand(OpenWorkflowDir, CanOpenWorkflowDir);
        OpenArchivedProjectDirCommand = new RelayCommand(OpenArchivedProjectDir, CanOpenArchivedProjectDir);
        OpenArchivedSourceDirCommand = new RelayCommand(OpenArchivedSourceDir, CanOpenArchivedSourceDir);
        OpenArchivedWorkflowDirCommand = new RelayCommand(OpenArchivedWorkflowDir, CanOpenArchivedWorkflowDir);
        OpenConfigFileCommand = new RelayCommand(OpenConfigFile, CanOperateWithRootDir);
        OpenPosterCommand = new RelayCommand(OpenPoster, CanOpenPoster);
        OpenCostReportCommand = new RelayCommand(OpenCostReport, CanOpenCostReport);
        OpenProjectImageCommand = new RelayCommand(OpenProjectImage, CanOpenProjectImage);
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
        StatusMessage = "输入项目根目录后点击“扫描项目”。";
        SelectedStepOption = StepOptions.FirstOrDefault();
        SelectedExecutionModeOption = ExecutionModeOptions.FirstOrDefault();
        SelectedDownloadEpisodeRangeOption = DownloadEpisodeRangeOptions.FirstOrDefault();
        SelectedProjectLogFilter = ProjectLogFilters.First();
        SelectedStepLogFilter = StepLogFilters.First();
        _interactionService.RequestChanged += OnInteractionRequestChanged;
        LoadConfig();
        RefreshDependencies();
        QueueStartupScanIfNeeded();
    }

    public ObservableCollection<ProjectListItemViewModel> Projects { get; } = [];
    public ObservableCollection<ArchivedProjectItem> ArchivedProjects { get; } = [];
    public ObservableCollection<SearchResultRowViewModel> SearchResults { get; } = [];
    public ObservableCollection<ActivityLogEntry> ActivityLog { get; } = [];
    public ObservableCollection<DependencyStatusItem> Dependencies { get; } = [];
    public ObservableCollection<ConfigValidationItem> ConfigIssues { get; } = [];
    public ObservableCollection<MaterialValidationIssueItem> MaterialValidationIssues { get; } = [];
    public ObservableCollection<ProjectMaterialStepItem> ProjectMaterialSteps { get; } = [];
    public ObservableCollection<ActivityLogEntry> ProjectMaterialStepLogs { get; } = [];
    public ObservableCollection<LogFilterOption> ProjectLogFilters { get; } = [new(AllProjectsFilterKey, "全部项目")];
    public ObservableCollection<LogFilterOption> StepLogFilters { get; } =
    [
        new(AllStepsFilterKey, "全部步骤"),
        new(SystemStepFilterKey, "系统事件")
    ];
    public ObservableCollection<WorkflowStepOption> StepOptions { get; } =
    [
        new("download", "下载剧集"),
        new("transcode", "视频转码"),
        new("rewrite", "仿写剧名简介"),
        new("poster-rename", "生成海报图片"),
        new("project-image", "生成工程图"),
        new("cost-report", "生成成本报表"),
        new("batch-file-rename", "重命名视频文件"),
        new("material-convert", "转换素材视频"),
        new("weixin-upload", "微信上传剧集"),
        new("weixin-material-upload", "微信上传素材")
    ];
    public ObservableCollection<WorkflowStepOption> ExecutionModeOptions { get; } =
    [
        new(ExecutionModeSerial, "串行"),
        new(ExecutionModeConcurrent2, "并发 2")
    ];
    public ObservableCollection<WorkflowStepOption> DownloadEpisodeRangeOptions { get; } =
    [
        new(EpisodeRangeAll, "全部"),
        new(EpisodeRangeFirst3, "前3集"),
        new(EpisodeRangeCustom, "自定义")
    ];
    public ObservableCollection<string> WeixinMonetizationTypeOptions { get; } =
    [
        "IAA广告变现",
        "IAA广告",
        "IAP付费观看",
        "混合变现"
    ];
    public ObservableCollection<string> WeixinDramaTypeOptions { get; } =
    [
        "漫剧",
        "真人",
        "自动检测"
    ];
    public ObservableCollection<string> WeixinDramaQualificationOptions { get; } =
    [
        "其他微短剧",
        "重点普通微短剧"
    ];
    public ObservableCollection<string> WeixinSubmitterIdentityOptions { get; } =
    [
        "剧目制作方",
        "版权方",
        "平台方"
    ];

    public IAsyncRelayCommand ScanCommand { get; }
    public IAsyncRelayCommand RunSelectedProjectCommand { get; }
    public IAsyncRelayCommand RunRootWorkflowCommand { get; }
    public IAsyncRelayCommand SearchDramaCommand { get; }
    public IAsyncRelayCommand LoadTodayDramaCommand { get; }
    public IAsyncRelayCommand GoPreviousSearchPageCommand { get; }
    public IAsyncRelayCommand GoNextSearchPageCommand { get; }
    public IAsyncRelayCommand DownloadCheckedDramaCommand { get; }
    public IAsyncRelayCommand ImportCheckedDramaCommand { get; }
    public IAsyncRelayCommand ImportAndRunCheckedDramaCommand { get; }
    public IRelayCommand ReloadConfigCommand { get; }
    public IRelayCommand SaveConfigCommand { get; }
    public IRelayCommand ValidateConfigCommand { get; }
    public IRelayCommand RefreshDependenciesCommand { get; }
    public IRelayCommand RefreshArchivedProjectsCommand { get; }
    public IAsyncRelayCommand TestDependenciesCommand { get; }
    public IRelayCommand OpenSourceDirCommand { get; }
    public IRelayCommand OpenWorkflowDirCommand { get; }
    public IRelayCommand OpenArchivedProjectDirCommand { get; }
    public IRelayCommand OpenArchivedSourceDirCommand { get; }
    public IRelayCommand OpenArchivedWorkflowDirCommand { get; }
    public IRelayCommand OpenConfigFileCommand { get; }
    public IRelayCommand OpenPosterCommand { get; }
    public IRelayCommand OpenCostReportCommand { get; }
    public IRelayCommand OpenProjectImageCommand { get; }
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
    private string searchSummary = "输入剧名后搜索。";

    [ObservableProperty]
    private string activityTitle = "运行日志";

    [ObservableProperty]
    private string configFilePath = string.Empty;

    [ObservableProperty]
    private string configValidationSummary = "未校验";

    [ObservableProperty]
    private WorkflowStepOption? selectedStepOption;

    [ObservableProperty]
    private WorkflowStepOption? selectedExecutionModeOption;

    [ObservableProperty]
    private bool queueStepDownloadEnabled = true;

    [ObservableProperty]
    private bool queueStepProjectMaterialEnabled = true;

    [ObservableProperty]
    private bool queueStepEpisodeUploadEnabled = false;

    [ObservableProperty]
    private bool queueStepMaterialUploadEnabled = false;

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
            return checkedCount <= 0 ? "未勾选任务" : $"已勾选 {checkedCount} 个任务";
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
        TaskQueueDetailDownload => $"下载剧集 · {SelectedProjectTitle}",
        TaskQueueDetailProjectMaterial => $"生成项目素材 · {SelectedProjectTitle}",
        TaskQueueDetailEpisodeUpload => $"剧集上传 · {SelectedProjectTitle}",
        TaskQueueDetailMaterialUpload => $"素材上传 · {SelectedProjectTitle}",
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
    private string weixinMonetizationType = "IAA广告变现";

    [ObservableProperty]
    private string weixinDramaType = "漫剧";

    [ObservableProperty]
    private string weixinDramaQualification = "其他微短剧";

    [ObservableProperty]
    private string weixinSubmitterIdentity = "剧目制作方";

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
    private string interactionTitle = "人工介入";

    [ObservableProperty]
    private string interactionMessage = string.Empty;

    private Bitmap? _posterPreviewBitmap;
    private Bitmap? _costPreviewBitmap;
    private Bitmap? _projectImagePreviewBitmap;
    private string _posterPreviewPath = string.Empty;
    private string _costPreviewPath = string.Empty;
    private string _projectImagePreviewPath = string.Empty;

    public string SelectedProjectTitle => SelectedProject?.DisplayName ?? "未选择项目";
    public string SearchPageText => string.Equals(_searchMode, SearchModeToday, StringComparison.Ordinal)
        ? "今日上新"
        : $"第 {CurrentSearchPage} 页";
    public bool IsCustomDownloadEpisodeRange =>
        string.Equals(SelectedDownloadEpisodeRangeOption?.Key, EpisodeRangeCustom, StringComparison.Ordinal);
    public string WorkspaceSummary => string.IsNullOrWhiteSpace(RootDir) ? "未设置工作目录" : RootDir;
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
    partial void OnQueueStepDownloadEnabledChanged(bool value) => RefreshCommandStates();
    partial void OnQueueStepProjectMaterialEnabledChanged(bool value) => RefreshCommandStates();
    partial void OnQueueStepEpisodeUploadEnabledChanged(bool value) => RefreshCommandStates();
    partial void OnQueueStepMaterialUploadEnabledChanged(bool value) => RefreshCommandStates();

    partial void OnSelectedProjectLogFilterChanged(LogFilterOption? value) => ApplyActivityLogFilter();

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
        !string.Equals(_searchMode, SearchModeToday, StringComparison.Ordinal) &&
        CurrentSearchPage > 1 &&
        !string.IsNullOrWhiteSpace(_lastSearchKeyword);

    private bool CanGoNextSearchPage() =>
        !IsSearchBusy &&
        !string.Equals(_searchMode, SearchModeToday, StringComparison.Ordinal) &&
        SearchResults.Count > 0 &&
        !string.IsNullOrWhiteSpace(_lastSearchKeyword);

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
        HasAnyQueueStepSelected();

    private bool CanRunCurrentTask() =>
        !IsBusy &&
        CanOperateWithRootDir() &&
        SelectedProject is not null &&
        HasAnyQueueStepSelected();

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
        ScanCommand.NotifyCanExecuteChanged();
        RunSelectedProjectCommand.NotifyCanExecuteChanged();
        RunRootWorkflowCommand.NotifyCanExecuteChanged();
        SearchDramaCommand.NotifyCanExecuteChanged();
        LoadTodayDramaCommand.NotifyCanExecuteChanged();
        GoPreviousSearchPageCommand.NotifyCanExecuteChanged();
        GoNextSearchPageCommand.NotifyCanExecuteChanged();
        ImportCheckedDramaCommand.NotifyCanExecuteChanged();
        ImportAndRunCheckedDramaCommand.NotifyCanExecuteChanged();
        DownloadCheckedDramaCommand.NotifyCanExecuteChanged();
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
        TestDependenciesCommand.NotifyCanExecuteChanged();
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
            RefreshDependencies();
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
            StatusMessage = $"根目录不存在: {RootDir}";
            AppendLog($"扫描失败，目录不存在: {RootDir}");
            return;
        }

        await RunBusyAsync("正在扫描项目...", async cancellationToken =>
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
            StatusMessage = $"扫描完成，共 {result.TotalProjects} 个项目，待处理 {result.PendingProjects} 个。";
            AppendLog(StatusMessage);
            LoadConfig();
            RefreshDependencies();
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
        ActivityTitle = $"批处理日志 · {Path.GetFileName(RootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}";
        await RunBusyAsync("正在执行整个根目录工作流...", async cancellationToken =>
        {
            var progress = CreateBufferedProgress();
            var result = await _workService.RunAsync(
                RootDir,
                null,
                force: false,
                progress,
                cancellationToken);

            AppendLog($"批处理完成：成功 {result.SucceededProjects} 个，失败 {result.FailedProjects} 个，跳过 {result.SkippedProjects} 个。");
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
        await ExecuteSelectedProjectStepAsync("transcode", "视频转码");
    }

    private async Task RunSelectedProjectMaterialAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        await ExecuteProjectMaterialPipelineAsync(SelectedProject, "项目素材日志");
    }

    private async Task RunSelectedWeixinUploadAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        if (IsTaskQueueEpisodeUploadDetailVisible)
        {
            await ExecuteEpisodeUploadStepAsync(SelectedProject, null, "微信上传剧集");
            return;
        }

        await ExecuteSelectedProjectStepAsync("weixin-upload", "微信上传剧集");
    }

    private async Task RunSelectedWeixinMaterialUploadAsync()
    {
        await ExecuteSelectedProjectStepAsync("weixin-material-upload", "微信上传素材");
    }

    private Task RunCheckedProjectsAsync() =>
        ExecuteCheckedProjectsAsync(stepKey: null, stepLabel: "全流程");

    private Task RunCheckedTranscodeAsync() =>
        ExecuteCheckedProjectsAsync("transcode", "视频转码");

    private Task RunCheckedProjectMaterialAsync() =>
        ExecuteCheckedProjectsAsync("__project-material__", "一键生成项目素材");

    private Task RunCheckedWeixinUploadAsync() =>
        ExecuteCheckedProjectsAsync("weixin-upload", "微信上传剧集");

    private Task RunCheckedWeixinMaterialUploadAsync() =>
        ExecuteCheckedProjectsAsync("weixin-material-upload", "微信上传素材");

    private async Task RunCheckedQueueAsync()
    {
        var selectedProjects = Projects.Where(item => item.IsChecked).ToArray();
        if (selectedProjects.Length == 0)
        {
            return;
        }

        ActivityTitle = "任务队列日志";
        await RunBusyAsync($"正在执行勾选队列，共 {selectedProjects.Length} 个项目...", async cancellationToken =>
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
            StatusMessage = $"勾选队列执行完成，共处理 {selectedProjects.Length} 个项目。";
            AppendLog(StatusMessage);
        });
    }

    private async Task RunCurrentTaskAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        ActivityTitle = $"任务详情日志 · {SelectedProject.DisplayName}";
        await RunBusyAsync($"正在执行当前任务：{SelectedProject.DisplayName}", async cancellationToken =>
        {
            await ExecuteQueueSelectionForProjectAsync(SelectedProject, 1, 1, cancellationToken);
            await RefreshAfterExecutionAsync(SelectedProject.ProjectKey);
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

        await ExecuteArchiveAsync([SelectedProject.ProjectKey], $"正在归档项目：{SelectedProject.DisplayName}");
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
            $"正在归档项目：{SelectedProject.DisplayName}",
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

        await ExecuteArchiveAsync(checkedKeys, $"正在归档勾选项目，共 {checkedKeys.Length} 个...");
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
            $"正在归档勾选项目，共 {checkedKeys.Length} 个...",
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
            AppendLog($"归档失败: {ex.Message}", string.Empty, string.Empty, "archive", "归档项目", isFailure: true);
        }
        finally
        {
            RefreshCommandStates();
        }
    }

    private static bool CanArchiveProject(ProjectListItemViewModel project)
    {
        return !string.Equals(project.SchedulingStatus, "运行中", StringComparison.Ordinal) &&
               !string.Equals(project.SchedulingStatus, "排队中", StringComparison.Ordinal);
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
        ActivityTitle = $"步骤日志 · {SelectedProject.DisplayName} · {stepLabel}";
        await RunBusyAsync($"正在执行步骤：{stepLabel}", async cancellationToken =>
        {
            var progress = CreateBufferedProgress();
            var result = await _workService.RunProjectStepAsync(
                SelectedProject.SourceProjectDir,
                null,
                stepKey,
                force: true,
                progress,
                cancellationToken);

            AppendLog($"步骤完成: {stepLabel}，结果={(result.Ok ? "成功" : "失败")}");
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
        ActivityTitle = $"项目素材校验 · {project.DisplayName}";
        ClearMaterialValidationIssues();

        await RunSearchBusyAsync($"正在校验项目素材：{project.DisplayName}", async cancellationToken =>
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
        await RunBusyAsync($"正在修复素材问题：{issue.Message}", async cancellationToken =>
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
            StatusMessage = "未选择项目，无法执行一键修复。";
            return;
        }

        var project = SelectedProject;

        await RunBusyAsync($"正在一键修复素材问题：{project.DisplayName}", async cancellationToken =>
        {
            var fixableIssues = await RefreshMaterialValidationIssuesAsync(project, cancellationToken, appendLogs: false);
            if (fixableIssues.Length == 0)
            {
                StatusMessage = $"当前没有可自动修复的素材问题：{project.DisplayName}";
                AppendLog(StatusMessage);
                return;
            }

            ShowMaterialValidationInProgress($"共 {fixableIssues.Length} 项待修复");
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
            "处理中",
            $"正在修复：{message}，完成后将自动重新校验。",
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
                "通过",
                "素材校验通过。",
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
                    "素材校验",
                    isFailure: string.Equals(issue.Severity, "错误", StringComparison.Ordinal));
            }

            if (result.Issues.Count == 0)
            {
                AppendLog(
                    "素材校验通过。",
                    project.ProjectKey,
                    project.DisplayName,
                    "project-material-validate",
                    "素材校验");
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
        return ExecuteProjectMaterialPipelineAsync(project, "项目素材日志");
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
                AppendLog($"归档跳过：未找到项目 {projectKey}", projectKey, projectKey, "archive", "归档项目", isFailure: true);
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
                $"归档完成：{scannedProject.DisplayName}，{archiveResult.Message}",
                projectKey,
                scannedProject.DisplayName,
                "archive",
                "归档项目");
        }

        if (archivedAnySelected)
        {
            IsTaskQueueDetailOpen = false;
        }

        await RefreshProjectListAsync();
        StatusMessage = archivedCount > 0
            ? $"归档完成，共处理 {archivedCount} 个项目。"
            : "未归档任何项目。";
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
            ActivityTitle = $"下载剧集日志 · {project.DisplayName}";
        }
        else if (string.Equals(detailMode, TaskQueueDetailProjectMaterial, StringComparison.Ordinal))
        {
            ClearMaterialValidationIssues();
            SelectedStepOption = StepOptions.FirstOrDefault(item => string.Equals(item.Key, "transcode", StringComparison.Ordinal))
                ?? SelectedStepOption;
            SelectedStepLogFilter = StepLogFilters.FirstOrDefault(item => string.Equals(item.Key, AllStepsFilterKey, StringComparison.Ordinal))
                ?? SelectedStepLogFilter;
            ActivityTitle = $"项目素材日志 · {project.DisplayName}";
        }
        else if (string.Equals(detailMode, TaskQueueDetailEpisodeUpload, StringComparison.Ordinal))
        {
            SelectedStepLogFilter = StepLogFilters.FirstOrDefault(item => string.Equals(item.Key, "weixin-upload", StringComparison.Ordinal))
                ?? SelectedStepLogFilter;
            ActivityTitle = $"剧集上传日志 · {project.DisplayName}";
        }
        else if (string.Equals(detailMode, TaskQueueDetailMaterialUpload, StringComparison.Ordinal))
        {
            SelectedStepLogFilter = StepLogFilters.FirstOrDefault(item => string.Equals(item.Key, "weixin-material-upload", StringComparison.Ordinal))
                ?? SelectedStepLogFilter;
            ActivityTitle = $"素材上传日志 · {project.DisplayName}";
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
                $"下载剧集 重试失败：缺少 book_id，无法下载第{episode.EpisodeNumber}集。",
                project.ProjectKey,
                project.DisplayName,
                "download",
                "下载剧集",
                isFailure: true);
            return;
        }

        await RunBusyAsync($"正在重试下载：{project.DisplayName} · 第{episode.EpisodeNumber}集", async cancellationToken =>
        {
            HandleProgress(new WorkRunEvent(project.ProjectKey, project.DisplayName, "step-started", "download", $"重试第{episode.EpisodeNumber}集", true));

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
                "请先停止当前上传，再执行逐行重试。",
                SelectedProject.ProjectKey,
                SelectedProject.DisplayName,
                "weixin-upload",
                "微信上传剧集",
                isFailure: true);
            return;
        }

        SelectedProject.ClearEpisodeUploadSkipped(episode.EpisodeNumber);
        await ExecuteEpisodeUploadStepAsync(SelectedProject, [episode.EpisodeNumber], $"重试第{episode.EpisodeNumber}集");
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
                "请先停止当前上传，再执行逐行跳过。",
                SelectedProject.ProjectKey,
                SelectedProject.DisplayName,
                "weixin-upload",
                "微信上传剧集",
                isFailure: true);
            return;
        }

        SelectedProject.MarkEpisodeUploadSkipped(episode.EpisodeNumber);
        AppendLog(
            $"剧集上传 已将第{episode.EpisodeNumber}集标记为跳过，下一次开始/继续上传时将不包含该集。",
            SelectedProject.ProjectKey,
            SelectedProject.DisplayName,
            "weixin-upload",
            "微信上传剧集");
    }

    public void MarkEpisodeUploadCompleted(EpisodeUploadItemViewModel? episode)
    {
        if (SelectedProject is null || episode is null)
        {
            return;
        }

        SelectedProject.MarkEpisodeUploadCompleted(episode.EpisodeNumber);
        AppendLog(
            $"剧集上传 已将第{episode.EpisodeNumber}集手动标记为完成。",
            SelectedProject.ProjectKey,
            SelectedProject.DisplayName,
            "weixin-upload",
            "微信上传剧集");
    }

    public void MarkMaterialUploadCompleted()
    {
        if (SelectedProject is null)
        {
            return;
        }

        SelectedProject.MaterialUploadStepStatus = "已完成";
        AppendLog(
            "素材上传 已手动标记为完成。",
            SelectedProject.ProjectKey,
            SelectedProject.DisplayName,
            "weixin-material-upload",
            "微信上传素材");
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
                "当前有任务运行，请先停止后再修改新剧名。",
                SelectedProject.ProjectKey,
                SelectedProject.DisplayName,
                "rewrite",
                "修改新剧名",
                isFailure: true);
            return;
        }

        var project = SelectedProject;
        var trimmedTitle = newTitle?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedTitle))
        {
            return;
        }

        await RunSearchBusyAsync($"正在修改新剧名：{project.DisplayName} -> {trimmedTitle}", async cancellationToken =>
        {
            var result = await _workService.UpdateProjectTitleAsync(
                project.SourceProjectDir,
                null,
                trimmedTitle,
                cancellationToken);

            AppendLog(
                $"已更新新剧名：{result.OriginalTitle} -> {result.NewTitle}",
                result.ProjectKey,
                result.NewTitle,
                "rewrite",
                "修改新剧名");

            AppendLog(
                $"已同步重命名视频文件 {result.RenamedVideoCount} 个。",
                result.ProjectKey,
                result.NewTitle,
                "batch-file-rename",
                "重命名视频文件");

            AppendLog(
                $"已同步刷新微信上传配置 {result.UpdatedWeixinConfigCount} 个。",
                result.ProjectKey,
                result.NewTitle,
                "weixin-upload",
                "微信上传剧集");

            if (result.RegeneratedSteps.Count > 0)
            {
                AppendLog(
                    $"已自动重新生成步骤：{string.Join("、", result.RegeneratedSteps)}",
                    result.ProjectKey,
                    result.NewTitle,
                    "project-material",
                    "生成项目素材");
            }

            AppendLog(
                $"已失效步骤：{string.Join("、", result.InvalidatedSteps)}",
                result.ProjectKey,
                result.NewTitle,
                "rewrite",
                "修改新剧名");

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
            $"下载剧集 已移除第{episode.EpisodeNumber}集的源视频文件。",
            project.ProjectKey,
            project.DisplayName,
            "download",
            "下载剧集");

        await RefreshAfterExecutionAsync(project.ProjectKey);
    }

    private async Task ExecuteEpisodeUploadStepAsync(ProjectListItemViewModel project, IReadOnlyCollection<int>? onlyEpisodes, string stepLabel)
    {
        ClearLogsForProject(project.ProjectKey);
        ActivityTitle = $"剧集上传日志 · {project.DisplayName}";
        AppendLog(
            $"开始执行: {stepLabel}",
            project.ProjectKey,
            project.DisplayName,
            "weixin-upload",
            "微信上传剧集");

        await RunBusyAsync($"正在执行步骤：{stepLabel}", async cancellationToken =>
        {
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
                        $"已确认微信剧集上传配置：{ensuredConfigPath}",
                        project.ProjectKey,
                        project.DisplayName,
                        "weixin-upload",
                        "微信上传剧集");

                    overrideConfigPath = CreateEpisodeUploadOverrideConfig(project, onlyEpisodes);
                }
                catch (Exception ex)
                {
                    AppendLog(
                        $"启动失败: {ex.Message}",
                        project.ProjectKey,
                        project.DisplayName,
                        "weixin-upload",
                        "微信上传剧集",
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
                        $"执行异常: {ex.Message}",
                        project.ProjectKey,
                        project.DisplayName,
                        "weixin-upload",
                        "微信上传剧集",
                        isFailure: true);
                    throw;
                }

                AppendLog(
                    $"步骤完成: {stepLabel}，结果={(result.Ok ? "成功" : "失败")}",
                    project.ProjectKey,
                    project.DisplayName,
                    "weixin-upload",
                    "微信上传剧集",
                    !result.Ok);

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
            throw new FileNotFoundException("未找到微信剧集上传配置文件。", configPath);
        }

        var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
            ?? throw new InvalidOperationException("微信剧集上传配置文件格式无效。");
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
            throw new InvalidOperationException("当前没有可执行上传的剧集。");
        }

        var uploadItems = ResolveEpisodeUploadQueueItems(uploadQueue, upload)
            .Where(item => item.EpisodeNumber > 0 && selectedEpisodes.Contains(item.EpisodeNumber))
            .ToArray();
        if (uploadItems.Length == 0)
        {
            throw new InvalidOperationException("未在上传配置中找到可执行的剧集视频。");
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
        foreach (var project in Projects)
        {
            project.IsChecked = isChecked;
        }

        OnPropertyChanged(nameof(CheckedProjectsSummary));
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
            ? $"批量项目日志 · {stepLabel}"
            : $"批量步骤日志 · {stepLabel}";

        await RunBusyAsync(
            stepKey is null
                ? $"正在批量执行{stepLabel}，共 {selectedProjects.Length} 个项目..."
                : $"正在批量执行{stepLabel}，共 {selectedProjects.Length} 个项目...",
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
                    ? $"批量全流程执行完成，共处理 {selectedProjects.Length} 个项目。"
                    : $"批量{stepLabel}完成，共处理 {selectedProjects.Length} 个项目。";
                AppendLog(StatusMessage);
            });
    }

    private async Task ExecuteQueueSelectionForProjectAsync(
        ProjectListItemViewModel project,
        int index,
        int total,
        CancellationToken cancellationToken)
    {
        project.MarkRunning("任务队列");
        ClearLogsForProject(project.ProjectKey);

        var selectedSteps = GetQueueSelectedSteps();
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

            var progress = CreateBufferedProgress();
            var result = await _workService.RunProjectStepAsync(
                project.SourceProjectDir,
                null,
                stepKey,
                force: false,
                progress,
                cancellationToken);

            AppendLog(
                $"{prefix}节点完成: {project.DisplayName} · {stepLabel}，结果={(result.Ok ? "成功" : "失败")}",
                project.ProjectKey,
                project.DisplayName,
                stepKey,
                stepLabel,
                !result.Ok);

            if (!result.Ok)
            {
                project.MarkFailed();
                return;
            }
        }

        project.MarkCompleted();
    }

    private (string Key, string Label)[] GetQueueSelectedSteps()
    {
        var steps = new List<(string Key, string Label)>();
        if (QueueStepDownloadEnabled)
        {
            steps.Add(("download", "下载剧集"));
        }

        if (QueueStepProjectMaterialEnabled)
        {
            steps.Add(("__project-material__", "生成项目素材"));
        }

        if (QueueStepEpisodeUploadEnabled)
        {
            steps.Add(("weixin-upload", "剧集上传"));
        }

        if (QueueStepMaterialUploadEnabled)
        {
            steps.Add(("weixin-material-upload", "素材上传"));
        }

        return steps.ToArray();
    }

    private bool HasAnyQueueStepSelected()
    {
        return QueueStepDownloadEnabled ||
               QueueStepProjectMaterialEnabled ||
               QueueStepEpisodeUploadEnabled ||
               QueueStepMaterialUploadEnabled;
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
        project.MarkRunning(stepKey is null ? "全流程" : stepLabel);
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

            AppendLog($"[{index}/{total}] 步骤完成: {project.DisplayName} · {stepLabel}，结果={(stepResult.Ok ? "成功" : "失败")}");
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
        ActivityTitle = $"{titlePrefix} · {project.DisplayName}";
        await RunBusyAsync($"正在生成项目素材：{project.DisplayName}", async cancellationToken =>
        {
            project.MarkRunning("一键生成项目素材");
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
            AppendLog(
                $"{prefix}项目素材流程 {index + 1}/{ProjectMaterialPipelineSteps.Length}: {stepLabel}",
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
                $"{prefix}步骤完成: {project.DisplayName} · {stepLabel}，结果={(result.Ok ? "成功" : "失败")}",
                project.ProjectKey,
                project.DisplayName,
                stepKey,
                stepLabel,
                !result.Ok);

            if (!result.Ok)
            {
                return false;
            }
        }

        AppendLog(
            $"{prefix}项目素材生成完成: {project.DisplayName}",
            project.ProjectKey,
            project.DisplayName,
            string.Empty,
            "一键生成项目素材");
        return true;
    }

    private async Task SearchDramaAsync()
    {
        var keyword = SearchKeyword.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            SearchSummary = "请输入剧名关键词。";
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
        var busyMessage = string.Equals(_searchMode, SearchModeToday, StringComparison.Ordinal)
            ? "正在获取今日上新..."
            : $"正在搜索短剧：{_lastSearchKeyword}";

        await RunSearchBusyAsync(busyMessage, async cancellationToken =>
        {
            var results = string.Equals(_searchMode, SearchModeToday, StringComparison.Ordinal)
                ? await _dramaSearchService.GetTodayAsync(cancellationToken)
                : await _dramaSearchService.SearchAsync(_lastSearchKeyword, CurrentSearchPage, cancellationToken);

            var pageSize = ParseSearchPageSize();
            var visibleResults = results.Take(pageSize).ToArray();

            UnsubscribeFromSearchResultRows();
            SearchResults.Clear();
            foreach (var item in visibleResults)
            {
                var row = new SearchResultRowViewModel(item);
                row.CheckedChanged += OnSearchResultRowCheckedChanged;
                SearchResults.Add(row);
            }

            SelectedSearchResult = SearchResults.FirstOrDefault();
            SearchSummary = BuildSearchSummary(results.Count, visibleResults.Length, pageSize);
            StatusMessage = SearchSummary;
            OnPropertyChanged(nameof(SearchPageText));
            AppendLog(SearchSummary);
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
            AppendLog($"搜索失败: {ex.Message}", string.Empty, string.Empty, "search", "短剧搜索", isFailure: true);
        }
        finally
        {
            IsSearchBusy = false;
            RefreshCommandStates();
        }
    }

    private string BuildSearchSummary(int totalCount, int visibleCount, int pageSize)
    {
        if (string.Equals(_searchMode, SearchModeToday, StringComparison.Ordinal))
        {
            return totalCount == 0
                ? "今日上新暂无结果。"
                : $"今日上新共 {totalCount} 条，当前展示 {visibleCount} 条，分页大小 {pageSize}。";
        }

        return totalCount == 0
            ? $"第 {CurrentSearchPage} 页未找到“{_lastSearchKeyword}”的匹配结果。"
            : $"“{_lastSearchKeyword}”第 {CurrentSearchPage} 页共 {totalCount} 条结果，当前展示 {visibleCount} 条，分页大小 {pageSize}。";
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
            StatusMessage = "下载集数范围无效，请输入如 1-5 或 1,3,5。";
            AppendLog(StatusMessage, string.Empty, string.Empty, "search", "短剧搜索", isFailure: true);
            return;
        }

        var selectionLabel = string.Equals(episodes, "all", StringComparison.OrdinalIgnoreCase) ? "全部" : episodes;
        await RunSearchBusyAsync($"正在下载勾选项目，共 {selectedRows.Length} 个项目，范围：{selectionLabel}...", async cancellationToken =>
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
                    $"[{processed}/{selectedRows.Length}] 已写入下载范围并准备下载：{bootstrap.DisplayName}（{selectionLabel}）",
                    bootstrap.ProjectKey,
                    bootstrap.DisplayName,
                    "download",
                    "下载剧集");

                var result = await _workService.RunProjectStepAsync(
                    bootstrap.SourceProjectDir,
                    null,
                    "download",
                    force: true,
                    progress: null,
                    cancellationToken);

                AppendLog(
                    $"[{processed}/{selectedRows.Length}] 下载完成：{result.DisplayName}，结果={(result.Ok ? "成功" : "失败")}",
                    result.ProjectKey,
                    result.DisplayName,
                    "download",
                    "下载剧集",
                    !result.Ok);
            }

            await RefreshProjectListAsync();
            StatusMessage = $"勾选项目下载完成，共处理 {selectedRows.Length} 个，范围：{selectionLabel}。";
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

        var actionText = runWorkflow ? "导入并执行勾选项目全流程" : "导入勾选项目到工作目录";
        Func<string, Func<CancellationToken, Task>, Task> runner = runWorkflow
            ? RunBusyAsync
            : RunSearchBusyAsync;

        await runner($"正在{actionText}，共 {selectedRows.Length} 个项目...", async cancellationToken =>
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
                    ? $"[{processed}/{selectedRows.Length}] 已导入项目：{bootstrap.DisplayName}"
                    : $"[{processed}/{selectedRows.Length}] 项目已存在，复用工作目录：{bootstrap.DisplayName}");

                if (!runWorkflow)
                {
                    continue;
                }

                ClearLogsForProject(bootstrap.ProjectKey);
                ActivityTitle = $"项目日志 · {bootstrap.DisplayName}";
                var progress = CreateBufferedProgress();
                var result = await _workService.RunProjectAsync(
                    bootstrap.SourceProjectDir,
                    null,
                    force: false,
                    progress,
                    cancellationToken);

                AppendLog($"项目完成: {result.DisplayName}，结果={(result.Ok ? "成功" : "失败")}");
            }

            await RefreshProjectListAsync();
            StatusMessage = runWorkflow
                ? $"勾选项目已执行完成，共处理 {selectedRows.Length} 个。"
                : $"勾选项目已导入完成，共处理 {selectedRows.Length} 个。";
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
        ActivityTitle = $"项目日志 · {displayName}";
        await RunBusyAsync($"正在执行项目：{displayName}", async cancellationToken =>
        {
            var project = Projects.FirstOrDefault(item => string.Equals(item.ProjectKey, projectKey, StringComparison.Ordinal));
            var result = project is null
                ? await RunProjectNormallyAsync(sourceProjectDir, cancellationToken)
                : await RunProjectWithSmartResumeAsync(project, cancellationToken);

            AppendLog($"项目完成: {result.DisplayName}，结果={(result.Ok ? "成功" : "失败")}", projectKey, displayName, string.Empty, string.Empty, !result.Ok);
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
            AppendLog($"{prefix}项目完成: {projectResult.DisplayName}，结果={(projectResult.Ok ? "成功" : "失败")}");
            return projectResult;
        }

        var stepLabel = ResolveStepLabel(smartStep);
        var prefixMessage = batchIndex.HasValue && batchTotal.HasValue ? $"[{batchIndex}/{batchTotal}] " : string.Empty;
        AppendLog($"{prefixMessage}检测到项目缺少下载元数据，自动从“{stepLabel}”继续。", project.ProjectKey, project.DisplayName, smartStep, stepLabel);

        var progress = CreateBufferedProgress();
        var stepResult = await _workService.RunProjectStepAsync(
            project.SourceProjectDir,
            null,
            smartStep,
            force: true,
            progress,
            cancellationToken);

        AppendLog($"{prefixMessage}步骤完成: {project.DisplayName} · {stepLabel}，结果={(stepResult.Ok ? "成功" : "失败")}");
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
            StatusMessage = "已停止当前运行，进度已保存，可继续运行。";
            AppendLog(StatusMessage);
            await RefreshProjectListAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            AppendLog($"错误: {ex.Message}", string.Empty, string.Empty, string.Empty, string.Empty, isFailure: true);
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
        StatusMessage = "正在停止当前运行并保存进度...";
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
            "resume" => "继续执行",
            "skip_video" => "跳过当前素材",
            "skip_project" => "跳过当前项目",
            "stop" => "停止任务",
            _ => decision
        };
        StatusMessage = $"已提交人工介入决策：{actionLabel}";
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
        var running = materialSteps.FirstOrDefault(item => string.Equals(item.Status, "进行中", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(running.Key))
        {
            return running.Key;
        }

        var blocked = materialSteps.FirstOrDefault(item =>
            string.Equals(item.Status, "失败", StringComparison.Ordinal) ||
            string.Equals(item.Status, "待继续", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(blocked.Key))
        {
            return blocked.Key;
        }

        var pending = materialSteps.FirstOrDefault(item => !string.Equals(item.Status, "已完成", StringComparison.Ordinal));
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
            "已完成" => (isSelected ? new SolidColorBrush(Color.Parse("#112F1D")) : Brushes.Transparent, Brushes.LimeGreen, Brushes.LimeGreen, Brushes.LightGreen),
            "进行中" => (isSelected ? new SolidColorBrush(Color.Parse("#102A44")) : new SolidColorBrush(Color.Parse("#0A1A2A")), Brushes.DodgerBlue, Brushes.DodgerBlue, Brushes.LightBlue),
            "失败" => (isSelected ? new SolidColorBrush(Color.Parse("#3A1616")) : Brushes.Transparent, Brushes.IndianRed, Brushes.IndianRed, Brushes.LightCoral),
            "待继续" => (isSelected ? new SolidColorBrush(Color.Parse("#3A2A12")) : Brushes.Transparent, Brushes.Orange, Brushes.Orange, Brushes.Wheat),
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

    private void OnSearchResultRowCheckedChanged(object? sender, EventArgs e) => RefreshCommandStates();

    private void OnProjectRowCheckedChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CheckedProjectsSummary));
        RefreshCommandStates();
    }

    private void OnInteractionRequestChanged(WorkflowInteractionRequest? request)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _currentInteractionRequest = request;
            HasInteractionRequest = request is not null;
            InteractionTitle = request is null
                ? "人工介入"
                : $"人工介入 · {ResolveInteractionScopeLabel(request.Scope)}";
            InteractionMessage = request?.Message ?? string.Empty;
            if (request is not null)
            {
                StatusMessage = $"等待人工处理：{request.DisplayName}";
                AppendLog(
                    $"等待人工处理: {request.Message}",
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
        StatusMessage = $"刷新完成，共 {result.TotalProjects} 个项目。";
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
            Projects.Add(item);
        }

        OnPropertyChanged(nameof(CheckedProjectsSummary));
        RefreshCommandStates();
    }

    private void UnsubscribeFromProjectRows()
    {
        foreach (var project in Projects)
        {
            project.CheckedChanged -= OnProjectRowCheckedChanged;
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
        StatusMessage = $"已加载配置：{config.ConfigFilePath}";
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
        StatusMessage = $"配置已保存：{snapshot.ConfigFilePath}";
        AppendLog(StatusMessage);
        ValidateConfig();
    }

    private void RefreshDependencies()
    {
        Dependencies.Clear();
        foreach (var item in _dependencyInspector.Inspect())
        {
            Dependencies.Add(item);
        }

        AppendLog($"依赖检测完成：共 {Dependencies.Count} 项。");
    }

    private async Task TestDependenciesAsync()
    {
        await RunBusyAsync("正在测试外部依赖...", async cancellationToken =>
        {
            var items = await _dependencyInspector.TestAsync(cancellationToken);
            Dependencies.Clear();
            foreach (var item in items)
            {
                Dependencies.Add(item);
            }

            var passed = items.Count(item => string.Equals(item.TestStatus, "通过", StringComparison.Ordinal));
            var failed = items.Count(item => string.Equals(item.TestStatus, "失败", StringComparison.Ordinal));
            AppendLog($"依赖测试完成：通过 {passed} 项，失败 {failed} 项。");
        });
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
        WeixinMonetizationType = string.IsNullOrWhiteSpace(config.WeixinMonetizationType) ? "IAA广告变现" : config.WeixinMonetizationType;
        WeixinDramaType = string.IsNullOrWhiteSpace(config.WeixinDramaType) ? "漫剧" : config.WeixinDramaType;
        WeixinDramaQualification = string.IsNullOrWhiteSpace(config.WeixinDramaQualification) ? "其他微短剧" : config.WeixinDramaQualification;
        WeixinSubmitterIdentity = string.IsNullOrWhiteSpace(config.WeixinSubmitterIdentity) ? "剧目制作方" : config.WeixinSubmitterIdentity;
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
            AddConfigIssue("错误", "项目根目录为空。");
            FinalizeConfigValidation();
            return;
        }

        if (!Directory.Exists(RootDir))
        {
            AddConfigIssue("错误", $"项目根目录不存在: {RootDir}");
            FinalizeConfigValidation();
            return;
        }

        var configDir = DesktopConfigService.GetConfigDirectoryPath(RootDir);
        var configPath = DesktopConfigService.GetConfigFilePath(RootDir);

        if (!Directory.Exists(configDir))
        {
            AddConfigIssue("错误", $"缺少 config 目录: {configDir}");
        }

        if (!File.Exists(configPath))
        {
            AddConfigIssue("错误", $"缺少配置文件: {configPath}");
        }

        var templatePath = ResolveConfigPath(TemplateDocxPath);
        if (string.IsNullOrWhiteSpace(templatePath))
        {
            AddConfigIssue("提示", "未配置成本报表模板，将只生成 PNG，不保留原始 docx。");
        }
        else if (!File.Exists(templatePath))
        {
            AddConfigIssue("警告", $"成本报表模板不存在: {templatePath}；PNG 仍可生成，但不会输出原始 docx。");
        }

        if (!string.IsNullOrWhiteSpace(_costReportBaseImagePath))
        {
            var baseImagePath = ResolveConfigPath(_costReportBaseImagePath);
            if (!File.Exists(baseImagePath))
            {
                AddConfigIssue("警告", $"成本报表底图不存在: {baseImagePath}；将回退到内置底图。");
            }
        }

        var projectImageTemplatePath = ResolveConfigPath(ProjectImageTemplateDir);
        if (string.IsNullOrWhiteSpace(projectImageTemplatePath))
        {
            AddConfigIssue("错误", "未配置工程图模板目录，工程图步骤无法执行。");
        }
        else if (!Directory.Exists(projectImageTemplatePath))
        {
            AddConfigIssue("错误", $"工程图模板目录不存在: {projectImageTemplatePath}");
        }
        else
        {
            ValidateProjectImageTemplates(projectImageTemplatePath);
        }

        var signPath = Path.Combine(configDir, "sign.png");
        var sealPath = Path.Combine(configDir, "seal.png");
        if (!File.Exists(signPath))
        {
            AddConfigIssue("错误", $"缺少签字图片: {signPath}");
        }

        if (!File.Exists(sealPath))
        {
            AddConfigIssue("错误", $"缺少盖章图片: {sealPath}");
        }

        if (string.IsNullOrWhiteSpace(CompanyName))
        {
            AddConfigIssue("警告", "CompanyName 为空，部分生成内容会缺少公司名称。");
        }

        if (!int.TryParse(SearchPageSize, out var searchPageSize) || searchPageSize <= 0)
        {
            AddConfigIssue("错误", "SearchPageSize 必须是大于 0 的整数。");
        }

        if (string.IsNullOrWhiteSpace(ChatModelId))
        {
            AddConfigIssue("错误", "未配置 ChatModelId，仿写剧名简介无法执行。");
        }

        if (string.IsNullOrWhiteSpace(ChatModelApiKey))
        {
            AddConfigIssue("错误", "未配置 ChatModelApiKey。");
        }

        if (string.IsNullOrWhiteSpace(ChatModelEndpoint))
        {
            AddConfigIssue("警告", "未配置 ChatModelEndpoint，可能无法访问文本模型。");
        }

        if (string.IsNullOrWhiteSpace(AiTextEndpoint))
        {
            AddConfigIssue("警告", "未配置 AiTextEndpoint，将回退到文本模型地址。");
        }

        if (string.IsNullOrWhiteSpace(AiTextApiKey))
        {
            AddConfigIssue("警告", "未配置 AiTextApiKey，将回退到文本模型密钥。");
        }

        if (string.IsNullOrWhiteSpace(AiTextModel))
        {
            AddConfigIssue("警告", "未配置 AiTextModel，将回退到文本模型 ID。");
        }

        if (!int.TryParse(AiTextTimeoutSeconds, out var aiTextTimeout) || aiTextTimeout <= 0)
        {
            AddConfigIssue("错误", "AiTextTimeoutSeconds 必须是大于 0 的整数。");
        }

        if (!int.TryParse(AiTextMaxBatchSize, out var aiTextBatchSize) || aiTextBatchSize <= 0)
        {
            AddConfigIssue("错误", "AiTextMaxBatchSize 必须是大于 0 的整数。");
        }

        if (string.IsNullOrWhiteSpace(AiTextBatchPrompt))
        {
            AddConfigIssue("警告", "AiTextBatchPrompt 为空，将使用内置默认提示词。");
        }

        if (string.IsNullOrWhiteSpace(AiTextRetryPrompt))
        {
            AddConfigIssue("警告", "AiTextRetryPrompt 为空，将使用内置默认纠偏提示词。");
        }

        if (!int.TryParse(WeixinSlowMoMs, out var weixinSlowMoValue) || weixinSlowMoValue < 0)
        {
            AddConfigIssue("错误", "WeixinSlowMoMs 必须是大于等于 0 的整数。");
        }

        if (!int.TryParse(WeixinKeepOpenSeconds, out var weixinKeepOpenValue) || weixinKeepOpenValue < 0)
        {
            AddConfigIssue("错误", "WeixinKeepOpenSeconds 必须是大于等于 0 的整数。");
        }

        if (!int.TryParse(WeixinLoginTimeoutSeconds, out var weixinLoginTimeoutValue) || weixinLoginTimeoutValue <= 0)
        {
            AddConfigIssue("错误", "WeixinLoginTimeoutSeconds 必须是大于 0 的整数。");
        }

        if (string.IsNullOrWhiteSpace(WeixinMonetizationType))
        {
            AddConfigIssue("警告", "WeixinMonetizationType 为空，将回退到默认值 IAA广告变现。");
        }

        if (string.IsNullOrWhiteSpace(WeixinDramaType))
        {
            AddConfigIssue("警告", "WeixinDramaType 为空，将回退到默认值 漫剧。");
        }

        if (string.IsNullOrWhiteSpace(WeixinDramaQualification))
        {
            AddConfigIssue("警告", "WeixinDramaQualification 为空，将回退到默认值 其他微短剧。");
        }

        if (string.IsNullOrWhiteSpace(WeixinSubmitterIdentity))
        {
            AddConfigIssue("警告", "WeixinSubmitterIdentity 为空，将回退到默认值 剧目制作方。");
        }

        if (!int.TryParse(WeixinTrialEpisodes, out var weixinTrialEpisodesValue) || weixinTrialEpisodesValue <= 0)
        {
            AddConfigIssue("错误", "WeixinTrialEpisodes 必须是大于 0 的整数。");
        }

        var submissionReportDir = ResolveConfigPath(WeixinSubmissionReportDir);
        if (!string.IsNullOrWhiteSpace(submissionReportDir) && !Directory.Exists(submissionReportDir))
        {
            AddConfigIssue("错误", $"WeixinSubmissionReportDir 不存在: {submissionReportDir}");
        }

        if (string.IsNullOrWhiteSpace(ImageModelId))
        {
            AddConfigIssue("错误", "未配置 ImageModelId，海报生成无法执行。");
        }

        if (string.IsNullOrWhiteSpace(ImageModelApiKey))
        {
            AddConfigIssue("错误", "未配置 ImageModelApiKey。");
        }

        if (string.IsNullOrWhiteSpace(ImageModelEndpoint))
        {
            AddConfigIssue("警告", "未配置 ImageModelEndpoint，图片模型请求可能失败。");
        }

        if (string.IsNullOrWhiteSpace(PosterLayoutDetectPrompt))
        {
            AddConfigIssue("警告", "PosterLayoutDetectPrompt 为空，将使用内置海报布局检测提示词。");
        }

        if (string.IsNullOrWhiteSpace(PosterNameUserPrompt))
        {
            AddConfigIssue("警告", "PosterNameUserPrompt 为空，将使用内置海报名提示词。");
        }

        if (!int.TryParse(ProjectImageCount, out var projectImageCount) || projectImageCount <= 0)
        {
            AddConfigIssue("错误", "ProjectImageCount 必须是大于 0 的整数。");
        }

        if (!int.TryParse(VideoBitrateBps, out var videoBitrate) || videoBitrate <= 0)
        {
            AddConfigIssue("错误", "VideoBitrateBps 必须是有效整数。");
        }
        else if (videoBitrate < MinimumUploadVideoBitrate)
        {
            AddConfigIssue("警告", $"VideoBitrateBps 当前低于视频号建议阈值 {MinimumUploadVideoBitrate}。");
        }

        if (!int.TryParse(VideoAudioBitrateBps, out var audioBitrate) || audioBitrate <= 0)
        {
            AddConfigIssue("警告", "VideoAudioBitrateBps 为空或无效，将使用默认音频码率。");
        }

        if (!int.TryParse(VideoFps, out var fps) || fps <= 0)
        {
            AddConfigIssue("警告", "VideoFps 为空或无效，将使用默认帧率。");
        }

        if (!int.TryParse(VideoConcurrentCount, out var concurrentCount) || concurrentCount <= 0)
        {
            AddConfigIssue("错误", "VideoConcurrentCount 必须是大于 0 的整数。");
        }
        else if (concurrentCount > 4)
        {
            AddConfigIssue("警告", "VideoConcurrentCount 当前大于 4，可能导致机器负载过高。");
        }

        if (string.IsNullOrWhiteSpace(VideoRes))
        {
            AddConfigIssue("警告", "VideoRes 为空，将使用默认分辨率。");
        }

        if (string.IsNullOrWhiteSpace(VideoBitrateMode))
        {
            AddConfigIssue("警告", "VideoBitrateMode 为空，将使用默认码率模式。");
        }

        if (!double.TryParse(MaterialTrimHeadSeconds, out var materialTrimHead) || materialTrimHead < 0)
        {
            AddConfigIssue("错误", "MaterialTrimHeadSeconds 必须是大于等于 0 的数字。");
        }

        if (!double.TryParse(MaterialTrimTailSeconds, out var materialTrimTail) || materialTrimTail < 0)
        {
            AddConfigIssue("错误", "MaterialTrimTailSeconds 必须是大于等于 0 的数字。");
        }

        if (!double.TryParse(MaterialSpeedPercent, out var materialSpeed) || materialSpeed < -50 || materialSpeed > 50)
        {
            AddConfigIssue("错误", "MaterialSpeedPercent 必须在 -50 到 50 之间。");
        }

        if (!int.TryParse(MaterialDropEveryNFrames, out var materialDropEvery) || materialDropEvery <= 1)
        {
            AddConfigIssue("错误", "MaterialDropEveryNFrames 必须是大于 1 的整数。");
        }

        if (!int.TryParse(MaterialDropCount, out var materialDropCount) || materialDropCount <= 0)
        {
            AddConfigIssue("错误", "MaterialDropCount 必须是大于 0 的整数。");
        }
        else if (int.TryParse(MaterialDropEveryNFrames, out materialDropEvery) && materialDropCount >= materialDropEvery)
        {
            AddConfigIssue("错误", "MaterialDropCount 必须小于 MaterialDropEveryNFrames。");
        }

        if (!double.TryParse(MaterialCropWidthPercent, out var materialCropWidth) || materialCropWidth < 0 || materialCropWidth >= 100)
        {
            AddConfigIssue("错误", "MaterialCropWidthPercent 必须在 0 到 100 之间。");
        }

        if (!double.TryParse(MaterialCropHeightPercent, out var materialCropHeight) || materialCropHeight < 0 || materialCropHeight >= 100)
        {
            AddConfigIssue("错误", "MaterialCropHeightPercent 必须在 0 到 100 之间。");
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
            AppendLog($"已同步更新 {updatedCount} 个微信上传配置文件。");
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
                string.Equals(label, "推荐语", StringComparison.Ordinal) &&
                !snapshot.WeixinFillRecommendation)
            {
                actions.RemoveAt(i);
                continue;
            }

            if (string.Equals(type, "choose", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(fieldLabel, "变现类型", StringComparison.Ordinal))
                {
                    action["option_text"] = snapshot.WeixinMonetizationType;
                }
                else if (string.Equals(fieldLabel, "剧目类型", StringComparison.Ordinal))
                {
                    action["option_text"] = snapshot.WeixinDramaType;
                }
                else if (string.Equals(fieldLabel, "剧目资质", StringComparison.Ordinal))
                {
                    action["option_text"] = snapshot.WeixinDramaQualification;
                }
                else if (string.Equals(fieldLabel, "提审身份", StringComparison.Ordinal))
                {
                    action["option_text"] = snapshot.WeixinSubmitterIdentity;
                }
            }
            else if (string.Equals(type, "fill", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(label, "试看集数", StringComparison.Ordinal))
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
        ReplacePosterPreview(FindPreviewPath("海报图片.jpg"));
        ReplaceCostPreview(FindPreviewPath("成本报表.png"));
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

        return Directory.EnumerateFiles(baseDir, "工程图_*.png", SearchOption.TopDirectoryOnly)
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
        var errorCount = ConfigIssues.Count(item => string.Equals(item.Severity, "错误", StringComparison.Ordinal));
        var warningCount = ConfigIssues.Count(item => string.Equals(item.Severity, "警告", StringComparison.Ordinal));

        ConfigValidationSummary = errorCount == 0 && warningCount == 0
            ? "配置校验通过"
            : $"配置校验：错误 {errorCount} 项，警告 {warningCount} 项";
    }

    private void ValidateProjectImageTemplates(string templateDir)
    {
        if (!int.TryParse(ProjectImageCount, out var count) || count <= 0)
        {
            return;
        }

        for (var index = 1; index <= count; index++)
        {
            var templatePath = Path.Combine(templateDir, $"工程图_{index}.png");
            if (!File.Exists(templatePath))
            {
                AddConfigIssue("错误", $"缺少工程图模板: {templatePath}");
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
        await RunSearchBusyAsync($"正在删除归档项目：{target.DisplayName}", async cancellationToken =>
        {
            var result = await _archivedProjectDeleteService.DeleteAsync(
                RootDir,
                target.ArchiveProjectDir,
                cancellationToken);

            AppendLog(result.Message, target.ProjectKey, target.DisplayName, "archive-delete", "删除归档项目");
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

        await RunSearchBusyAsync($"正在删除勾选归档项目，共 {targets.Length} 个...", async cancellationToken =>
        {
            var deleted = 0;
            foreach (var target in targets)
            {
                var result = await _archivedProjectDeleteService.DeleteAsync(
                    RootDir,
                    target.ArchiveProjectDir,
                    cancellationToken);
                deleted++;
                AppendLog(result.Message, target.ProjectKey, target.DisplayName, "archive-delete", "删除归档项目");
            }

            LoadArchivedProjects();
            StatusMessage = $"已删除归档项目 {deleted} 个。";
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
    }

    private void RefreshProjectLogFilters()
    {
        var selectedKey = SelectedProjectLogFilter?.Key ?? AllProjectsFilterKey;
        ProjectLogFilters.Clear();
        ProjectLogFilters.Add(new LogFilterOption(AllProjectsFilterKey, "全部项目"));

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
            "publish_video" => "当前素材",
            "project" => "当前项目",
            _ => "当前节点"
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
