using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Kuaishou.Publishing;
using PlatformPublisher.Weixin.Publishing;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Core.Queue;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly (string Key, string Label)[] WorkflowStepDefinitions =
    [
        ("download", "下载剧集"),
        ("smart-recut", "智能重剪"),
        ("rewrite", "改写信息"),
        ("poster-rename", "生成海报"),
        ("transcode", "素材转码"),
        ("material-auto-repair", "一键修复"),
        ("auto-fill-info", "补齐字段"),
        ("cost-report", "成本报表"),
        ("project-image", "工程图"),
        ("ai-proof", "AI制作证明"),
        ("timestamp-certificate", "可信时间戳"),
        ("upload-remux", "无损重封装"),
        ("material-validate", "素材校验"),
        ("kuaishou-personal-artifacts", "快手个人版产物"),
        ("weixin-upload", "上传剧集"),
        ("shelf", "上架"),
        ("management-sync", "管理同步"),
    ];
    private readonly PublishJobStore _store;
    private readonly PublishAccountStore _accountStore;
    private readonly PlatformPublishCoordinator _coordinator;
    private readonly IWorkflowInteractionService _interactionService;
    private readonly IWorkService _workService;
    private readonly IMaterialValidationService _materialValidationService;
    private readonly IProjectScanner _projectScanner;
    private readonly IProjectArchiveService _projectArchiveService;
    private readonly WeixinWorkflowSettingsStore _workflowSettingsStore;
    private readonly WeixinAutoShelfService _autoShelfService;
    private readonly WeixinSmartRecutService _smartRecutService;
    private readonly WeixinManagementSyncService _managementSyncService;
    private readonly WeixinProofArtifactsService _proofArtifactsService;
    private readonly KuaishouPersonalProjectDataService _kuaishouPersonalProjectDataService;
    private readonly KuaishouPersonalPreparationService _kuaishouPersonalPreparationService;
    private readonly List<PublishJob> _jobs = [];
    private readonly List<PublishAccount> _accounts = [];
    private readonly DispatcherTimer _scheduleTimer;
    private CancellationTokenSource? _operationCts;
    private bool _scheduleTickRunning;
    private WorkflowInteractionRequest? _currentInteractionRequest;
    private string _activeWeixinAccountId = string.Empty;
    private string _activeWeixinAccountSessionDirectory = string.Empty;

    public MainWindowViewModel(
        PublishJobStore store,
        PublishAccountStore accountStore,
        PlatformPublishCoordinator coordinator,
        IWorkflowInteractionService interactionService,
        IWorkService workService,
        IMaterialValidationService materialValidationService,
        IProjectScanner projectScanner,
        IProjectArchiveService projectArchiveService,
        WeixinWorkflowSettingsStore workflowSettingsStore,
        WeixinAutoShelfService autoShelfService,
        WeixinSmartRecutService smartRecutService,
        WeixinManagementSyncService managementSyncService,
        WeixinProofArtifactsService proofArtifactsService,
        KuaishouPersonalProjectDataService kuaishouPersonalProjectDataService,
        KuaishouPersonalPreparationService kuaishouPersonalPreparationService)
    {
        _store = store;
        _accountStore = accountStore;
        _coordinator = coordinator;
        _interactionService = interactionService;
        _workService = workService;
        _materialValidationService = materialValidationService;
        _projectScanner = projectScanner;
        _projectArchiveService = projectArchiveService;
        _workflowSettingsStore = workflowSettingsStore;
        _autoShelfService = autoShelfService;
        _smartRecutService = smartRecutService;
        _managementSyncService = managementSyncService;
        _proofArtifactsService = proofArtifactsService;
        _kuaishouPersonalProjectDataService = kuaishouPersonalProjectDataService;
        _kuaishouPersonalPreparationService = kuaishouPersonalPreparationService;
        Platforms =
        [
            new(PublishPlatform.WeixinChannel, "视频号", "剧集上传、提交与断点恢复"),
            new(PublishPlatform.KuaishouPersonalRevenue, "快手分账 · 个人", "独立个人分账任务通道"),
            new(PublishPlatform.KuaishouEnterpriseRevenue, "快手分账 · 企业", "独立企业分账任务通道"),
        ];
        JobKinds =
        [
            new(PublishJobKind.Series, "剧集上传", "使用项目内剧集配置创建并上传分集"),
            new(PublishJobKind.DirectoryMaterials, "目录批量发表", "每个一级子目录发表一条视频"),
            new(PublishJobKind.SystemHighlight, "系统高光发表", "按剧名选择平台系统高光并发表"),
            new(PublishJobKind.ProjectMaterials, "项目素材发表", "从 material-videos 或 videos 目录发表"),
            new(PublishJobKind.LocalVideos, "本地视频发表", "发表所选目录顶层的视频"),
            new(PublishJobKind.CustomVideos, "自选视频发表", "手工选择一个或多个视频文件"),
        ];
        MaterialJobKinds = JobKinds.Where(item => item.Value != PublishJobKind.Series).ToArray();
        JobStatusChoices =
        [
            new(PublishJobStatus.Pending, "待执行"),
            new(PublishJobStatus.Succeeded, "已完成"),
            new(PublishJobStatus.Failed, "失败"),
            new(PublishJobStatus.Blocked, "已阻止"),
        ];
        _selectedPlatform = Platforms[0];
        _selectedJobKind = JobKinds[0];
        _selectedJobStatusChoice = JobStatusChoices[0];
        AddJobCommand = new AsyncRelayCommand(AddJobAsync, CanAddJob);
        RunSelectedCommand = new AsyncRelayCommand(RunSelectedAsync, CanRunSelected);
        RunRunnableCommand = new AsyncRelayCommand(RunRunnableAsync, CanRunRunnable);
        RetryFailedCommand = new AsyncRelayCommand(RetryFailedAsync, CanRetryFailed);
        NewAccountCommand = new RelayCommand(BeginNewAccount, () => !IsBusy);
        SaveAccountCommand = new AsyncRelayCommand(SaveAccountAsync, CanSaveAccount);
        DeleteAccountCommand = new AsyncRelayCommand(DeleteAccountAsync, () => SelectedAccount is not null && !IsBusy);
        OpenLoginCommand = new AsyncRelayCommand(OpenLoginAsync, CanOpenLogin);
        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => SelectedJob is not null && !IsBusy);
        StopCommand = new RelayCommand(Stop, () => IsBusy);
        TakeoverInteractionCommand = new RelayCommand(() => ResolveInteraction("manual"), () => CanResolveInteraction("manual"));
        ResumeInteractionCommand = new RelayCommand(() => ResolveInteraction("resume"), () => CanResolveInteraction("resume"));
        SkipCurrentVideoCommand = new RelayCommand(() => ResolveInteraction("skip_video"), () => CanResolveInteraction("skip_video"));
        SkipCurrentProjectCommand = new RelayCommand(() => ResolveInteraction("skip_project"), () => CanResolveInteraction("skip_project"));
        StopInteractionCommand = new RelayCommand(() => ResolveInteraction("stop"), () => CanResolveInteraction("stop"));
        ClearActivityLogsCommand = new RelayCommand(ActivityLogs.Clear);
        RunSharedPipelineCommand = new AsyncRelayCommand(RunSharedPipelineAsync, CanRunSharedPipeline);
        ScanWorkspaceCommand = new AsyncRelayCommand(ScanWorkspaceAsync, CanScanWorkspace);
        RefreshArchivedProjectsCommand = new RelayCommand(RefreshArchivedProjects);
        ResetSelectedJobCommand = new AsyncRelayCommand(ResetSelectedJobAsync, () => SelectedJob is not null && !IsBusy);
        SaveWorkflowSettingsCommand = new AsyncRelayCommand(SaveWorkflowSettingsAsync, () => !IsBusy);
        CreateNamedProjectCommand = new AsyncRelayCommand(CreateNamedProjectAsync, CanCreateNamedProject);
        RunCheckedCommand = new AsyncRelayCommand(RunCheckedAsync, () => !IsBusy);
        CheckAllVisibleCommand = new AsyncRelayCommand(() => SetVisibleCheckedAsync(_ => true), () => !IsBusy);
        CheckPendingCommand = new AsyncRelayCommand(() => SetVisibleCheckedAsync(row => row.Model.Status is PublishJobStatus.Pending or PublishJobStatus.Failed), () => !IsBusy);
        UncheckAllVisibleCommand = new AsyncRelayCommand(() => SetVisibleCheckedAsync(_ => false), () => !IsBusy);
        ResetCheckedJobsCommand = new AsyncRelayCommand(ResetCheckedJobsAsync, () => !IsBusy);
        RemoveCheckedJobsCommand = new AsyncRelayCommand(RemoveCheckedJobsAsync, () => !IsBusy);
        CheckToCurrentCommand = new AsyncRelayCommand(CheckToCurrentAsync, () => !IsBusy);
        CheckCompletedCommand = new AsyncRelayCommand(() => SetVisibleCheckedAsync(row => row.Model.Status == PublishJobStatus.Succeeded), () => !IsBusy);
        ApplyCheckedStatusCommand = new AsyncRelayCommand(ApplyCheckedStatusAsync, () => !IsBusy);
        AssignCheckedToCurrentAccountCommand = new AsyncRelayCommand(AssignCheckedToCurrentAccountAsync, () => !IsBusy);
        PreviousWorkflowPageCommand = new RelayCommand(PreviousWorkflowPage, () => WorkflowCurrentPage > 1);
        NextWorkflowPageCommand = new RelayCommand(NextWorkflowPage, () => WorkflowCurrentPage < WorkflowPageCount);
        AutoShelfCommand = new AsyncRelayCommand(AutoShelfAsync, () => SelectedJob is not null && !IsBusy);
        SyncCheckedManagementCommand = new AsyncRelayCommand(SyncCheckedManagementAsync, () => !IsBusy);
        _interactionService.RequestChanged += OnInteractionRequestChanged;
        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _scheduleTimer.Tick += OnScheduleTimerTick;
        _scheduleTimer.Start();
        _ = LoadAsync();
    }

    public IReadOnlyList<PlatformOptionViewModel> Platforms { get; }
    public IReadOnlyList<PublishJobKindOptionViewModel> JobKinds { get; }
    public IReadOnlyList<PublishJobKindOptionViewModel> MaterialJobKinds { get; }
    public IReadOnlyList<PublishJobStatusOptionViewModel> JobStatusChoices { get; }
    public ObservableCollection<PublishJobRowViewModel> VisibleJobs { get; } = [];
    public ObservableCollection<PublishJobRowViewModel> PagedJobs { get; } = [];
    public ObservableCollection<PublishAccountItemViewModel> VisibleAccounts { get; } = [];
    public ObservableCollection<string> ActivityLogs { get; } = [];
    public ObservableCollection<ArchivedProjectRowViewModel> ArchivedProjects { get; } = [];
    public IAsyncRelayCommand AddJobCommand { get; }
    public IAsyncRelayCommand RunSelectedCommand { get; }
    public IAsyncRelayCommand RunRunnableCommand { get; }
    public IAsyncRelayCommand RetryFailedCommand { get; }
    public IRelayCommand NewAccountCommand { get; }
    public IAsyncRelayCommand SaveAccountCommand { get; }
    public IAsyncRelayCommand DeleteAccountCommand { get; }
    public IAsyncRelayCommand OpenLoginCommand { get; }
    public IAsyncRelayCommand RemoveSelectedCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IRelayCommand TakeoverInteractionCommand { get; }
    public IRelayCommand ResumeInteractionCommand { get; }
    public IRelayCommand SkipCurrentVideoCommand { get; }
    public IRelayCommand SkipCurrentProjectCommand { get; }
    public IRelayCommand StopInteractionCommand { get; }
    public IRelayCommand ClearActivityLogsCommand { get; }
    public IAsyncRelayCommand RunSharedPipelineCommand { get; }
    public IAsyncRelayCommand ScanWorkspaceCommand { get; }
    public IRelayCommand RefreshArchivedProjectsCommand { get; }
    public IAsyncRelayCommand ResetSelectedJobCommand { get; }
    public IAsyncRelayCommand SaveWorkflowSettingsCommand { get; }
    public IAsyncRelayCommand CreateNamedProjectCommand { get; }
    public IAsyncRelayCommand RunCheckedCommand { get; }
    public IAsyncRelayCommand CheckAllVisibleCommand { get; }
    public IAsyncRelayCommand CheckPendingCommand { get; }
    public IAsyncRelayCommand UncheckAllVisibleCommand { get; }
    public IAsyncRelayCommand ResetCheckedJobsCommand { get; }
    public IAsyncRelayCommand RemoveCheckedJobsCommand { get; }
    public IAsyncRelayCommand CheckToCurrentCommand { get; }
    public IAsyncRelayCommand CheckCompletedCommand { get; }
    public IAsyncRelayCommand ApplyCheckedStatusCommand { get; }
    public IAsyncRelayCommand AssignCheckedToCurrentAccountCommand { get; }
    public IRelayCommand PreviousWorkflowPageCommand { get; }
    public IRelayCommand NextWorkflowPageCommand { get; }
    public IAsyncRelayCommand AutoShelfCommand { get; }
    public IAsyncRelayCommand SyncCheckedManagementCommand { get; }

    [ObservableProperty]
    private PlatformOptionViewModel _selectedPlatform;

    [ObservableProperty]
    private PublishJobKindOptionViewModel _selectedJobKind;

    [ObservableProperty]
    private PublishJobRowViewModel? _selectedJob;

    [ObservableProperty]
    private PublishJobStatusOptionViewModel _selectedJobStatusChoice;

    [ObservableProperty]
    private PublishAccountItemViewModel? _selectedAccount;

    [ObservableProperty]
    private string _draftProjectDirectory = string.Empty;

    [ObservableProperty]
    private string _draftConfigPath = string.Empty;

    [ObservableProperty]
    private string _draftAccountName = string.Empty;

    [ObservableProperty]
    private bool _draftDeclareOriginal = true;

    [ObservableProperty]
    private bool _draftHideLocation = true;

    [ObservableProperty]
    private bool _draftAllowDuplicatePublish;

    [ObservableProperty]
    private bool _draftScheduleEnabled;

    [ObservableProperty]
    private string _draftScheduleText = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm");

    [ObservableProperty]
    private string _draftDramaTitle = string.Empty;

    [ObservableProperty]
    private int _draftPublishCount = 1;

    [ObservableProperty]
    private string _draftPublishVideoTypes = "混剪,解说,切片";

    [ObservableProperty]
    private bool _draftRegenerateHighlightsAfterPublish;

    [ObservableProperty]
    private string _draftPublishDescription = "热门短剧，精彩内容持续更新。";

    [ObservableProperty]
    private string _draftCustomVideoFilesText = string.Empty;

    [ObservableProperty]
    private string _draftPlatformOptionsJson = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "多平台发布助手已启动，数据与 TikTok 助手完全隔离。";

    [ObservableProperty]
    private string _activeWeixinAccountName = "请先在左侧选择账号";

    public bool HasActiveWeixinAccount => !string.IsNullOrWhiteSpace(_activeWeixinAccountId);

    [ObservableProperty] private bool _pipelineDownloadEnabled;
    [ObservableProperty] private bool _pipelineRewriteEnabled = true;
    [ObservableProperty] private bool _pipelinePosterEnabled = true;
    [ObservableProperty] private bool _pipelineTranscodeEnabled = true;
    [ObservableProperty] private bool _pipelineAutoRepairEnabled = true;
    [ObservableProperty] private bool _pipelineAutoFillEnabled = true;
    [ObservableProperty] private bool _pipelineCostReportEnabled = true;
    [ObservableProperty] private bool _pipelineProjectImageEnabled = true;
    [ObservableProperty] private bool _pipelineMaterialValidateEnabled = true;
    [ObservableProperty] private bool _pipelineRemuxEnabled;
    [ObservableProperty] private bool _pipelineForceRerun;
    [ObservableProperty] private bool _pipelineAutoArchiveAfterUpload;
    [ObservableProperty] private bool _pipelinePreferUploadWhenReady = true;

    [ObservableProperty] private string _archiveRootDirectory = string.Empty;
    [ObservableProperty] private ArchivedProjectRowViewModel? _selectedArchivedProject;
    [ObservableProperty] private string _workflowFilterText = string.Empty;
    [ObservableProperty] private string _draftNewProjectName = string.Empty;
    [ObservableProperty] private int _workflowPageSize = 20;
    [ObservableProperty] private int _workflowCurrentPage = 1;
    [ObservableProperty] private int _autoShelfMaxPages = 10;
    [ObservableProperty] private int _autoShelfMaxRounds = 20;
    [ObservableProperty] private bool _pipelineSmartRecutEnabled;
    [ObservableProperty] private int _smartRecutEpisodeCount;
    [ObservableProperty] private int _smartRecutMinSeconds = 60;
    [ObservableProperty] private int _smartRecutMaxSeconds = 180;
    [ObservableProperty] private bool _pipelineSyncManagementAfterUpload;
    [ObservableProperty] private bool _pipelineGenerateAiProofEnabled;
    [ObservableProperty] private bool _pipelineGenerateTimestampCertificateEnabled;

    public int WorkflowPageCount => Math.Max(1, (int)Math.Ceiling(VisibleJobs.Count / (double)Math.Max(1, WorkflowPageSize)));
    public string WorkflowPageSummary => $"第 {WorkflowCurrentPage}/{WorkflowPageCount} 页，共 {VisibleJobs.Count} 条";

    public string ArchivedProjectsSummary => $"已归档 {ArchivedProjects.Count} 个项目";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasInteractionRequest;

    [ObservableProperty]
    private string _interactionTitle = "人工介入";

    [ObservableProperty]
    private string _interactionMessage = string.Empty;

    public string SelectedPlatformCapability =>
        _coordinator.GetAdapter(SelectedPlatform.Value).AvailabilityMessage;

    public string QueueSummary =>
        $"当前平台 {VisibleJobs.Count} 条任务、{VisibleAccounts.Count} 个账号，共 {_jobs.Count} 条独立任务";

    public void SelectPlatform(PublishPlatform platform)
    {
        var option = Platforms.FirstOrDefault(item => item.Value == platform);
        if (option is not null)
            SelectedPlatform = option;
    }

    public void UseWeixinAccount(string? accountId, string? accountName, string? sessionDirectory)
    {
        _activeWeixinAccountId = accountId?.Trim() ?? string.Empty;
        _activeWeixinAccountSessionDirectory = sessionDirectory?.Trim() ?? string.Empty;
        ActiveWeixinAccountName = string.IsNullOrWhiteSpace(accountName)
            ? "请先在左侧选择账号"
            : accountName.Trim();
        if (!string.IsNullOrWhiteSpace(accountName))
            DraftAccountName = accountName.Trim();
        OnPropertyChanged(nameof(HasActiveWeixinAccount));
        AddJobCommand.NotifyCanExecuteChanged();
        ScanWorkspaceCommand.NotifyCanExecuteChanged();
        CreateNamedProjectCommand.NotifyCanExecuteChanged();
    }

    public bool IsSystemHighlightKind => SelectedJobKind.Value == PublishJobKind.SystemHighlight;
    public bool IsWeixinPlatform => SelectedPlatform.Value == PublishPlatform.WeixinChannel;
    public bool IsKuaishouPersonalPlatform => SelectedPlatform.Value == PublishPlatform.KuaishouPersonalRevenue;
    public bool IsCustomVideoKind => SelectedJobKind.Value == PublishJobKind.CustomVideos;
    public bool IsStandardMaterialKind => SelectedJobKind.Value is
        PublishJobKind.ProjectMaterials or PublishJobKind.LocalVideos or PublishJobKind.CustomVideos;

    private bool _materialWorkflowActive;

    public void ActivateSeriesWorkflow()
    {
        _materialWorkflowActive = false;
        SelectedJobKind = JobKinds.First(item => item.Value == PublishJobKind.Series);
        RefreshVisibleJobs();
    }

    public void ActivateMaterialWorkflow()
    {
        _materialWorkflowActive = true;
        if (SelectedJobKind.Value == PublishJobKind.Series)
            SelectedJobKind = MaterialJobKinds[0];
        RefreshVisibleJobs();
    }

    public void SelectMaterialJobKind(PublishJobKind kind)
    {
        var option = MaterialJobKinds.FirstOrDefault(item => item.Value == kind);
        if (option is not null) SelectedJobKind = option;
    }

    partial void OnSelectedPlatformChanged(PlatformOptionViewModel value)
    {
        RefreshVisibleJobs();
        RefreshVisibleAccounts();
        OnPropertyChanged(nameof(SelectedPlatformCapability));
        OnPropertyChanged(nameof(IsWeixinPlatform));
        OnPropertyChanged(nameof(IsKuaishouPersonalPlatform));
        NotifyCommands();
    }

    public async Task UpdateSelectedAccountConfigPathAsync(string configPath)
    {
        if (SelectedAccount is null) throw new InvalidOperationException("请先在左侧选择快手分账个人版账号。");
        SelectedAccount.Model.BaseConfigPath = Path.GetFullPath(configPath);
        DraftConfigPath = SelectedAccount.Model.BaseConfigPath;
        await PersistAsync();
        RefreshVisibleAccounts(SelectedAccount.Model.Id);
        StatusMessage = $"快手分账个人版配置已保存：{SelectedAccount.Model.BaseConfigPath}";
    }

    partial void OnSelectedJobChanged(PublishJobRowViewModel? value) => NotifyCommands();
    partial void OnWorkflowFilterTextChanged(string value) => RefreshVisibleJobs(SelectedJob?.Id);
    partial void OnWorkflowPageSizeChanged(int value)
    {
        var normalized = Math.Clamp(value, 5, 200);
        if (normalized != value)
        {
            WorkflowPageSize = normalized;
            return;
        }
        WorkflowCurrentPage = 1;
        RefreshPagedJobs();
    }
    partial void OnSelectedAccountChanged(PublishAccountItemViewModel? value)
    {
        if (value is not null)
        {
            DraftAccountName = value.Model.Name;
            DraftConfigPath = value.Model.BaseConfigPath;
        }
        NotifyCommands();
    }
    partial void OnSelectedJobKindChanged(PublishJobKindOptionViewModel value)
    {
        OnPropertyChanged(nameof(IsSystemHighlightKind));
        OnPropertyChanged(nameof(IsCustomVideoKind));
        OnPropertyChanged(nameof(IsStandardMaterialKind));
        AddJobCommand.NotifyCanExecuteChanged();
    }
    partial void OnDraftProjectDirectoryChanged(string value)
    {
        AddJobCommand.NotifyCanExecuteChanged();
        RunSharedPipelineCommand.NotifyCanExecuteChanged();
        ScanWorkspaceCommand.NotifyCanExecuteChanged();
        CreateNamedProjectCommand.NotifyCanExecuteChanged();
    }
    partial void OnDraftDramaTitleChanged(string value) => AddJobCommand.NotifyCanExecuteChanged();
    partial void OnDraftAccountNameChanged(string value) => SaveAccountCommand.NotifyCanExecuteChanged();
    partial void OnDraftScheduleEnabledChanged(bool value) => AddJobCommand.NotifyCanExecuteChanged();
    partial void OnDraftScheduleTextChanged(string value) => AddJobCommand.NotifyCanExecuteChanged();
    partial void OnDraftCustomVideoFilesTextChanged(string value) => AddJobCommand.NotifyCanExecuteChanged();
    partial void OnDraftNewProjectNameChanged(string value) => CreateNamedProjectCommand.NotifyCanExecuteChanged();
    partial void OnPipelineDownloadEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineSmartRecutEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineRewriteEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelinePosterEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineTranscodeEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineAutoRepairEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineAutoFillEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineCostReportEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineProjectImageEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineGenerateAiProofEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineGenerateTimestampCertificateEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineMaterialValidateEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineRemuxEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();

    private async Task LoadAsync()
    {
        try
        {
            var loadJobs = _store.LoadAsync();
            var loadAccounts = _accountStore.LoadAsync();
            var loadWorkflowSettings = _workflowSettingsStore.LoadAsync();
            await Task.WhenAll(loadJobs, loadAccounts, loadWorkflowSettings);
            _jobs.AddRange(await loadJobs);
            var recoveredSteps = 0;
            foreach (var job in _jobs)
            {
                EnsureStepStates(job);
                foreach (var step in job.StepStates.Values.Where(step => step.Status == PublishJobStepStatus.Running))
                {
                    step.Status = PublishJobStepStatus.Pending;
                    step.Message = "上次运行意外中断，已恢复为待执行";
                    step.UpdatedAt = DateTimeOffset.Now;
                    recoveredSteps++;
                }
            }
            _accounts.AddRange(await loadAccounts);
            ApplyWorkflowSettings(await loadWorkflowSettings);
            var recovered = PublishSchedulePolicy.RecoverInterrupted(_jobs);
            if (recovered > 0 || recoveredSteps > 0)
            {
                await PersistAsync();
                StatusMessage = $"已恢复 {recovered} 条任务、{recoveredSteps} 个中断步骤。";
            }
            RefreshVisibleJobs();
            RefreshVisibleAccounts();
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取独立任务队列失败：{ex.Message}";
        }
    }

    private bool CanAddJob() =>
        !IsBusy &&
        (SelectedPlatform.Value != PublishPlatform.WeixinChannel || HasActiveWeixinAccount || SelectedAccount is not null) &&
        HasValidDraftSource() &&
        (SelectedJobKind.Value != PublishJobKind.SystemHighlight || !string.IsNullOrWhiteSpace(DraftDramaTitle)) &&
        (!DraftScheduleEnabled || PublishSchedulePolicy.TryParseLocal(DraftScheduleText, out _));

    private bool HasValidDraftSource()
    {
        if (SelectedJobKind.Value != PublishJobKind.CustomVideos)
            return Directory.Exists(DraftProjectDirectory);

        return ParseCustomVideoFiles().Any(File.Exists);
    }

    private async Task AddJobAsync()
    {
        var customVideoFiles = ParseCustomVideoFiles().Where(File.Exists).Select(Path.GetFullPath).ToList();
        var directory = Directory.Exists(DraftProjectDirectory)
            ? Path.GetFullPath(DraftProjectDirectory)
            : Path.GetDirectoryName(customVideoFiles.First())!;
        var adapter = _coordinator.GetAdapter(SelectedPlatform.Value);
        DateTimeOffset? scheduledAt = null;
        if (DraftScheduleEnabled)
        {
            if (!PublishSchedulePolicy.TryParseLocal(DraftScheduleText, out var parsedSchedule))
            {
                StatusMessage = "定时时间格式无效，请使用 yyyy-MM-dd HH:mm。";
                return;
            }

            scheduledAt = parsedSchedule;
        }

        var job = new PublishJob
        {
            Platform = SelectedPlatform.Value,
            Kind = SelectedJobKind.Value,
            ProjectName = SelectedJobKind.Value == PublishJobKind.SystemHighlight
                ? DraftDramaTitle.Trim()
                : Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            ProjectDirectory = directory,
            ConfigPath = DraftConfigPath.Trim(),
            AccountId = SelectedPlatform.Value == PublishPlatform.WeixinChannel && HasActiveWeixinAccount
                ? _activeWeixinAccountId
                : SelectedAccount?.Model.Id ?? string.Empty,
            AccountName = SelectedPlatform.Value == PublishPlatform.WeixinChannel && HasActiveWeixinAccount
                ? ActiveWeixinAccountName
                : DraftAccountName.Trim(),
            AccountSessionDirectory = SelectedPlatform.Value == PublishPlatform.WeixinChannel && HasActiveWeixinAccount
                ? _activeWeixinAccountSessionDirectory
                : string.Empty,
            DeclareOriginal = DraftDeclareOriginal,
            HideLocation = DraftHideLocation,
            AllowDuplicatePublish = DraftAllowDuplicatePublish,
            DramaTitle = DraftDramaTitle.Trim(),
            PublishCount = Math.Clamp(DraftPublishCount, 1, 100),
            PublishVideoTypes = DraftPublishVideoTypes.Trim(),
            RegenerateHighlightsAfterPublish = DraftRegenerateHighlightsAfterPublish,
            PublishDescription = DraftPublishDescription.Trim(),
            CustomVideoFiles = customVideoFiles,
            PlatformOptionsJson = DraftPlatformOptionsJson,
            ScheduledAt = scheduledAt,
            Status = adapter.IsAvailable ? PublishJobStatus.Pending : PublishJobStatus.Blocked,
            StatusMessage = adapter.IsAvailable
                ? scheduledAt is null ? "等待执行" : $"计划于 {scheduledAt:yyyy-MM-dd HH:mm} 执行"
                : adapter.AvailabilityMessage,
        };
        EnsureStepStates(job);
        _jobs.Add(job);
        await PersistAsync();
        RefreshVisibleJobs(job.Id);
        StatusMessage = $"已加入{job.Platform.DisplayName()}任务：{job.ProjectName}";
        AppendActivityLog(StatusMessage);
    }

    private IReadOnlyList<string> ParseCustomVideoFiles() =>
        DraftCustomVideoFilesText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private bool CanCreateNamedProject() =>
        !IsBusy && HasActiveWeixinAccount && Directory.Exists(DraftProjectDirectory) &&
        IsValidProjectDirectoryName(DraftNewProjectName);

    private async Task CreateNamedProjectAsync()
    {
        var directory = Path.Combine(Path.GetFullPath(DraftProjectDirectory), DraftNewProjectName.Trim());
        Directory.CreateDirectory(directory);
        var added = await AddImportedProjectJobsAsync([directory]);
        StatusMessage = added > 0
            ? $"已按剧名创建项目：{DraftNewProjectName.Trim()}"
            : $"项目已存在于队列：{DraftNewProjectName.Trim()}";
        AppendActivityLog(StatusMessage);
        DraftNewProjectName = string.Empty;
    }

    private static void EnsureStepStates(PublishJob job)
    {
        job.StepStates ??= new Dictionary<string, PublishJobStepState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, label) in WorkflowStepDefinitions)
        {
            if (job.StepStates.ContainsKey(key)) continue;
            job.StepStates[key] = new PublishJobStepState { Key = key, Label = label };
        }
    }

    public async Task<int> ImportLocalProjectDirectoriesAsync(IEnumerable<string> directories)
    {
        if (!HasActiveWeixinAccount)
        {
            StatusMessage = "请先在左侧选择视频号账号。";
            return 0;
        }
        var added = await AddImportedProjectJobsAsync(directories);
        StatusMessage = $"导入本地项目完成：新增 {added} 个任务。";
        AppendActivityLog(StatusMessage);
        return added;
    }

    private async Task<int> AddImportedProjectJobsAsync(IEnumerable<string> directories)
    {
        var existing = _jobs
            .Where(job => job.Platform == PublishPlatform.WeixinChannel && job.Kind == PublishJobKind.Series)
            .Where(job => !string.IsNullOrWhiteSpace(job.ProjectDirectory))
            .Select(job => Path.GetFullPath(job.ProjectDirectory))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var rawDirectory in directories)
        {
            if (string.IsNullOrWhiteSpace(rawDirectory) || !Directory.Exists(rawDirectory)) continue;
            var directory = Path.GetFullPath(rawDirectory);
            if (!existing.Add(directory)) continue;
            var job = new PublishJob
            {
                Platform = PublishPlatform.WeixinChannel,
                Kind = PublishJobKind.Series,
                ProjectName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                ProjectDirectory = directory,
                ConfigPath = ResolveScannedWeixinConfig(directory),
                AccountId = _activeWeixinAccountId,
                AccountName = ActiveWeixinAccountName,
                AccountSessionDirectory = _activeWeixinAccountSessionDirectory,
                Status = PublishJobStatus.Pending,
                StatusMessage = "本地导入，等待执行",
            };
            EnsureStepStates(job);
            _jobs.Add(job);
            added++;
        }
        if (added > 0)
        {
            await PersistAsync();
            RefreshVisibleJobs();
        }
        return added;
    }

    private static bool IsValidProjectDirectoryName(string? value)
    {
        var name = value?.Trim();
        return !string.IsNullOrWhiteSpace(name) && name is not "." and not ".." &&
               name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
               !name.Contains(Path.DirectorySeparatorChar) && !name.Contains(Path.AltDirectorySeparatorChar);
    }

    private bool CanRunSharedPipeline() =>
        !IsBusy &&
        Directory.Exists(DraftProjectDirectory) &&
        (PipelineDownloadEnabled || PipelineSmartRecutEnabled || PipelineRewriteEnabled || PipelinePosterEnabled ||
         PipelineTranscodeEnabled || PipelineAutoRepairEnabled || PipelineAutoFillEnabled ||
         PipelineCostReportEnabled || PipelineProjectImageEnabled || PipelineGenerateAiProofEnabled ||
         PipelineGenerateTimestampCertificateEnabled || PipelineMaterialValidateEnabled ||
         PipelineRemuxEnabled);

    private bool CanScanWorkspace() =>
        !IsBusy && HasActiveWeixinAccount && Directory.Exists(DraftProjectDirectory);

    private async Task ScanWorkspaceAsync()
    {
        var rootDirectory = Path.GetFullPath(DraftProjectDirectory);
        ArchiveRootDirectory = rootDirectory;
        await RunBusyAsync(async cancellationToken =>
        {
            try
            {
                StatusMessage = $"正在扫描视频号工作目录：{rootDirectory}";
                AppendActivityLog(StatusMessage);
                var result = await _projectScanner.ScanAsync(rootDirectory, null, cancellationToken);
                var existing = _jobs
                    .Where(job => job.Platform == PublishPlatform.WeixinChannel && job.Kind == PublishJobKind.Series)
                    .Select(job => Path.GetFullPath(job.ProjectDirectory))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var added = 0;
                foreach (var project in result.Projects)
                {
                    var sourceDirectory = Path.GetFullPath(project.SourceProjectDir);
                    if (!existing.Add(sourceDirectory)) continue;
                    var job = new PublishJob
                    {
                        Platform = PublishPlatform.WeixinChannel,
                        Kind = PublishJobKind.Series,
                        ProjectName = project.DisplayName,
                        ProjectDirectory = sourceDirectory,
                        ConfigPath = ResolveScannedWeixinConfig(project.WorkflowProjectDir),
                        AccountId = _activeWeixinAccountId,
                        AccountName = ActiveWeixinAccountName,
                        AccountSessionDirectory = _activeWeixinAccountSessionDirectory,
                        Status = PublishJobStatus.Pending,
                        StatusMessage = "扫描导入，等待执行",
                    };
                    EnsureStepStates(job);
                    _jobs.Add(job);
                    added++;
                }
                await PersistAsync(cancellationToken);
                RefreshVisibleJobs();
                StatusMessage = $"扫描完成：发现 {result.TotalProjects} 个项目，新增 {added} 个任务。";
                AppendActivityLog(StatusMessage);
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "扫描工作目录已停止。";
                AppendActivityLog(StatusMessage);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StatusMessage = $"扫描工作目录失败：{ex.Message}";
                AppendActivityLog(StatusMessage);
            }
        });
    }

    private void ApplyWorkflowSettings(WeixinWorkflowSettings settings)
    {
        DraftProjectDirectory = settings.LastWorkspaceDirectory;
        ArchiveRootDirectory = settings.ArchiveRootDirectory;
        PipelineDownloadEnabled = settings.DownloadEnabled;
        PipelineSmartRecutEnabled = settings.SmartRecutEnabled;
        SmartRecutEpisodeCount = Math.Clamp(settings.SmartRecutEpisodeCount, 0, 100);
        SmartRecutMinSeconds = Math.Clamp(settings.SmartRecutMinSeconds, 30, 1800);
        SmartRecutMaxSeconds = Math.Clamp(settings.SmartRecutMaxSeconds, SmartRecutMinSeconds, 3600);
        PipelineRewriteEnabled = settings.RewriteEnabled;
        PipelinePosterEnabled = settings.PosterEnabled;
        PipelineTranscodeEnabled = settings.TranscodeEnabled;
        PipelineAutoRepairEnabled = settings.AutoRepairEnabled;
        PipelineAutoFillEnabled = settings.AutoFillEnabled;
        PipelineCostReportEnabled = settings.CostReportEnabled;
        PipelineProjectImageEnabled = settings.ProjectImageEnabled;
        PipelineMaterialValidateEnabled = settings.MaterialValidateEnabled;
        PipelineRemuxEnabled = settings.RemuxEnabled;
        PipelineForceRerun = settings.ForceRerun;
        PipelineAutoArchiveAfterUpload = settings.AutoArchiveAfterUpload;
        PipelinePreferUploadWhenReady = settings.PreferUploadWhenReady;
        PipelineSyncManagementAfterUpload = settings.SyncManagementAfterUpload;
        PipelineGenerateAiProofEnabled = settings.GenerateAiProofEnabled;
        PipelineGenerateTimestampCertificateEnabled = settings.GenerateTimestampCertificateEnabled;
        WorkflowPageSize = Math.Clamp(settings.PageSize, 5, 200);
        AutoShelfMaxPages = Math.Clamp(settings.AutoShelfMaxPages, 1, 100);
        AutoShelfMaxRounds = Math.Clamp(settings.AutoShelfMaxRounds, 1, 100);
        RefreshArchivedProjects();
    }

    private async Task SaveWorkflowSettingsAsync()
    {
        await _workflowSettingsStore.SaveAsync(new WeixinWorkflowSettings
        {
            LastWorkspaceDirectory = DraftProjectDirectory,
            ArchiveRootDirectory = ArchiveRootDirectory,
            DownloadEnabled = PipelineDownloadEnabled,
            SmartRecutEnabled = PipelineSmartRecutEnabled,
            SmartRecutEpisodeCount = SmartRecutEpisodeCount,
            SmartRecutMinSeconds = SmartRecutMinSeconds,
            SmartRecutMaxSeconds = SmartRecutMaxSeconds,
            RewriteEnabled = PipelineRewriteEnabled,
            PosterEnabled = PipelinePosterEnabled,
            TranscodeEnabled = PipelineTranscodeEnabled,
            AutoRepairEnabled = PipelineAutoRepairEnabled,
            AutoFillEnabled = PipelineAutoFillEnabled,
            CostReportEnabled = PipelineCostReportEnabled,
            ProjectImageEnabled = PipelineProjectImageEnabled,
            MaterialValidateEnabled = PipelineMaterialValidateEnabled,
            RemuxEnabled = PipelineRemuxEnabled,
            ForceRerun = PipelineForceRerun,
            AutoArchiveAfterUpload = PipelineAutoArchiveAfterUpload,
            PreferUploadWhenReady = PipelinePreferUploadWhenReady,
            SyncManagementAfterUpload = PipelineSyncManagementAfterUpload,
            GenerateAiProofEnabled = PipelineGenerateAiProofEnabled,
            GenerateTimestampCertificateEnabled = PipelineGenerateTimestampCertificateEnabled,
            PageSize = WorkflowPageSize,
            AutoShelfMaxPages = AutoShelfMaxPages,
            AutoShelfMaxRounds = AutoShelfMaxRounds,
        });
        StatusMessage = "视频号项目流水线设置已保存。";
        AppendActivityLog(StatusMessage);
    }

    private static string ResolveScannedWeixinConfig(string? workflowProjectDirectory)
    {
        if (string.IsNullOrWhiteSpace(workflowProjectDirectory) || !Directory.Exists(workflowProjectDirectory))
            return string.Empty;
        foreach (var name in new[] { "weixin-channel-autogen.json", "weixin-channel-submit.json", "weixin-channel-config.json" })
        {
            var path = Path.Combine(workflowProjectDirectory, name);
            if (File.Exists(path)) return path;
        }
        return string.Empty;
    }

    private async Task RunSharedPipelineAsync()
    {
        var projectDirectory = Path.GetFullPath(DraftProjectDirectory);
        var trackedJob = _jobs.FirstOrDefault(job =>
            !string.IsNullOrWhiteSpace(job.ProjectDirectory) &&
            string.Equals(Path.GetFullPath(job.ProjectDirectory), projectDirectory, StringComparison.OrdinalIgnoreCase));
        if (trackedJob is not null) EnsureStepStates(trackedJob);
        var trackedRow = trackedJob is null ? null : VisibleJobs.FirstOrDefault(row => row.Id == trackedJob.Id);
        var steps = new List<(string Key, string Label)>();
        if (PipelineDownloadEnabled) steps.Add(("download", "下载剧集"));
        if (PipelineSmartRecutEnabled) steps.Add(("smart-recut", "智能重剪"));
        if (PipelineRewriteEnabled) steps.Add(("rewrite", "改写信息"));
        if (PipelinePosterEnabled) steps.Add(("poster-rename", "生成海报"));
        if (PipelineTranscodeEnabled && !PipelineSmartRecutEnabled) steps.Add(("transcode", "素材转码"));
        if (PipelineCostReportEnabled) steps.Add(("cost-report", "生成成本报表"));
        if (PipelineProjectImageEnabled) steps.Add(("project-image", "生成工程图"));
        if (PipelineGenerateAiProofEnabled) steps.Add(("ai-proof", "生成AI制作证明"));
        if (PipelineGenerateTimestampCertificateEnabled) steps.Add(("timestamp-certificate", "生成可信时间戳"));

        var runtimeSettings = ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);
        var runtimeConfigPath = PrepareWeixinRuntimeConfig(runtimeSettings, projectDirectory, ActiveWeixinAccountName);

        try
        {
            await RunBusyAsync(async cancellationToken =>
            {
                var progress = new Progress<WorkRunEvent>(item =>
                {
                    if (string.IsNullOrWhiteSpace(item.Message)) return;
                    StatusMessage = $"[{item.DisplayName}] {item.Message}";
                    AppendActivityLog(StatusMessage);
                });

                try
                {
                    foreach (var (key, label) in steps)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await RunTrackedStepAsync(trackedJob, trackedRow, key, label, PipelineForceRerun, async () =>
                        {
                            if (key == "smart-recut")
                            {
                                await _smartRecutService.RunAsync(
                                    projectDirectory,
                                    SmartRecutEpisodeCount,
                                    SmartRecutMinSeconds,
                                    SmartRecutMaxSeconds,
                                    PipelineForceRerun,
                                    new Progress<string>(message => AppendActivityLog($"智能重剪：{message}")),
                                    cancellationToken);
                                await UpdateStepStateAsync(
                                    trackedJob,
                                    trackedRow,
                                    "transcode",
                                    "素材转码",
                                    PublishJobStepStatus.Skipped,
                                    "智能重剪已直接生成工作视频");
                                return;
                            }
                            if (key == "ai-proof" || key == "timestamp-certificate")
                            {
                                var settings = ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);
                                var artifactProgress = new Progress<string>(message => AppendActivityLog($"证明材料：{message}"));
                                if (key == "ai-proof")
                                    await _proofArtifactsService.GenerateAiProofAsync(
                                        trackedJob ?? new PublishJob { ProjectDirectory = projectDirectory, ProjectName = Path.GetFileName(projectDirectory) },
                                        settings,
                                        PipelineForceRerun,
                                        artifactProgress,
                                        cancellationToken);
                                else
                                    await _proofArtifactsService.GenerateTimestampCertificateAsync(
                                        trackedJob ?? new PublishJob { ProjectDirectory = projectDirectory, ProjectName = Path.GetFileName(projectDirectory) },
                                        settings,
                                        PipelineForceRerun,
                                        artifactProgress,
                                        cancellationToken);
                                return;
                            }
                            var result = await _workService.RunProjectStepAsync(
                                projectDirectory, null, key, PipelineForceRerun, progress, cancellationToken, runtimeConfigPath);
                            if (!result.Ok)
                                throw new InvalidOperationException(result.Message ?? $"{label}执行失败。");
                        });
                    }

                    if (PipelineAutoRepairEnabled)
                    {
                        await RunTrackedStepAsync(trackedJob, trackedRow, "material-auto-repair", "一键修复", PipelineForceRerun,
                            () => RunMaterialAutoRepairAsync(projectDirectory, runtimeConfigPath, progress, cancellationToken));
                    }

                    if (PipelineAutoFillEnabled)
                    {
                        await RunTrackedStepAsync(trackedJob, trackedRow, "auto-fill-info", "补齐字段", PipelineForceRerun,
                            async () => await _workService.AutoFillProjectInfoAsync(projectDirectory, null, cancellationToken));
                    }

                    if (PipelineRemuxEnabled)
                    {
                        await RunTrackedStepAsync(trackedJob, trackedRow, "upload-remux", "无损重封装", PipelineForceRerun, async () =>
                        {
                            var remux = await _workService.RemuxUploadVideosAsync(projectDirectory, null, progress, cancellationToken);
                            if (!remux.Ok) throw new InvalidOperationException(remux.Message);
                        });
                    }

                    if (SelectedPlatform.Value == PublishPlatform.KuaishouPersonalRevenue)
                    {
                        await RunTrackedStepAsync(trackedJob, trackedRow, "kuaishou-personal-artifacts", "快手个人版产物", PipelineForceRerun, async () =>
                        {
                            var config = KuaishouPersonalConfig.Load(new PublishJob
                            {
                                AccountId = SelectedAccount?.Model.Id ?? string.Empty,
                                ConfigPath = SelectedAccount?.Model.BaseConfigPath ?? DraftConfigPath,
                                ProjectDirectory = projectDirectory,
                            });
                            var projectData = await _kuaishouPersonalProjectDataService.ResolveAsync(projectDirectory, config, cancellationToken);
                            await _kuaishouPersonalPreparationService.PrepareAsync(projectData, config, PipelineForceRerun, cancellationToken);
                            var issues = await _kuaishouPersonalPreparationService.ValidateAsync(projectData.WorkflowDirectory, cancellationToken);
                            if (issues.Count > 0)
                                throw new InvalidOperationException($"快手个人版产物校验失败：{string.Join("；", issues.Select(item => item.Message))}");
                            AppendActivityLog("快手个人版产物：横屏封面、竖屏海报、自动补齐字段和 payload 预览已生成并通过校验。");
                        });
                    }

                    if (PipelineMaterialValidateEnabled)
                    {
                        if (SelectedPlatform.Value == PublishPlatform.KuaishouPersonalRevenue)
                        {
                            AppendActivityLog("素材校验：快手个人版已使用其专属产物校验，跳过视频号校验器。");
                        }
                        else
                        {
                        await RunTrackedStepAsync(trackedJob, trackedRow, "material-validate", "素材校验", PipelineForceRerun, async () =>
                        {
                            var configPath = await _workService.EnsureWeixinUploadConfigAsync(projectDirectory, null, cancellationToken);
                            var workflowDirectory = Path.GetDirectoryName(configPath)
                                                    ?? throw new InvalidOperationException("无法定位视频号工作项目目录。");
                            var validation = await _materialValidationService.ValidateAsync(workflowDirectory, cancellationToken);
                            if (validation.HasErrors)
                            {
                                var errors = validation.Issues.Where(item => item.Severity == "错误").Select(item => item.Message);
                                throw new InvalidOperationException($"素材校验失败：{string.Join("；", errors)}");
                            }
                            foreach (var issue in validation.Issues)
                                AppendActivityLog($"素材校验[{issue.Severity}]：{issue.Message}");
                        });
                        }
                    }

                    StatusMessage = $"共享项目流水线完成：{Path.GetFileName(projectDirectory)}";
                    AppendActivityLog(StatusMessage);
                }
                catch (OperationCanceledException)
                {
                    StatusMessage = "共享项目流水线已停止。";
                    AppendActivityLog(StatusMessage);
                }
                catch (Exception ex)
                {
                    StatusMessage = $"共享项目流水线失败：{ex.Message}";
                    AppendActivityLog(StatusMessage);
                }
            });
        }
        finally
        {
            try
            {
                var runtimeDir = Path.GetDirectoryName(runtimeConfigPath);
                if (runtimeDir is not null && Path.GetFileName(runtimeDir).StartsWith("weixin-runtime-", StringComparison.Ordinal))
                    Directory.Delete(runtimeDir, true);
                else
                    File.Delete(runtimeConfigPath);
            }
            catch { /* 临时配置清理由下次系统临时目录维护。 */ }
        }
    }

    private static string PrepareWeixinRuntimeConfig(
        TikTokPublisher.Core.Models.ClientSettings settings,
        string projectDirectory,
        string accountName)
    {
        var generated = ClientSettingsWorkflowConfigWriter.WriteTempConfig(settings);
        var runtimeDir = Path.Combine(PlatformPublisherPaths.DataRoot, "runtime", "weixin-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeDir);
        var configPath = Path.Combine(runtimeDir, "config.json");
        var payload = JsonNode.Parse(File.ReadAllText(generated))?.AsObject() ?? new JsonObject();
        try { File.Delete(generated); } catch { }

        var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var infoPath = Path.Combine(projectDirectory, "短剧信息.txt");
        if (File.Exists(infoPath))
        {
            foreach (var line in File.ReadLines(infoPath))
            {
                var index = line.IndexOfAny([':', '：']);
                if (index > 0) info[line[..index].Trim()] = line[(index + 1)..].Trim();
            }
        }
        var company = info.GetValueOrDefault("制作公司", string.Empty);
        var legacyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".weixin_channel_tool",
            "settings.json");
        if (File.Exists(legacyPath))
        {
            try
            {
                var legacy = JsonNode.Parse(File.ReadAllText(legacyPath))?.AsObject();
                var profilesText = legacy?["account_profiles_json"]?.GetValue<string>() ?? "[]";
                var profiles = JsonNode.Parse(profilesText)?.AsArray();
                var selected = profiles?.OfType<JsonObject>().FirstOrDefault(profile =>
                                   !string.IsNullOrWhiteSpace(company) &&
                                   string.Equals(profile["cost_report_company_name"]?.ToString(), company, StringComparison.Ordinal))
                               ?? profiles?.OfType<JsonObject>().FirstOrDefault(profile =>
                                   !string.IsNullOrWhiteSpace(accountName) &&
                                   string.Equals(profile["name"]?.ToString(), accountName, StringComparison.Ordinal));
                if (selected is not null)
                {
                    CopyLegacyAsset(selected, "cost_report_sign_path", Path.Combine(runtimeDir, "sign.png"));
                    CopyLegacyAsset(selected, "cost_report_seal_path", Path.Combine(runtimeDir, "seal.png"));
                    var template = selected["cost_report_template_docx_path"]?.ToString();
                    if (string.IsNullOrWhiteSpace(template) || !File.Exists(template))
                        template = new[]
                        {
                            @"D:\Program Files\shortdrama-assistant\_internal\成本报表模板示例.docx",
                            @"D:\code\weixin-channel-tool\成本报表模板示例.docx",
                        }.FirstOrDefault(File.Exists);
                    if (!string.IsNullOrWhiteSpace(template) && File.Exists(template))
                    {
                        var target = Path.Combine(runtimeDir, "成本报表模板.docx");
                        File.Copy(template, target, true);
                        payload["TemplateDocxPath"] = target;
                        payload["CostReportTemplatePath"] = target;
                    }
                    payload["CompanyName"] = string.IsNullOrWhiteSpace(company)
                        ? selected["cost_report_company_name"]?.ToString()
                        : company;
                    payload["CostReportActorPayRatio"] = selected["cost_report_actor_pay_ratio"]?.DeepClone();
                    payload["CostReportLegalRepresentative"] = selected["cost_report_legal_representative"]?.DeepClone();
                }
            }
            catch
            {
                // 旧配置不可用时仍保留独立运行时配置，具体缺失资源由对应步骤报告。
            }
        }
        File.WriteAllText(configPath, payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return configPath;
    }

    private static void CopyLegacyAsset(JsonObject profile, string key, string target)
    {
        var source = profile[key]?.ToString();
        if (!string.IsNullOrWhiteSpace(source) && File.Exists(source)) File.Copy(source, target, true);
    }

    private async Task RunTrackedStepAsync(
        PublishJob? job,
        PublishJobRowViewModel? row,
        string key,
        string label,
        bool force,
        Func<Task> action)
    {
        if (job is not null && job.StepStates.TryGetValue(key, out var existing) &&
            existing.Status == PublishJobStepStatus.Succeeded && !force)
        {
            AppendActivityLog($"共享流水线：{label}已完成，跳过。");
            return;
        }

        StatusMessage = $"共享流水线：{label}";
        AppendActivityLog(StatusMessage);
        await UpdateStepStateAsync(job, row, key, label, PublishJobStepStatus.Running, "正在执行");
        try
        {
            await action();
            await UpdateStepStateAsync(job, row, key, label, PublishJobStepStatus.Succeeded, "执行完成");
        }
        catch (OperationCanceledException)
        {
            await UpdateStepStateAsync(job, row, key, label, PublishJobStepStatus.Pending, "已停止，可继续执行");
            throw;
        }
        catch (Exception ex)
        {
            await UpdateStepStateAsync(job, row, key, label, PublishJobStepStatus.Failed, ex.Message);
            throw;
        }
    }

    private async Task UpdateStepStateAsync(
        PublishJob? job,
        PublishJobRowViewModel? row,
        string key,
        string label,
        PublishJobStepStatus status,
        string message)
    {
        if (job is null) return;
        EnsureStepStates(job);
        var step = job.StepStates[key];
        step.Key = key;
        step.Label = label;
        step.Status = status;
        step.Message = message;
        step.UpdatedAt = DateTimeOffset.Now;
        job.UpdatedAt = DateTimeOffset.Now;
        row?.Refresh();
        await PersistAsync();
    }

    private async Task RunMaterialAutoRepairAsync(
        string projectDirectory,
        string runtimeConfigPath,
        IProgress<WorkRunEvent> progress,
        CancellationToken cancellationToken)
    {
        var configPath = await _workService.EnsureWeixinUploadConfigAsync(projectDirectory, null, cancellationToken);
        var workflowDirectory = Path.GetDirectoryName(configPath)
                                ?? throw new InvalidOperationException("一键修复无法定位工作项目目录。");
        var validation = await _materialValidationService.ValidateAsync(workflowDirectory, cancellationToken);
        var codes = validation.Issues
            .Where(issue => issue.CanAutoFix)
            .Select(issue => issue.Code)
            .ToHashSet(StringComparer.Ordinal);
        if (codes.Count == 0)
        {
            AppendActivityLog("一键修复：未发现可自动修复的问题。");
            return;
        }

        var repairSteps = new List<(bool Needed, string Key, string Label, bool Force)>
        {
            (codes.Contains("info-missing") || codes.Contains("info-invalid"), "rewrite", "改写信息", true),
            (codes.Contains("video-bitrate-low") || codes.Contains("videos-dir-missing") || codes.Contains("video-bitrate-unreadable"), "transcode", "素材转码", PipelineForceRerun),
            (codes.Contains("poster-missing"), "poster-rename", "生成海报", true),
            (codes.Contains("project-images-missing"), "project-image", "生成工程图", true),
            (codes.Contains("material-video-title-mismatch"), "material-convert", "重建素材视频", true),
            (codes.Contains("cost-missing"), "cost-report", "生成成本报表", true),
            (codes.Contains("video-title-mismatch"), "batch-file-rename", "修正视频文件名", true),
        };
        foreach (var (_, key, label, force) in repairSteps.Where(item => item.Needed))
        {
            AppendActivityLog($"一键修复：{label}");
            var result = await _workService.RunProjectStepAsync(
                projectDirectory, null, key, force, progress, cancellationToken, runtimeConfigPath);
            if (!result.Ok)
                throw new InvalidOperationException(result.Message ?? $"一键修复步骤失败：{label}");
        }

        if (codes.Contains("weixin-upload-config-missing"))
            await _workService.EnsureWeixinUploadConfigAsync(projectDirectory, null, cancellationToken);
        if (codes.Contains("weixin-title-mismatch"))
            await _workService.RefreshWeixinConfigsAsync(projectDirectory, null, cancellationToken);

        var after = await _materialValidationService.ValidateAsync(workflowDirectory, cancellationToken);
        if (after.HasErrors)
        {
            var remaining = after.Issues.Where(issue => issue.Severity == "错误").Select(issue => issue.Message);
            throw new InvalidOperationException($"一键修复后仍有问题：{string.Join("；", remaining)}");
        }
        AppendActivityLog("一键修复完成。");
    }

    private bool CanRunSelected() => SelectedJob is not null && !IsBusy;
    private bool CanRunRunnable() => !IsBusy && VisibleJobs.Any(row =>
        PublishSchedulePolicy.CanRunNow(row.Model, DateTimeOffset.Now) &&
        _coordinator.GetAdapter(row.Platform).IsAvailable);
    private bool CanRetryFailed() => !IsBusy && VisibleJobs.Any(row => row.Model.Status == PublishJobStatus.Failed);
    private bool CanOpenLogin() => SelectedJob is not null && !IsBusy;

    private bool CanSaveAccount() => !IsBusy && !string.IsNullOrWhiteSpace(DraftAccountName);

    private void BeginNewAccount()
    {
        SelectedAccount = null;
        DraftAccountName = string.Empty;
        DraftConfigPath = string.Empty;
        StatusMessage = $"正在新增{SelectedPlatform.Name}账号档案。";
    }

    private async Task SaveAccountAsync()
    {
        var account = SelectedAccount?.Model;
        if (account is null || account.Platform != SelectedPlatform.Value)
        {
            account = new PublishAccount
            {
                Platform = SelectedPlatform.Value,
                CreatedAt = DateTimeOffset.Now,
            };
            _accounts.Add(account);
        }

        account.Name = DraftAccountName.Trim();
        account.BaseConfigPath = DraftConfigPath.Trim();
        account.UpdatedAt = DateTimeOffset.Now;
        await _accountStore.SaveAsync(_accounts);
        RefreshVisibleAccounts(account.Id);
        StatusMessage = $"已保存{account.Platform.DisplayName()}账号：{account.Name}";
    }

    private async Task DeleteAccountAsync()
    {
        if (SelectedAccount is null)
            return;

        var account = SelectedAccount.Model;
        _accounts.Remove(account);
        await _accountStore.SaveAsync(_accounts);
        RefreshVisibleAccounts();
        StatusMessage = $"已删除账号档案：{account.Name}；授权文件和历史任务均未删除。";
    }

    private async Task RunSelectedAsync()
    {
        if (SelectedJob is null)
            return;

        await RunRowsAsync([SelectedJob], clearSchedule: true);
    }

    private async Task AutoShelfAsync()
    {
        if (SelectedJob is null) return;
        var row = SelectedJob;
        var job = row.Model;
        await RunBusyAsync(async cancellationToken =>
        {
            await UpdateStepStateAsync(job, row, "shelf", "自动上架", PublishJobStepStatus.Running, "正在扫描未上架剧集");
            try
            {
                var progress = new Progress<string>(message =>
                {
                    StatusMessage = $"[自动上架] {message}";
                    AppendActivityLog(StatusMessage);
                });
                var result = await _autoShelfService.RunAsync(
                    job,
                    Math.Clamp(AutoShelfMaxPages, 1, 100),
                    Math.Clamp(AutoShelfMaxRounds, 1, 100),
                    progress,
                    cancellationToken);
                var message = $"完成：上架 {result.ShelvedCount}，失败 {result.FailedCount}，扫描 {result.ScannedRows} 行";
                var status = result.FailedCount > 0 && result.ShelvedCount == 0
                    ? PublishJobStepStatus.Failed
                    : PublishJobStepStatus.Succeeded;
                await UpdateStepStateAsync(job, row, "shelf", "自动上架", status, message);
                StatusMessage = $"自动上架{message}";
                AppendActivityLog(StatusMessage);
            }
            catch (OperationCanceledException)
            {
                await UpdateStepStateAsync(job, row, "shelf", "自动上架", PublishJobStepStatus.Pending, "已停止，可继续执行");
                StatusMessage = "自动上架已停止。";
            }
            catch (Exception ex)
            {
                await UpdateStepStateAsync(job, row, "shelf", "自动上架", PublishJobStepStatus.Failed, ex.Message);
                StatusMessage = $"自动上架失败：{ex.Message}";
                AppendActivityLog(StatusMessage);
            }
        });
    }

    private async Task SyncCheckedManagementAsync()
    {
        var rows = VisibleJobs.Where(row => row.IsChecked).ToArray();
        if (rows.Length == 0 && SelectedJob is not null) rows = [SelectedJob];
        if (rows.Length == 0)
        {
            StatusMessage = "请先勾选或选择要同步的任务。";
            return;
        }
        await RunBusyAsync(async cancellationToken =>
        {
            var succeeded = 0;
            var failed = 0;
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var uploaded = row.Model.Status == PublishJobStatus.Succeeded ||
                               row.Model.StepStates.GetValueOrDefault("weixin-upload")?.Status == PublishJobStepStatus.Succeeded
                    ? "是"
                    : "否";
                if (await TrySyncManagementAsync(row.Model, row, uploaded, cancellationToken)) succeeded++;
                else failed++;
            }
            StatusMessage = $"管理系统同步结束：成功 {succeeded}，失败 {failed}。";
            AppendActivityLog(StatusMessage);
        });
    }

    private async Task<bool> TrySyncManagementAsync(
        PublishJob job,
        PublishJobRowViewModel row,
        string uploaded,
        CancellationToken cancellationToken)
    {
        await UpdateStepStateAsync(job, row, "management-sync", "管理同步", PublishJobStepStatus.Running, "正在同步");
        try
        {
            var settings = ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);
            var credentials = new WeixinManagementCredentials(
                settings.AuthServerUrl,
                settings.AuthAccount,
                settings.AuthPassword);
            var result = await _managementSyncService.SyncAsync(
                job,
                credentials,
                uploaded,
                job.AccountName,
                cancellationToken);
            await UpdateStepStateAsync(job, row, "management-sync", "管理同步", PublishJobStepStatus.Succeeded, result.Message);
            AppendActivityLog($"[{job.ProjectName}] {result.Message}");
            return true;
        }
        catch (OperationCanceledException)
        {
            await UpdateStepStateAsync(job, row, "management-sync", "管理同步", PublishJobStepStatus.Pending, "已停止，可继续同步");
            throw;
        }
        catch (Exception ex)
        {
            await UpdateStepStateAsync(job, row, "management-sync", "管理同步", PublishJobStepStatus.Failed, ex.Message);
            AppendActivityLog($"[{job.ProjectName}] 管理系统同步失败：{ex.Message}");
            return false;
        }
    }

    private async Task RunCheckedAsync()
    {
        var rows = VisibleJobs.Where(row => row.IsChecked).ToArray();
        if (rows.Length == 0)
        {
            StatusMessage = "请先勾选要执行的任务。";
            return;
        }
        await RunRowsAsync(rows, clearSchedule: true);
    }

    private async Task SetVisibleCheckedAsync(Func<PublishJobRowViewModel, bool> selector)
    {
        foreach (var row in VisibleJobs)
            row.IsChecked = selector(row);
        await PersistAsync();
        StatusMessage = $"当前筛选结果已勾选 {VisibleJobs.Count(row => row.IsChecked)} 条。";
    }

    private async Task CheckToCurrentAsync()
    {
        if (SelectedJob is null)
        {
            StatusMessage = "请先选择当前项目。";
            return;
        }
        var currentIndex = VisibleJobs.IndexOf(SelectedJob);
        if (currentIndex < 0) return;
        for (var index = 0; index < VisibleJobs.Count; index++)
            VisibleJobs[index].IsChecked = index <= currentIndex;
        await PersistAsync();
        StatusMessage = $"已勾选到当前项目，共 {currentIndex + 1} 条。";
    }

    private async Task ApplyCheckedStatusAsync()
    {
        var rows = VisibleJobs.Where(row => row.IsChecked).ToArray();
        foreach (var row in rows)
        {
            row.Model.Status = SelectedJobStatusChoice.Value;
            row.Model.StatusMessage = $"手动修改为{SelectedJobStatusChoice.Label}";
            row.Model.UpdatedAt = DateTimeOffset.Now;
            row.Refresh();
        }
        if (rows.Length > 0) await PersistAsync();
        StatusMessage = rows.Length == 0
            ? "没有勾选任务。"
            : $"已将 {rows.Length} 条任务修改为{SelectedJobStatusChoice.Label}。";
        AppendActivityLog(StatusMessage);
    }

    private async Task AssignCheckedToCurrentAccountAsync()
    {
        if (!HasActiveWeixinAccount)
        {
            StatusMessage = "请先在左侧选择视频号账号。";
            return;
        }
        var rows = VisibleJobs.Where(row => row.IsChecked).ToArray();
        foreach (var row in rows)
        {
            row.Model.AccountId = _activeWeixinAccountId;
            row.Model.AccountName = ActiveWeixinAccountName;
            row.Model.AccountSessionDirectory = _activeWeixinAccountSessionDirectory;
            row.Model.UpdatedAt = DateTimeOffset.Now;
            row.Refresh();
        }
        if (rows.Length > 0)
        {
            await PersistAsync();
            RefreshVisibleJobs(rows[0].Id);
        }
        StatusMessage = rows.Length == 0
            ? "没有勾选任务。"
            : $"已将 {rows.Length} 条任务归属到账号：{ActiveWeixinAccountName}。";
        AppendActivityLog(StatusMessage);
    }

    private async Task ResetCheckedJobsAsync()
    {
        var rows = VisibleJobs.Where(row => row.IsChecked).ToArray();
        foreach (var row in rows)
        {
            row.Model.Status = PublishJobStatus.Pending;
            row.Model.StatusMessage = "已批量重置为待执行";
            ResetFailedStepStates(row.Model);
            row.Model.UpdatedAt = DateTimeOffset.Now;
            row.Refresh();
        }
        if (rows.Length > 0) await PersistAsync();
        StatusMessage = rows.Length == 0 ? "没有勾选任务。" : $"已重置 {rows.Length} 条任务。";
        AppendActivityLog(StatusMessage);
    }

    private async Task RemoveCheckedJobsAsync()
    {
        var targets = VisibleJobs.Where(row => row.IsChecked).Select(row => row.Model).ToArray();
        foreach (var job in targets)
            _jobs.Remove(job);
        if (targets.Length > 0) await PersistAsync();
        RefreshVisibleJobs();
        StatusMessage = targets.Length == 0
            ? "没有勾选任务。"
            : $"已从队列移出 {targets.Length} 条任务，项目文件未删除。";
        AppendActivityLog(StatusMessage);
    }

    private async Task RunRunnableAsync()
    {
        var now = DateTimeOffset.Now;
        var rows = VisibleJobs
            .Where(row => PublishSchedulePolicy.CanRunNow(row.Model, now))
            .Where(row => _coordinator.GetAdapter(row.Platform).IsAvailable)
            .OrderBy(row => PipelinePreferUploadWhenReady ? ResolveUploadPriority(row.Model) : 0)
            .ThenBy(row => row.Model.ScheduledAt ?? row.Model.CreatedAt)
            .ToArray();
        await RunRowsAsync(rows, clearSchedule: true);
    }

    private static int ResolveUploadPriority(PublishJob job)
    {
        if (job.Kind != PublishJobKind.Series)
            return 1;
        if (!string.IsNullOrWhiteSpace(job.ConfigPath) && File.Exists(job.ConfigPath))
            return 0;
        if (!Directory.Exists(job.ProjectDirectory))
            return 1;
        return new[] { "weixin-channel-autogen.json", "weixin-channel-submit.json", "weixin-channel-config.json" }
            .Any(name => File.Exists(Path.Combine(job.ProjectDirectory, name))) ? 0 : 1;
    }

    private async Task RetryFailedAsync()
    {
        var rows = VisibleJobs.Where(row => row.Model.Status == PublishJobStatus.Failed).ToArray();
        await RunRowsAsync(rows, clearSchedule: true);
    }

    private async Task RunRowsAsync(IReadOnlyList<PublishJobRowViewModel> rows, bool clearSchedule)
    {
        if (rows.Count == 0)
            return;

        await RunBusyAsync(async cancellationToken =>
        {
            try
            {
                for (var index = 0; index < rows.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    StatusMessage = $"批量执行 {index + 1}/{rows.Count}：{rows[index].ProjectName}";
                    AppendActivityLog(StatusMessage);
                    await ExecuteJobAsync(rows[index], clearSchedule, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "批量执行已停止，未开始的任务仍保留在队列中。";
            }
        });
    }

    private async Task ExecuteJobAsync(
        PublishJobRowViewModel row,
        bool clearSchedule,
        CancellationToken cancellationToken)
    {
        var job = row.Model;
        var adapter = _coordinator.GetAdapter(job.Platform);
        if (!adapter.IsAvailable)
        {
            job.Status = PublishJobStatus.Blocked;
            job.StatusMessage = adapter.AvailabilityMessage;
            row.Refresh();
            StatusMessage = adapter.AvailabilityMessage;
            await PersistAsync(cancellationToken);
            return;
        }

        if (clearSchedule)
            job.ScheduledAt = null;
        job.AttemptCount++;
        job.LastStartedAt = DateTimeOffset.Now;
        job.Status = PublishJobStatus.Running;
        job.StatusMessage = "正在启动发布流程…";
        job.UpdatedAt = DateTimeOffset.Now;
        row.Refresh();
        await PersistAsync(cancellationToken);

        var progress = new Progress<string>(message =>
        {
            job.StatusMessage = message;
            row.Refresh();
            StatusMessage = $"[{job.ProjectName}] {message}";
            AppendActivityLog(StatusMessage);
        });
        var uploadLabel = job.Kind == PublishJobKind.Series ? "上传剧集" : "素材发表";

        try
        {
            await UpdateStepStateAsync(job, row, "weixin-upload", uploadLabel, PublishJobStepStatus.Running, "正在执行");
            await adapter.RunAsync(job, progress, cancellationToken);
            await UpdateStepStateAsync(job, row, "weixin-upload", uploadLabel, PublishJobStepStatus.Succeeded, "执行完成");
            job.Status = PublishJobStatus.Succeeded;
            job.StatusMessage = "发布流程执行完成";
            StatusMessage = $"[{job.ProjectName}] 发布完成";
            AppendActivityLog(StatusMessage);
            if (PipelineSyncManagementAfterUpload)
                await TrySyncManagementAsync(job, row, "是", CancellationToken.None);
            if (PipelineAutoArchiveAfterUpload && job.Kind == PublishJobKind.Series)
                await TryArchivePublishedProjectAsync(job, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await UpdateStepStateAsync(job, row, "weixin-upload", uploadLabel, PublishJobStepStatus.Pending, "已停止，可继续执行");
            job.Status = PublishJobStatus.Pending;
            job.StatusMessage = "已停止，可继续执行";
            StatusMessage = $"[{job.ProjectName}] 已停止";
            AppendActivityLog(StatusMessage);
        }
        catch (Exception ex)
        {
            await UpdateStepStateAsync(job, row, "weixin-upload", uploadLabel, PublishJobStepStatus.Failed, ex.Message);
            job.Status = PublishJobStatus.Failed;
            job.StatusMessage = ex.Message;
            StatusMessage = $"[{job.ProjectName}] 发布失败：{ex.Message}";
            AppendActivityLog(StatusMessage);
        }
        finally
        {
            job.UpdatedAt = DateTimeOffset.Now;
            job.LastCompletedAt = DateTimeOffset.Now;
            row.Refresh();
            await PersistAsync();
        }
    }

    private async Task TryArchivePublishedProjectAsync(PublishJob job, CancellationToken cancellationToken)
    {
        try
        {
            var rootDirectory = Directory.GetParent(job.ProjectDirectory)?.FullName;
            if (string.IsNullOrWhiteSpace(rootDirectory))
                throw new InvalidOperationException("无法确定项目根目录。");
            ArchiveRootDirectory = rootDirectory;
            var scan = await _projectScanner.ScanAsync(rootDirectory, null, cancellationToken);
            var project = scan.Projects.FirstOrDefault(item =>
                string.Equals(Path.GetFullPath(item.SourceProjectDir), Path.GetFullPath(job.ProjectDirectory), StringComparison.OrdinalIgnoreCase));
            if (project is null)
                throw new InvalidOperationException("扫描结果中未找到刚完成上传的项目。");
            AppendActivityLog($"上传完成，开始自动归档：{job.ProjectName}");
            var result = await _projectArchiveService.ArchiveAsync(
                rootDirectory, project, new ProjectArchiveOptions(), cancellationToken);
            if (!result.Ok)
                throw new InvalidOperationException(result.Message);
            job.ProjectDirectory = result.ArchiveProjectDir;
            job.StatusMessage = $"发布完成并已归档：{result.Message}";
            AppendActivityLog(job.StatusMessage);
            RefreshArchivedProjects();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            job.StatusMessage = $"发布完成；自动归档失败：{ex.Message}";
            AppendActivityLog(job.StatusMessage);
        }
    }

    private async Task OpenLoginAsync()
    {
        if (SelectedJob is null)
            return;

        var job = SelectedJob.Model;
        var adapter = _coordinator.GetAdapter(job.Platform);
        if (!adapter.IsAvailable)
        {
            StatusMessage = adapter.AvailabilityMessage;
            return;
        }

        await RunBusyAsync(async cancellationToken =>
        {
            StatusMessage = $"[{job.ProjectName}] 已打开登录窗口，关闭浏览器后返回助手。";
            try
            {
                await adapter.OpenLoginAsync(job, cancellationToken);
                StatusMessage = $"[{job.ProjectName}] 登录窗口已关闭，登录态已保存。";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = $"[{job.ProjectName}] 登录操作已停止。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"[{job.ProjectName}] 打开登录窗口失败：{ex.Message}";
            }
        });
    }

    private async Task RemoveSelectedAsync()
    {
        if (SelectedJob is null)
            return;

        var name = SelectedJob.ProjectName;
        _jobs.Remove(SelectedJob.Model);
        await PersistAsync();
        RefreshVisibleJobs();
        StatusMessage = $"已从独立队列移除：{name}（项目文件未删除）";
    }

    private async Task ResetSelectedJobAsync()
    {
        if (SelectedJob is null) return;
        var job = SelectedJob.Model;
        job.Status = PublishJobStatus.Pending;
        job.StatusMessage = "已重置为待执行";
        ResetFailedStepStates(job);
        job.UpdatedAt = DateTimeOffset.Now;
        SelectedJob.Refresh();
        await PersistAsync();
        StatusMessage = $"已重置任务状态：{job.ProjectName}";
        AppendActivityLog(StatusMessage);
        NotifyCommands();
    }

    private static void ResetFailedStepStates(PublishJob job)
    {
        EnsureStepStates(job);
        foreach (var step in job.StepStates.Values.Where(step => step.Status is PublishJobStepStatus.Failed or PublishJobStepStatus.Running))
        {
            step.Status = PublishJobStepStatus.Pending;
            step.Message = "已重置为待执行";
            step.UpdatedAt = DateTimeOffset.Now;
        }
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        _operationCts = new CancellationTokenSource();
        NotifyCommands();
        try
        {
            await action(_operationCts.Token);
        }
        finally
        {
            _operationCts.Dispose();
            _operationCts = null;
            IsBusy = false;
            NotifyCommands();
        }
    }

    public void Stop() => _operationCts?.Cancel();

    public void Shutdown()
    {
        _scheduleTimer.Stop();
        _interactionService.RequestChanged -= OnInteractionRequestChanged;
        Stop();
    }

    private void OnInteractionRequestChanged(WorkflowInteractionRequest? request)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _currentInteractionRequest = request;
            HasInteractionRequest = request is not null;
            InteractionTitle = request is null ? "人工介入" : $"人工介入 · {request.DisplayName}";
            InteractionMessage = request?.Message ?? string.Empty;
            if (request is not null)
                StatusMessage = $"等待人工处理：{request.DisplayName}";
            NotifyCommands();
        });
    }

    private bool CanResolveInteraction(string decision) =>
        _currentInteractionRequest?.Options.Contains(decision, StringComparer.Ordinal) == true;

    private void ResolveInteraction(string decision)
    {
        if (!_interactionService.TryResolve(decision))
            return;
        StatusMessage = decision switch
        {
            "manual" => "已切换到人工接管，请在浏览器完成处理后点击继续。",
            "resume" => "已提交继续执行。",
            "skip_video" => "已提交跳过当前视频。",
            "skip_project" => "已提交跳过当前项目。",
            "stop" => "已提交停止任务。",
            _ => $"已提交人工处理决策：{decision}",
        };
    }

    private async void OnScheduleTimerTick(object? sender, EventArgs e)
    {
        if (_scheduleTickRunning || IsBusy)
            return;

        var dueJobs = _jobs
            .Where(job => PublishSchedulePolicy.IsDue(job, DateTimeOffset.Now))
            .OrderBy(job => job.ScheduledAt)
            .ToArray();
        if (dueJobs.Length == 0)
            return;

        _scheduleTickRunning = true;
        try
        {
            var rows = dueJobs.Select(job =>
                VisibleJobs.FirstOrDefault(row => row.Id == job.Id) ?? new PublishJobRowViewModel(job)).ToArray();
            StatusMessage = $"检测到 {rows.Length} 条到期任务，开始自动执行。";
            await RunRowsAsync(rows, clearSchedule: true);
            RefreshVisibleJobs(SelectedJob?.Id);
        }
        finally
        {
            _scheduleTickRunning = false;
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken = default) =>
        await _store.SaveAsync(_jobs, cancellationToken);

    private void AppendActivityLog(string message)
    {
        void Append()
        {
            ActivityLogs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            while (ActivityLogs.Count > 500)
                ActivityLogs.RemoveAt(0);
        }

        if (Dispatcher.UIThread.CheckAccess())
            Append();
        else
            Dispatcher.UIThread.Post(Append);
    }

    public void RefreshArchivedProjects()
    {
        var selectedKey = SelectedArchivedProject?.ProjectKey;
        ArchivedProjects.Clear();
        if (string.IsNullOrWhiteSpace(ArchiveRootDirectory))
        {
            OnPropertyChanged(nameof(ArchivedProjectsSummary));
            SelectedArchivedProject = null;
            return;
        }

        var archiveRoot = Path.Combine(Path.GetFullPath(ArchiveRootDirectory), "archive");
        if (!Directory.Exists(archiveRoot))
        {
            OnPropertyChanged(nameof(ArchivedProjectsSummary));
            SelectedArchivedProject = null;
            return;
        }

        var rows = new List<ArchivedProjectRowViewModel>();
        foreach (var directory in Directory.EnumerateDirectories(archiveRoot))
        {
            try
            {
                var metadataPath = Path.Combine(directory, "archive-meta.json");
                using var document = File.Exists(metadataPath)
                    ? JsonDocument.Parse(File.ReadAllText(metadataPath))
                    : null;
                var root = document?.RootElement;
                var projectKey = ReadString(root, "ProjectKey") ?? Path.GetFileName(directory);
                rows.Add(new ArchivedProjectRowViewModel(
                    projectKey,
                    ReadString(root, "DisplayName") ?? projectKey,
                    directory,
                    ReadString(root, "archivedSourceDir") ?? Path.Combine(directory, "source"),
                    ReadString(root, "archivedWorkflowDir") ?? Path.Combine(directory, "workflow"),
                    ReadDateTimeOffset(root, "ArchivedAt"),
                    ReadInt32(root, "deletedVideoFileCount"),
                    ReadInt32(root, "preservedVideoFileCount")));
            }
            catch (Exception ex)
            {
                AppendActivityLog($"读取归档项目失败：{directory}，{ex.Message}");
            }
        }

        foreach (var row in rows.OrderByDescending(item => item.ArchivedAt))
            ArchivedProjects.Add(row);
        SelectedArchivedProject = selectedKey is null
            ? ArchivedProjects.FirstOrDefault()
            : ArchivedProjects.FirstOrDefault(item => item.ProjectKey == selectedKey) ?? ArchivedProjects.FirstOrDefault();
        OnPropertyChanged(nameof(ArchivedProjectsSummary));
    }

    private static string? ReadString(JsonElement? root, string propertyName) =>
        root is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int ReadInt32(JsonElement? root, string propertyName) =>
        root is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(propertyName, out var property) &&
        property.TryGetInt32(out var result)
            ? result
            : 0;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement? root, string propertyName) =>
        root is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(property.GetString(), out var result)
            ? result
            : null;

    private void RefreshVisibleJobs(string? selectId = null)
    {
        void Refresh()
        {
            VisibleJobs.Clear();
            foreach (var job in _jobs
                         .Where(job => job.Platform == SelectedPlatform.Value)
                         .Where(job => SelectedPlatform.Value != PublishPlatform.WeixinChannel ||
                                       (_materialWorkflowActive
                                           ? job.Kind != PublishJobKind.Series
                                           : job.Kind == PublishJobKind.Series))
                         .Where(job => SelectedPlatform.Value != PublishPlatform.WeixinChannel ||
                                       string.IsNullOrWhiteSpace(WorkflowFilterText) ||
                                       job.ProjectName.Contains(WorkflowFilterText, StringComparison.CurrentCultureIgnoreCase) ||
                                       job.AccountName.Contains(WorkflowFilterText, StringComparison.CurrentCultureIgnoreCase) ||
                                       job.ProjectDirectory.Contains(WorkflowFilterText, StringComparison.CurrentCultureIgnoreCase) ||
                                       job.StatusMessage.Contains(WorkflowFilterText, StringComparison.CurrentCultureIgnoreCase))
                         .OrderByDescending(job => job.CreatedAt))
            {
                VisibleJobs.Add(new PublishJobRowViewModel(job));
            }

            if (SelectedPlatform.Value == PublishPlatform.WeixinChannel)
                RefreshPagedJobs(selectId);
            else
                SelectedJob = selectId is null
                    ? VisibleJobs.FirstOrDefault()
                    : VisibleJobs.FirstOrDefault(row => row.Id == selectId);
            OnPropertyChanged(nameof(QueueSummary));
        }

        if (Dispatcher.UIThread.CheckAccess())
            Refresh();
        else
            Dispatcher.UIThread.Post(Refresh);
    }

    private void RefreshPagedJobs(string? selectId = null)
    {
        if (selectId is not null)
        {
            var selectedIndex = VisibleJobs.ToList().FindIndex(row => row.Id == selectId);
            if (selectedIndex >= 0)
                WorkflowCurrentPage = selectedIndex / Math.Max(1, WorkflowPageSize) + 1;
        }
        WorkflowCurrentPage = Math.Clamp(WorkflowCurrentPage, 1, WorkflowPageCount);
        PagedJobs.Clear();
        foreach (var row in VisibleJobs
                     .Skip((WorkflowCurrentPage - 1) * WorkflowPageSize)
                     .Take(WorkflowPageSize))
            PagedJobs.Add(row);
        SelectedJob = selectId is null
            ? PagedJobs.FirstOrDefault()
            : PagedJobs.FirstOrDefault(row => row.Id == selectId) ?? PagedJobs.FirstOrDefault();
        OnPropertyChanged(nameof(WorkflowPageCount));
        OnPropertyChanged(nameof(WorkflowPageSummary));
        PreviousWorkflowPageCommand.NotifyCanExecuteChanged();
        NextWorkflowPageCommand.NotifyCanExecuteChanged();
        AutoShelfCommand.NotifyCanExecuteChanged();
        SyncCheckedManagementCommand.NotifyCanExecuteChanged();
    }

    private void PreviousWorkflowPage()
    {
        if (WorkflowCurrentPage <= 1) return;
        WorkflowCurrentPage--;
        RefreshPagedJobs();
    }

    private void NextWorkflowPage()
    {
        if (WorkflowCurrentPage >= WorkflowPageCount) return;
        WorkflowCurrentPage++;
        RefreshPagedJobs();
    }

    private void RefreshVisibleAccounts(string? selectId = null)
    {
        void Refresh()
        {
            VisibleAccounts.Clear();
            foreach (var account in _accounts
                         .Where(account => account.Platform == SelectedPlatform.Value)
                         .OrderBy(account => account.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                VisibleAccounts.Add(new PublishAccountItemViewModel(account));
            }

            SelectedAccount = selectId is null
                ? VisibleAccounts.FirstOrDefault()
                : VisibleAccounts.FirstOrDefault(account => account.Id == selectId);
            OnPropertyChanged(nameof(QueueSummary));
        }

        if (Dispatcher.UIThread.CheckAccess())
            Refresh();
        else
            Dispatcher.UIThread.Post(Refresh);
    }

    private void NotifyCommands()
    {
        AddJobCommand.NotifyCanExecuteChanged();
        RunSelectedCommand.NotifyCanExecuteChanged();
        RunRunnableCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
        NewAccountCommand.NotifyCanExecuteChanged();
        SaveAccountCommand.NotifyCanExecuteChanged();
        DeleteAccountCommand.NotifyCanExecuteChanged();
        OpenLoginCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        TakeoverInteractionCommand.NotifyCanExecuteChanged();
        ResumeInteractionCommand.NotifyCanExecuteChanged();
        SkipCurrentVideoCommand.NotifyCanExecuteChanged();
        SkipCurrentProjectCommand.NotifyCanExecuteChanged();
        StopInteractionCommand.NotifyCanExecuteChanged();
        RunSharedPipelineCommand.NotifyCanExecuteChanged();
        ScanWorkspaceCommand.NotifyCanExecuteChanged();
        ResetSelectedJobCommand.NotifyCanExecuteChanged();
        SaveWorkflowSettingsCommand.NotifyCanExecuteChanged();
        CreateNamedProjectCommand.NotifyCanExecuteChanged();
        RunCheckedCommand.NotifyCanExecuteChanged();
        CheckAllVisibleCommand.NotifyCanExecuteChanged();
        CheckPendingCommand.NotifyCanExecuteChanged();
        UncheckAllVisibleCommand.NotifyCanExecuteChanged();
        ResetCheckedJobsCommand.NotifyCanExecuteChanged();
        RemoveCheckedJobsCommand.NotifyCanExecuteChanged();
        CheckToCurrentCommand.NotifyCanExecuteChanged();
        CheckCompletedCommand.NotifyCanExecuteChanged();
        ApplyCheckedStatusCommand.NotifyCanExecuteChanged();
        AssignCheckedToCurrentAccountCommand.NotifyCanExecuteChanged();
        PreviousWorkflowPageCommand.NotifyCanExecuteChanged();
        NextWorkflowPageCommand.NotifyCanExecuteChanged();
    }
}
