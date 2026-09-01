using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;
using PlatformPublisher.Common.Services;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly PublishJobStore _store;
    private readonly PublishAccountStore _accountStore;
    private readonly PlatformPublishCoordinator _coordinator;
    private readonly IWorkflowInteractionService _interactionService;
    private readonly IWorkService _workService;
    private readonly IMaterialValidationService _materialValidationService;
    private readonly IProjectScanner _projectScanner;
    private readonly IProjectArchiveService _projectArchiveService;
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
        IProjectArchiveService projectArchiveService)
    {
        _store = store;
        _accountStore = accountStore;
        _coordinator = coordinator;
        _interactionService = interactionService;
        _workService = workService;
        _materialValidationService = materialValidationService;
        _projectScanner = projectScanner;
        _projectArchiveService = projectArchiveService;
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
        _selectedPlatform = Platforms[0];
        _selectedJobKind = JobKinds[0];
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
        _interactionService.RequestChanged += OnInteractionRequestChanged;
        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _scheduleTimer.Tick += OnScheduleTimerTick;
        _scheduleTimer.Start();
        _ = LoadAsync();
    }

    public IReadOnlyList<PlatformOptionViewModel> Platforms { get; }
    public IReadOnlyList<PublishJobKindOptionViewModel> JobKinds { get; }
    public ObservableCollection<PublishJobRowViewModel> VisibleJobs { get; } = [];
    public ObservableCollection<PublishAccountItemViewModel> VisibleAccounts { get; } = [];
    public ObservableCollection<string> ActivityLogs { get; } = [];
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

    [ObservableProperty]
    private PlatformOptionViewModel _selectedPlatform;

    [ObservableProperty]
    private PublishJobKindOptionViewModel _selectedJobKind;

    [ObservableProperty]
    private PublishJobRowViewModel? _selectedJob;

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
    }

    public bool IsSystemHighlightKind => SelectedJobKind.Value == PublishJobKind.SystemHighlight;
    public bool IsWeixinPlatform => SelectedPlatform.Value == PublishPlatform.WeixinChannel;
    public bool IsCustomVideoKind => SelectedJobKind.Value == PublishJobKind.CustomVideos;
    public bool IsStandardMaterialKind => SelectedJobKind.Value is
        PublishJobKind.ProjectMaterials or PublishJobKind.LocalVideos or PublishJobKind.CustomVideos;

    partial void OnSelectedPlatformChanged(PlatformOptionViewModel value)
    {
        RefreshVisibleJobs();
        RefreshVisibleAccounts();
        OnPropertyChanged(nameof(SelectedPlatformCapability));
        OnPropertyChanged(nameof(IsWeixinPlatform));
        NotifyCommands();
    }

    partial void OnSelectedJobChanged(PublishJobRowViewModel? value) => NotifyCommands();
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
    }
    partial void OnDraftDramaTitleChanged(string value) => AddJobCommand.NotifyCanExecuteChanged();
    partial void OnDraftAccountNameChanged(string value) => SaveAccountCommand.NotifyCanExecuteChanged();
    partial void OnDraftScheduleEnabledChanged(bool value) => AddJobCommand.NotifyCanExecuteChanged();
    partial void OnDraftScheduleTextChanged(string value) => AddJobCommand.NotifyCanExecuteChanged();
    partial void OnDraftCustomVideoFilesTextChanged(string value) => AddJobCommand.NotifyCanExecuteChanged();
    partial void OnPipelineDownloadEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineRewriteEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelinePosterEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineTranscodeEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineAutoRepairEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineAutoFillEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineCostReportEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineProjectImageEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineMaterialValidateEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineRemuxEnabledChanged(bool value) => RunSharedPipelineCommand.NotifyCanExecuteChanged();

    private async Task LoadAsync()
    {
        try
        {
            var loadJobs = _store.LoadAsync();
            var loadAccounts = _accountStore.LoadAsync();
            await Task.WhenAll(loadJobs, loadAccounts);
            _jobs.AddRange(await loadJobs);
            _accounts.AddRange(await loadAccounts);
            var recovered = PublishSchedulePolicy.RecoverInterrupted(_jobs);
            if (recovered > 0)
            {
                await PersistAsync();
                StatusMessage = $"已恢复 {recovered} 条上次意外中断的任务。";
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

    private bool CanRunSharedPipeline() =>
        !IsBusy &&
        Directory.Exists(DraftProjectDirectory) &&
        (PipelineDownloadEnabled || PipelineRewriteEnabled || PipelinePosterEnabled ||
         PipelineTranscodeEnabled || PipelineAutoRepairEnabled || PipelineAutoFillEnabled ||
         PipelineCostReportEnabled || PipelineProjectImageEnabled || PipelineMaterialValidateEnabled ||
         PipelineRemuxEnabled);

    private bool CanScanWorkspace() =>
        !IsBusy && HasActiveWeixinAccount && Directory.Exists(DraftProjectDirectory);

    private async Task ScanWorkspaceAsync()
    {
        var rootDirectory = Path.GetFullPath(DraftProjectDirectory);
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
                    _jobs.Add(new PublishJob
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
                    });
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
        var steps = new List<(string Key, string Label)>();
        if (PipelineDownloadEnabled) steps.Add(("download", "下载剧集"));
        if (PipelineRewriteEnabled) steps.Add(("rewrite", "改写信息"));
        if (PipelinePosterEnabled) steps.Add(("poster-rename", "生成海报"));
        if (PipelineTranscodeEnabled) steps.Add(("transcode", "素材转码"));
        if (PipelineCostReportEnabled) steps.Add(("cost-report", "生成成本报表"));
        if (PipelineProjectImageEnabled) steps.Add(("project-image", "生成工程图"));

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
                    StatusMessage = $"共享流水线：{label}";
                    AppendActivityLog(StatusMessage);
                    var result = await _workService.RunProjectStepAsync(
                        projectDirectory, null, key, PipelineForceRerun, progress, cancellationToken);
                    if (!result.Ok)
                        throw new InvalidOperationException(result.Message ?? $"{label}执行失败。");
                }

                if (PipelineAutoRepairEnabled)
                {
                    StatusMessage = "共享流水线：一键修复";
                    AppendActivityLog(StatusMessage);
                    await RunMaterialAutoRepairAsync(projectDirectory, progress, cancellationToken);
                }

                if (PipelineAutoFillEnabled)
                {
                    StatusMessage = "共享流水线：补齐字段";
                    AppendActivityLog(StatusMessage);
                    await _workService.AutoFillProjectInfoAsync(projectDirectory, null, cancellationToken);
                }

                if (PipelineRemuxEnabled)
                {
                    StatusMessage = "共享流水线：无损重封装";
                    AppendActivityLog(StatusMessage);
                    var remux = await _workService.RemuxUploadVideosAsync(projectDirectory, null, progress, cancellationToken);
                    if (!remux.Ok)
                        throw new InvalidOperationException(remux.Message);
                }

                if (PipelineMaterialValidateEnabled)
                {
                    StatusMessage = "共享流水线：素材校验";
                    AppendActivityLog(StatusMessage);
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

    private async Task RunMaterialAutoRepairAsync(
        string projectDirectory,
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
                projectDirectory, null, key, force, progress, cancellationToken);
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

        try
        {
            await adapter.RunAsync(job, progress, cancellationToken);
            job.Status = PublishJobStatus.Succeeded;
            job.StatusMessage = "发布流程执行完成";
            StatusMessage = $"[{job.ProjectName}] 发布完成";
            AppendActivityLog(StatusMessage);
            if (PipelineAutoArchiveAfterUpload && job.Kind == PublishJobKind.Series)
                await TryArchivePublishedProjectAsync(job, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            job.Status = PublishJobStatus.Pending;
            job.StatusMessage = "已停止，可继续执行";
            StatusMessage = $"[{job.ProjectName}] 已停止";
            AppendActivityLog(StatusMessage);
        }
        catch (Exception ex)
        {
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

    private void RefreshVisibleJobs(string? selectId = null)
    {
        void Refresh()
        {
            VisibleJobs.Clear();
            foreach (var job in _jobs
                         .Where(job => job.Platform == SelectedPlatform.Value)
                         .OrderByDescending(job => job.CreatedAt))
            {
                VisibleJobs.Add(new PublishJobRowViewModel(job));
            }

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
    }
}
