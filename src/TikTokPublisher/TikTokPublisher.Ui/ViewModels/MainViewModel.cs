using System.Collections.ObjectModel;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Remote;
using TikTokPublisher.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TikTokPublisher.Ui.Common;

using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.Services.TikTok;
using TikTokPublisher.Ui.Views;

namespace TikTokPublisher.Ui.ViewModels;

public sealed record FinalActionChoice(string Label, FinalAction Value);

public sealed record ManualInterventionDialogRequest(
    string WorkspaceRoot,
    string ProjectTitle,
    string ErrorMessage,
    string Hint);

public sealed record LocalManualDramaBatchImportResult(
    int RequestCount,
    int AddedCount,
    int ExistingCount,
    IReadOnlyList<LocalManualDramaImportResult> Results,
    IReadOnlyList<string> Failures)
{
    public int SuccessCount => Results.Count;
    public int FailedCount => Failures.Count;
    public IReadOnlyList<string> ProjectDirs => Results.Select(result => result.SourceProjectDir).ToArray();
    public string SummaryText =>
        $"本地剧集批量导入完成：请求 {RequestCount} 个，成功 {SuccessCount} 个，新增 {AddedCount} 个，已存在 {ExistingCount} 个，失败 {FailedCount} 个。";
}

public sealed record LocalUnimportedDuplicateDeletionResult(
    int DeletedDirectoryCount,
    IReadOnlyList<string> FailedImportDirs,
    IReadOnlyList<string> Failures);

public sealed record UploadTitleImportApplyOutcome(
    IReadOnlyList<string> OrderedProjectDirs,
    bool QueueWasRunning,
    IReadOnlyList<QueueProjectItem> AppendCandidates,
    int AppendedCount)
{
    public int AppendCandidateCount => AppendCandidates.Count;
}

public sealed record UploadTitleImportOutcome(
    UploadTitleImportResult ImportResult,
    WorkspaceQueueTarget RunTarget,
    UploadTitleImportApplyOutcome ApplyOutcome);

internal sealed record ManualUploadTitleImportTarget(
    WorkspaceQueueTarget QueueTarget,
    TikTokUploadTitleRoute Route,
    ClientSettings Settings,
    string AccountName)
{
    public string RouteLabel => $"{AccountName} · {Route.Describe()}";
}

public sealed record UploadTitleAutoRunPreparation(
    WorkspaceQueueTarget? RunTarget,
    int AppendedCount);

public sealed partial class MainViewModel : ViewModelBase
{
    public const string TikTokLoginUrl = TikTokUrls.DefaultLoginUrl;

    private sealed record RemoteUploadIdlePreparation(
        List<QueueProjectItem> AllItems,
        List<QueueProjectItem> AppendCandidates,
        QueueRunOptions RunOptions,
        int ClearedSelectionCount = 0);

    private sealed record WorkspaceQueueExecutionContext(
        string BatchId,
        TikTokAccountProfile? Account,
        string UploadEntryMode);

    private readonly AccountStore _store;
    private readonly AccountContextService _context;
    private readonly WorkspaceQueueOrchestrator _queueOrchestrator = new();
    private readonly QueueExecutionUseCase _queueExecution;
    private readonly QueueRetryUseCase _queueRetry;
    private readonly QueueStatePersistService _queueStatePersist = new();
    private readonly XingeRemoteCommandService _xingeRemoteCommandService = new();
    private readonly TikTokDispatchQueueScheduler _dispatchScheduler = new();
    private readonly Dictionary<string, QueueRunOptions> _dispatchOptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XingeAutoPublishProgress> _dispatchStartNotices = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _dispatchPump = new(1, 1);
    private readonly object _xingeUploadScanSync = new();
    private bool _xingeUploadScanRunning;
    private IReadOnlyDictionary<string, IReadOnlyList<TikTokUploadedDrama>?>? _lastXingeUploadedToday;
    private readonly object _localCheckedQueueLock = new();
    private readonly HashSet<string> _localCheckedQueueWorkspaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _checkedQueueFollowUpLock = new();
    private readonly Dictionary<string, PendingCheckedQueue> _checkedQueueFollowUps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _checkedQueueFollowUpArmed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _manualStopEpoch = new(StringComparer.OrdinalIgnoreCase);
    private string? _manualInterventionWorkspaceRoot;
    private bool _applyingQueueStepToggles;
    private bool _queueRunActive;
    private int _activeQueueRunCount;
    private string _displayedWorkspaceRoot = "";
    private readonly object _workspaceQueueSnapshotsLock = new();
    private readonly Dictionary<string, WorkspaceQueueSnapshot> _workspaceQueueSnapshots =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _queueExcelExportLock = new();
    private readonly HashSet<string> _pendingQueueExcelExportWorkspaces =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _queueFinalExcelExportScheduled;
    private bool _queueExcelExportDebounceScheduled;

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = new();
    public IReadOnlyList<AccountItemViewModel> FilteredAccounts { get; private set; } = [];
    private Avalonia.Threading.DispatcherTimer? _accountSearchTimer;
    private bool _refreshingFilteredAccounts;
    private bool _accountWorkspaceRefreshPending;

    public AccountItemViewModel? SidebarSelectedAccount
    {
        get => SelectedAccount is { } account && FilteredAccounts.Contains(account) ? account : null;
        set
        {
            if (!_refreshingFilteredAccounts && value is not null && FilteredAccounts.Contains(value))
                SelectedAccount = value;
        }
    }

    public void FlushActiveAccountSelection() => _store.FlushActivePointer();
    public ObservableCollection<PublishTaskItemViewModel> Tasks { get; } = new();
    public RangeObservableCollection<QueueProjectRowViewModel> QueueProjectRows { get; } = new();
    public RangeObservableCollection<QueueProjectRowViewModel> FilteredQueueProjectRows { get; } = new();

    public int QueueTotalCount => QueueProjectRows.Count;
    public int QueuePendingCount => QueueProjectRows.Count(row =>
        string.Equals(row.StatusText, QueueStepStatus.Pending, StringComparison.Ordinal));
    public int QueueRunningCount => QueueProjectRows.Count(row =>
        string.Equals(row.StatusText, QueueStepStatus.Running, StringComparison.Ordinal) ||
        string.Equals(row.StatusText, QueueStepStatus.WaitingUploadSlot, StringComparison.Ordinal) ||
        string.Equals(row.StatusText, QueueStepStatus.ManualIntervention, StringComparison.Ordinal));
    public int QueueCompletedCount => QueueProjectRows.Count(row =>
        string.Equals(row.StatusText, QueueStepStatus.Completed, StringComparison.Ordinal));
    public int QueueFailedCount => QueueProjectRows.Count(row => row.HasFailure);
    public int QueueStoppedCount => QueueProjectRows.Count(row =>
        string.Equals(row.StatusText, QueueStepStatus.Stopped, StringComparison.Ordinal));

    public event Action<ManualInterventionDialogRequest>? ManualInterventionDialogRequested;

    /// <summary>检测到 TikTok 单日创建剧集上限、队列已停止时触发（用于弹窗提示，对齐 Python）。</summary>
    public event Action<string>? DailyLimitReached;
    private bool _dailyLimitNotified;
    private readonly Dictionary<string, DateOnly> _dailyLimitReportedAccounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> _autoPublishResumeAbandoned = new();
    private readonly HashSet<long> _restartStoredStarted = new();
    private readonly HashSet<long> _stoppedThisSession = new();
    private bool _restartResumeChecked;
    private string? _restartResumeScope;
    private bool _restartResumeConfirmed;
    private bool _restartResumeDismissed;

    public IReadOnlyList<FinalActionChoice> FinalActionChoices { get; } = new[]
    {
        new FinalActionChoice("只填不发（安全）", FinalAction.None),
        new FinalActionChoice("保存草稿", FinalAction.Draft),
        new FinalActionChoice("直接发表", FinalAction.Publish),
    };

    [ObservableProperty] private AccountItemViewModel? _selectedAccount;
    [ObservableProperty] private string _statusMessage = "就绪";
    [ObservableProperty] private string _accountConfigSyncStatus = "";
    [ObservableProperty] private string _otherRunningStatusMessage = "";
    [ObservableProperty] private string _accountSearchText = "";
    [ObservableProperty] private string _queueSearchText = "";
    [ObservableProperty] private string _workspacePath = "";
    [ObservableProperty] private string _workspaceBindingSummary = "账号绑定：未绑定";
    [ObservableProperty] private string _queueSummaryText = "";
    [ObservableProperty] private FinalActionChoice _selectedFinalAction;
    [ObservableProperty] private int _maxParallel = 2;
    [ObservableProperty] private bool _showOnlyPendingUpload;
    [ObservableProperty] private bool _isQueueRunning;
    [ObservableProperty] private string _runningWorkspacesSummary = "";
    [ObservableProperty] private bool _forceRerunCompletedSteps;
    [ObservableProperty] private bool _autoArchiveAfterUpload;
    [ObservableProperty] private bool _preferUploadWhenReady;
    [ObservableProperty] private bool _syncManagementAfterUpload;
    [ObservableProperty] private bool _queueDownloadEnabled;
    [ObservableProperty] private bool _queueVideoDedupEnabled;
    [ObservableProperty] private bool _queueRewriteEnabled;
    [ObservableProperty] private bool _queueGeneratePosterEnabled;
    [ObservableProperty] private bool _queueGenerateEpisodeScriptEnabled;
    [ObservableProperty] private bool _queueGenerateAiScriptOutlineEnabled;
    [ObservableProperty] private bool _queueGenerateAiDramaMaterialsEnabled;
    [ObservableProperty] private bool _queueGenerateRoleVectorEnabled;
    [ObservableProperty] private bool _queueGenerateProofMaterialEnabled;
    [ObservableProperty] private bool _queueGenerateTimestampCertificateEnabled;
    [ObservableProperty] private bool _queueSmallVideoRepairEnabled;
    [ObservableProperty] private bool _queueVideoTranslateEnabled;
    [ObservableProperty] private bool _queueMaterialValidateEnabled;
    [ObservableProperty] private bool _queueUploadEnabled = true;
    [ObservableProperty] private int _todayUploadCount;
    [ObservableProperty] private bool _manualInterventionPending;
    [ObservableProperty] private string _manualInterventionHint = "";
    [ObservableProperty] private string _browserAuthStatus = "";

    public ObservableCollection<WorkspaceProjectItemViewModel> WorkspaceProjects { get; } = new();
    public ObservableCollection<WorkspaceProjectItemViewModel> FilteredWorkspaceProjects { get; } = new();

    public LogService Logs { get; } = new();
    public DramaDownloadViewModel DramaDownload { get; } = new();
    public SystemSettingsViewModel SystemSettings { get; }
    public SystemServicesViewModel SystemServices { get; } = new();
    public ArchivedProjectsViewModel ArchivedProjects { get; } = new();

    public event Action<string>? NavigatePageRequested;

    private List<QueueProjectItem> _queueItems = new();
    private QueueRunOptions _queueRunOptions = new();
    private readonly object _queueExecutionContextsLock = new();
    private readonly Dictionary<string, WorkspaceQueueExecutionContext> _queueExecutionContexts =
        new(StringComparer.OrdinalIgnoreCase);
    private int _workspaceRefreshGeneration;
    private DateTime _lastLogSnapshotUtc = DateTime.MinValue;
    private readonly Dictionary<string, QueueProjectRowViewModel> _queueRowByDir =
        new(StringComparer.OrdinalIgnoreCase);
    // 跨工作目录持久复用行 VM：来回切账号时避免重建全部行 VM 与重复订阅事件。
    private readonly Dictionary<string, QueueProjectRowViewModel> _rowVmCache =
        new(StringComparer.OrdinalIgnoreCase);
    private const int RowVmCacheMax = 2000;
    // 持久化回调合并：worker 每次状态变更都会回调，这里按工作目录合并到最多每 200ms 应用一次。
    private readonly object _persistCoalesceLock = new();
    private readonly Dictionary<string, (string Root, IReadOnlyList<QueueProjectItem> Items)> _pendingPersistByRoot =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _persistFlushScheduled;
    private static readonly TimeSpan PersistCoalesceInterval = TimeSpan.FromMilliseconds(200);
    private readonly object _todayUploadRefreshLock = new();
    private QueueProjectItem[] _pendingTodayUploadItems = [];
    private string _pendingTodayUploadAccountId = "";
    private string _pendingTodayUploadWorkspace = "";
    private string _pendingTodayUploadArchiveRoot = "";
    private bool _todayUploadRefreshScheduled;
    private int _todayUploadRefreshGeneration;
    private int _pendingTodayUploadGeneration;
    private int _appliedTodayUploadGeneration;
    private static readonly TimeSpan TodayUploadRefreshDelay = TimeSpan.FromMilliseconds(120);
    private readonly Dictionary<string, string> _lastProgressMessageByKey =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _queueSearchTextByAccount =
        new(StringComparer.OrdinalIgnoreCase);
    private const string DefaultQueueSearchAccountKey = "__default__";
    private static readonly TimeSpan UploadStatusPriorityGrace = TimeSpan.FromSeconds(3);
    private bool _uploadStatusPriorityActive;
    private DateTime _lastUploadStatusUtc = DateTime.MinValue;

    public event Action<AccountItemViewModel, string>? NavigateRequested;
    public event Action<TikTokAccountProfile>? AccountProfileNetworkChanged;
    public event Action<AccountItemViewModel>? AccountSwitchRequested;
    public event Action<AccountItemViewModel, bool>? AccountLoginRequested;
    public event Func<WorkspaceQueueTarget, QueueRunOptions?, Task<bool>>? RemoteQueueRunRequested;
    public event Func<QueueRunOptions?, IReadOnlyList<WorkspaceQueueTarget>, Task<bool>>? RemoteAllQueueRunRequested;
    // 与界面「执行勾选队列」相同：只跑当前勾选，步骤用界面上的勾选。
    public event Func<Task<bool>>? CheckedQueueRunRequested;

    public MainViewModel(AccountStore store, AccountContextService context,
        string? kuaishouConcurrencyDatabasePath = null)
    {
        SystemSettings = new SystemSettingsViewModel(kuaishouConcurrencyDatabasePath: kuaishouConcurrencyDatabasePath);
        _queueExecution = new QueueExecutionUseCase(_queueOrchestrator);
        _queueRetry = new QueueRetryUseCase(_queueExecution, _queueOrchestrator);
        _store = store;
        _context = context;
        _store.Load();
        foreach (var account in _store.Accounts)
            Accounts.Add(new AccountItemViewModel(account));
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == _store.ActiveAccountId) ?? Accounts.FirstOrDefault();
        _selectedFinalAction = FinalActionChoices[0];
        ArchivedProjects.AccountProvider = () => SelectedAccount?.Model;
        ArchivedProjects.AccountsProvider = () => Accounts.Select(item => item.Model).ToArray();
        ArchivedProjects.AccountResolver = ResolveAccountForQueueItem;
        ArchivedProjects.AccountUpdateRequested = _context.NotifyProfileUpdated;
        RefreshFilteredAccounts();
        RefreshWorkspaceFromActiveAccount();
        DramaDownload.LogRequested += AppendLog;
        WireSystemSettings();
        SystemServices.StatusRequested += message => StatusMessage = message;
        WireXingeRemoteCommandService();
        SystemServices.Load();
        ArchivedProjects.StatusRequested += message => StatusMessage = message;
        ArchivedProjects.Restored += () => RefreshWorkspaceProjects(WorkspacePath, force: true);
        DramaDownload.ImportToQueueRequested += ImportDramaProjectsToQueueAsync;
        DramaDownload.TikTokQueueTargetRequested += CaptureSelectedAccountQueueImportTarget;
        DramaDownload.TikTokQueueTargetsRequested += ListTikTokQueueImportTargets;
        DramaDownload.AccountProfileRequested += accountId => FindAccountById(accountId)?.Model;
        DramaDownload.ExistenceAccountsRequested += () => Accounts.Select(item => item.Model).ToArray();
        UpdateDramaDownloadQueueTarget();
        WireQueueOrchestrator();
    }

    private void WireSystemSettings()
    {
        SystemSettings.SettingsSaved += settings =>
        {
            DramaDownload.ApplyClientSettings(settings);
            if (!string.IsNullOrWhiteSpace(settings.LastDownloadWorkspace))
                DramaDownload.DownloadWorkspace = settings.LastDownloadWorkspace;
        };
        SystemSettings.StatusRequested += message => StatusMessage = message;
        SystemSettings.ProjectConcurrencySettings.TikTokLimitSaved += _queueOrchestrator.ApplyProjectLimit;
        SystemSettings.Load(WorkspacePath);
        SystemSettings.BeginHongguoAppPlaybackIdentity();
        DramaDownload.ChainPersisted += (chain, overseas) =>
        {
            if (overseas)
                SystemSettings.OverseasDramaSourceChain = chain;
            else
                SystemSettings.DramaSourceChain = chain;
        };
        DramaDownload.ApplyClientSettings(ClientSettingsStore.Load(), preferSavedWorkspace: true);
    }

    public MainViewModel()
    {
        SystemSettings = new SystemSettingsViewModel();
        _queueExecution = new QueueExecutionUseCase(_queueOrchestrator);
        _queueRetry = new QueueRetryUseCase(_queueExecution, _queueOrchestrator);
        var store = new AccountStore();
        _store = store;
        _context = new AccountContextService(store);
        _store.Load();
        foreach (var account in _store.Accounts)
            Accounts.Add(new AccountItemViewModel(account));
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == _store.ActiveAccountId) ?? Accounts.FirstOrDefault();
        _selectedFinalAction = FinalActionChoices[0];
        ArchivedProjects.AccountProvider = () => SelectedAccount?.Model;
        ArchivedProjects.AccountsProvider = () => Accounts.Select(item => item.Model).ToArray();
        ArchivedProjects.AccountResolver = ResolveAccountForQueueItem;
        ArchivedProjects.AccountUpdateRequested = _context.NotifyProfileUpdated;
        RefreshFilteredAccounts();
        RefreshWorkspaceFromActiveAccount();
        DramaDownload.LogRequested += AppendLog;
        WireSystemSettings();
        SystemServices.StatusRequested += message => StatusMessage = message;
        WireXingeRemoteCommandService();
        SystemServices.Load();
        ArchivedProjects.StatusRequested += message => StatusMessage = message;
        ArchivedProjects.Restored += () => RefreshWorkspaceProjects(WorkspacePath, force: true);
        DramaDownload.ImportToQueueRequested += ImportDramaProjectsToQueueAsync;
        DramaDownload.TikTokQueueTargetRequested += CaptureSelectedAccountQueueImportTarget;
        DramaDownload.TikTokQueueTargetsRequested += ListTikTokQueueImportTargets;
        DramaDownload.AccountProfileRequested += accountId => FindAccountById(accountId)?.Model;
        DramaDownload.ExistenceAccountsRequested += () => Accounts.Select(item => item.Model).ToArray();
        UpdateDramaDownloadQueueTarget();
        WireQueueOrchestrator();
    }

    private void WireQueueOrchestrator()
    {
        _queueOrchestrator.ManualInterventionPending += OnOrchestratorManualInterventionPending;
        _queueStatePersist.SetOnPersisted(OnQueueStatePersisted);
    }

    private void WireXingeRemoteCommandService()
    {
        SystemServices.SettingsSaved += RestartXingeRemoteCommandService;
        _xingeRemoteCommandService.AssignmentAlreadyLocal = AssignmentAlreadyLocal;
        _xingeRemoteCommandService.AssignmentVerifiedMissing = assignmentId =>
        {
            var seeds = Avalonia.Threading.Dispatcher.UIThread.CheckAccess()
                ? CaptureXingeAccountSeeds()
                : Avalonia.Threading.Dispatcher.UIThread.Invoke(CaptureXingeAccountSeeds);
            if (seeds.Count == 0)
                return false;
            var located = FindAutoPublishProject(assignmentId, seeds);
            return !located.LookupFailed && !located.ProjectExists && !LocalAutoPublishMarkerExists(assignmentId, located);
        };
        _xingeRemoteCommandService.LocalStoppedAssignmentIds = LocalStoppedAssignmentIds;
        _xingeRemoteCommandService.StatusChanged += OnXingeRemoteStatusChanged;
        XingeAutoPublishProgressReporter.Shared.Log += AppendLog;
        XingeUploadTaskReporter.Shared.Log += AppendLog;
    }

    public void StartXingeRemoteCommandService() => RestartXingeRemoteCommandService();

    public void StopXingeRemoteCommandService()
    {
        _xingeRemoteCommandService.Stop();
        XingeRuntimeConnection.ClearAutomatic();
        XingeRuntimeConnection.SetManualAvailable(false);
    }

    private void RestartXingeRemoteCommandService()
    {
        _xingeRemoteCommandService.Restart(
            ExecuteXingeRemoteCommandAsync,
            BuildXingeRemotePresenceSnapshotAsync,
            BuildXingeRemoteRegistrationSnapshotAsync,
            AppendLog,
            RecoverOpenAutoPublishAsync,
            OnAutoPublishPollIdle);
    }

    private void OnXingeRemoteStatusChanged(string message)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            ApplyXingeRemoteStatus(message);
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyXingeRemoteStatus(message));
    }

    private void ApplyXingeRemoteStatus(string message)
    {
        SystemServices.XingeRemoteStatus = message;
        if (!string.IsNullOrWhiteSpace(message))
            StatusMessage = message;
    }

    private async Task RecoverOpenAutoPublishAsync(IReadOnlyList<XingeRemoteCommandService.XingeOpenAutoPublishAssignment> open)
    {
        var connectionScope = XingeRuntimeConnection.Resolve(ClientSettingsStore.Load())?.Scope;
        if (connectionScope is null)
            return;
        if (_restartResumeScope != connectionScope)
        {
            _restartResumeScope = connectionScope;
            _restartResumeChecked = false;
            _restartResumeConfirmed = false;
            _restartResumeDismissed = false;
        }
        if (_restartResumeChecked && !_restartResumeConfirmed)
            return;
        if (open.Count == 0)
        {
            _restartResumeChecked = true;
            return;
        }

        var accounts = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(CaptureXingeAccountSeeds);
        var located = await Task.Run(() =>
        {
            var map = new Dictionary<long, AutoPublishProjectLocation>(open.Count);
            foreach (var assignment in open)
                map[assignment.Id] = FindAutoPublishProject(assignment.Id, accounts);
            return map;
        }).ConfigureAwait(false);

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (XingeRuntimeConnection.Resolve(ClientSettingsStore.Load())?.Scope != connectionScope)
                return;
            if (!_restartResumeChecked)
            {
                _restartResumeChecked = true;
                await ConfirmRestartResumeAsync(open, located);
            }
            if (!_restartResumeConfirmed || XingeRuntimeConnection.Resolve(ClientSettingsStore.Load())?.Scope != connectionScope)
                return;

            foreach (var assignment in open)
            {
                located.TryGetValue(assignment.Id, out var place);
                await ApplyRecoveredAutoPublishAsync(assignment, place);
            }

            _ = PumpAutoPublishQueuesAsync();
        });
    }

    // 重启后第一次准备自动续跑时提示一次。倒计时结束或点立即执行才开跑，关掉则本次启动不再自动续跑。
    private async Task ConfirmRestartResumeAsync(
        IReadOnlyList<XingeRemoteCommandService.XingeOpenAutoPublishAssignment> open,
        IReadOnlyDictionary<long, AutoPublishProjectLocation> located)
    {
        if (_restartResumeConfirmed || _restartResumeDismissed)
            return;

        var pending = new List<DispatchConfirmItem>();
        var anyReady = false;
        foreach (var assignment in open)
        {
            located.TryGetValue(assignment.Id, out var place);
            var resume = XingeAutoPublishRecovery.Decide(assignment.Status, place.Marker, place.ProjectExists) == XingeAutoPublishRecoveryAction.Resume;
            var stored = IsUnstartedStoredDispatch(assignment, place);
            if (!resume && !stored)
                continue;
            if (_autoPublishResumeAbandoned.Contains(assignment.Id) || _stoppedThisSession.Contains(assignment.Id))
                continue;
            var command = stored ? assignment.ReadCommand() : null;
            pending.Add(new DispatchConfirmItem
            {
                MessageId = assignment.Id,
                AssignmentId = assignment.Id,
                AccountName = FirstNonEmptyText(place.AccountName, command?.AccountProfileName, assignment.AccountProfileName),
                Title = FirstNonEmptyText(place.Marker?.Title, command?.Titles?.FirstOrDefault(title => !string.IsNullOrWhiteSpace(title)), assignment.Title),
                ConfirmSeconds = DispatchConfirmSession.DefaultCountdownSeconds,
            });
            if (stored || CanResumeAutoPublishNow(place))
                anyReady = true;
        }

        if (!anyReady || pending.Count == 0)
            return;

        var session = new DispatchConfirmSession(DateTimeOffset.Now, DispatchConfirmSession.DefaultCountdownSeconds);
        foreach (var item in pending)
            session.TryAdd(item);
        AppendLog($"客户端重启后将继续执行 {pending.Count} 部下发剧集，{DispatchConfirmSession.DefaultCountdownSeconds} 秒后自动开始。");
        if (await XingeRestartResumePrompt.ConfirmAsync(session))
        {
            _restartResumeConfirmed = true;
            AppendLog("已确认继续执行重启前下发的剧集。");
            return;
        }

        _restartResumeDismissed = true;
        AppendLog("已关闭继续执行提示，本次启动不再自动执行已下发剧集。");
    }

    private bool CanResumeAutoPublishNow(AutoPublishProjectLocation located)
    {
        if (!located.ProjectExists || string.IsNullOrWhiteSpace(located.Workspace) || string.IsNullOrWhiteSpace(located.ProjectDir))
            return false;
        return !IsLocalCheckedQueueRunning(located.Workspace) && !IsWorkspaceQueueRunning(located.Workspace);
    }

    private async Task ApplyRecoveredAutoPublishAsync(
        XingeRemoteCommandService.XingeOpenAutoPublishAssignment assignment,
        AutoPublishProjectLocation located)
    {
        if (_stoppedThisSession.Contains(assignment.Id))
            return;
        if (IsUnstartedStoredDispatch(assignment, located))
        {
            if (_restartResumeDismissed || !_restartResumeConfirmed)
                return;
            if (_restartStoredStarted.Contains(assignment.Id))
                return;
            var command = assignment.ReadCommand();
            if (command is null)
                return;
            var scope = XingeRuntimeConnection.Resolve(ClientSettingsStore.Load())?.Scope;
            if (scope is null || !await _xingeRemoteCommandService.TryStartRecoveredAssignmentAsync(assignment.Id, scope))
                return;
            _restartStoredStarted.Add(assignment.Id);
            AppendLog($"已分发未开始的剧继续执行：{FirstNonEmptyText(assignment.Title, command.Titles?.FirstOrDefault())}");
            await ExecuteRemoteCommandAsync(command with { XingeConnectionScope = scope });
            return;
        }

        var action = XingeAutoPublishRecovery.Decide(assignment.Status, located.Marker, located.ProjectExists);
        var model = new XingeAutoPublishAssignment(
            assignment.Id,
            $"{XingeAutoPublishProgressReporter.DefaultProgressPathPrefix}{assignment.Id}/progress",
            FirstNonEmptyText(located.Marker?.SeriesId, assignment.SeriesId),
            FirstNonEmptyText(located.Marker?.AccountProfileId, assignment.AccountProfileId),
            FirstNonEmptyText(located.Marker?.Title, assignment.Title),
            FirstNonEmptyText(located.Marker?.ConnectionScope, XingeRuntimeConnection.Resolve(ClientSettingsStore.Load())?.Scope));
        switch (action)
        {
            case XingeAutoPublishRecoveryAction.ReportSuccess:
                if (located.Marker is not null && !string.IsNullOrWhiteSpace(located.ProjectDir))
                    XingeAutoPublishMarkerStore.MarkFinal(located.ProjectDir, assignment.Id, XingeAutoPublishStatuses.Success);
                await ReportRecoveredAutoPublishAsync(model, XingeAutoPublishStatuses.Success, "upload_series", "本地项目已上传成功");
                break;
            case XingeAutoPublishRecoveryAction.ReportInterrupted:
                if (located.LookupFailed || LocalAutoPublishMarkerExists(assignment.Id, located))
                {
                    AppendLog($"自动上新派单 {assignment.Id} 的本地项目还在，或队列库暂时读不到，这次不向服务器报告项目丢失。");
                    break;
                }
                await ReportRecoveredAutoPublishAsync(model, assignment.Step == "importing" ? "import_not_queued" : XingeAutoPublishStatuses.Interrupted, "import", "客户端未找到本地项目");
                break;
            case XingeAutoPublishRecoveryAction.Resume:
                if (_restartResumeDismissed || !_restartResumeConfirmed)
                    break;
                if (_autoPublishResumeAbandoned.Contains(assignment.Id))
                    break;
                if (!string.IsNullOrWhiteSpace(located.ProjectDir))
                    XingeAutoPublishMarkerStore.MarkResumable(located.ProjectDir, assignment.Id);
                if (IsLocalCheckedQueueRunning(located.Workspace))
                {
                    AppendLog($"本地正在执行勾选队列，自动上新恢复等到该队列结束后再继续：{located.Workspace}");
                    break;
                }
                if (string.IsNullOrWhiteSpace(located.Workspace) || IsWorkspaceQueueRunning(located.Workspace))
                    break;
                if (!EnableRecoveredAutoPublishProject(located))
                {
                    AppendLog($"自动上新派单 {assignment.Id} 还没写进可执行队列，这次不释放，下一轮再继续。");
                    break;
                }
                var serverOptions = BuildRecoveredServerOptions(assignment);
                if (serverOptions is not null)
                    ApplyAutoPublishServerSteps(FindAccountById(located.AccountId)?.Model, located.Workspace, serverOptions);
                NoteAutoPublishDispatch(
                    located.Workspace,
                    located.AccountId,
                    [located.ProjectDir],
                    serverOptions,
                    XingeAutoPublishProgress.For(model, XingeAutoPublishStatuses.Running, "upload_series", "客户端重启后继续执行"));
                AppendLog($"自动上新派单 {assignment.Id} 已进入本机执行顺序。");
                break;
        }
    }

    private async Task ReportRecoveredAutoPublishAsync(XingeAutoPublishAssignment assignment, string status, string step, string detail)
    {
        var delivered = await XingeAutoPublishProgressReporter.Shared.ReportAsync(
            XingeAutoPublishProgress.For(assignment, status, step, detail),
            CancellationToken.None);
        if (!delivered)
            AppendLog($"自动上新派单 {assignment.AssignmentId} 的恢复结果暂未送达 XINGE，将在连接恢复后补发。");
    }

    private bool EnableRecoveredAutoPublishProject(AutoPublishProjectLocation located)
    {
        if (string.IsNullOrWhiteSpace(located.Workspace) || string.IsNullOrWhiteSpace(located.ProjectDir))
            return false;
        try
        {
            if (Directory.Exists(located.ProjectDir))
            {
                var account = FindAccountById(located.AccountId)?.Model;
                WorkspaceQueueService.AddProjectsToQueue(
                    located.Workspace,
                    [located.ProjectDir],
                    located.AccountId,
                    account?.DisplayName ?? located.AccountName);
            }
            var state = WorkspaceQueueDatabase.Load(located.Workspace);
            var enabled = XingeAutoPublishRecoveryQueue.EnableForResume(state.Items, new[] { located.ProjectDir });
            if (enabled.Count == 0)
                return false;
            WorkspaceQueueService.SaveProjects(located.Workspace, state.Items, state.Options);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLog($"自动上新派单勾选继续执行的项目失败：{ex.Message}");
            return false;
        }
    }

    private static QueueRunOptions? BuildRecoveredServerOptions(XingeRemoteCommandService.XingeOpenAutoPublishAssignment assignment)
    {
        if (assignment.EnabledSteps is not { Count: > 0 })
            return null;
        return TikTokRemoteRunOptions.BuildTikTokUploadRunOptions(
            new TikTokRemoteCommand(
                TikTokRemoteCommandNames.UploadSeries,
                EnabledSteps: assignment.EnabledSteps,
                QueueOptions: assignment.ReadQueueOptions()));
    }

    private async Task<bool> StartRecoveredAutoPublishQueueAsync(
        AutoPublishProjectLocation located,
        QueueRunOptions? serverOptions)
    {
        var run = RemoteQueueRunRequested;
        if (run is null || string.IsNullOrWhiteSpace(located.ProjectDir))
        {
            AppendLog($"自动上新派单已找到本地项目，但队列视图尚未初始化：{located.ProjectDir}");
            return false;
        }

        var started = await run(new WorkspaceQueueTarget(
            located.Workspace,
            located.AccountName,
            located.AccountId,
            new[] { located.ProjectDir },
            PreferPersistedQueueSnapshot: true), serverOptions);
        AppendLog(started
            ? $"自动上新派单已从中断处继续：{located.ProjectDir}"
            : $"自动上新派单暂时无法继续执行：{located.ProjectDir}");
        return started;
    }

    private static AutoPublishProjectLocation FindAutoPublishProject(
        long assignmentId,
        IReadOnlyList<XingeRemoteAccountSeed> accounts)
    {
        var seenWorkspaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scope = XingeRuntimeConnection.Resolve(ClientSettingsStore.Load())?.Scope;
        var lookupFailed = false;
        foreach (var account in accounts)
        {
            string workspace;
            try
            {
                workspace = account.ResolveWorkspacePath();
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(workspace) || !seenWorkspaces.Add(workspace))
                continue;

            IReadOnlyList<QueueProjectItem> items;
            try
            {
                items = WorkspaceQueueDatabase.Load(workspace).Items;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || ex.GetType().Name == "SqliteException")
            {
                lookupFailed = true;
                continue;
            }

            var located = FindAutoPublishProjectInItems(assignmentId, accounts, account, workspace, items, scope);
            if (located.ProjectExists)
                return located;

            located = FindAutoPublishProjectOnDisk(assignmentId, accounts, account, workspace, scope);
            if (located.ProjectExists)
                return located;
            if (located.LookupFailed)
                lookupFailed = true;
        }

        return new AutoPublishProjectLocation(null, false, "", "", "", "", LookupFailed: lookupFailed);
    }

    private static AutoPublishProjectLocation FindAutoPublishProjectInItems(
        long assignmentId,
        IReadOnlyList<XingeRemoteAccountSeed> accounts,
        XingeRemoteAccountSeed account,
        string workspace,
        IReadOnlyList<QueueProjectItem> items,
        string? scope)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ProjectDir))
                continue;
            var marker = XingeAutoPublishMarkerStore.TryRead(item.ProjectDir);
            if (marker is null || marker.AssignmentId != assignmentId || !MatchesXingeConnection(marker, scope))
                continue;
            var owner = accounts.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, marker.AccountProfileId, StringComparison.OrdinalIgnoreCase)) ?? account;
            return new AutoPublishProjectLocation(marker, true, workspace, item.ProjectDir, owner.Id, owner.Name);
        }

        return new AutoPublishProjectLocation(null, false, "", "", "", "");
    }

    // 队列库还没刷上磁盘时，项目目录里的派单标记仍然算找到了，不能向服务器报「项目丢失」。
    private static AutoPublishProjectLocation FindAutoPublishProjectOnDisk(
        long assignmentId,
        IReadOnlyList<XingeRemoteAccountSeed> accounts,
        XingeRemoteAccountSeed account,
        string workspace,
        string? scope)
    {
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(workspace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AutoPublishProjectLocation(null, false, workspace, "", "", "", LookupFailed: true);
        }

        foreach (var dir in children)
        {
            var marker = XingeAutoPublishMarkerStore.TryRead(dir);
            if (marker is null || marker.AssignmentId != assignmentId || !MatchesXingeConnection(marker, scope))
                continue;
            var owner = accounts.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, marker.AccountProfileId, StringComparison.OrdinalIgnoreCase)) ?? account;
            return new AutoPublishProjectLocation(marker, true, workspace, dir, owner.Id, owner.Name);
        }

        return new AutoPublishProjectLocation(null, false, "", "", "", "");
    }

    private static bool MatchesXingeConnection(XingeAutoPublishMarker marker, string? scope) =>
        string.IsNullOrEmpty(marker.ConnectionScope) || marker.ConnectionScope == scope;

    private static bool LocalAutoPublishMarkerExists(long assignmentId, AutoPublishProjectLocation located)
    {
        if (located.Marker?.AssignmentId == assignmentId)
            return true;
        if (string.IsNullOrWhiteSpace(located.ProjectDir))
            return false;
        var marker = XingeAutoPublishMarkerStore.TryRead(located.ProjectDir);
        return marker?.AssignmentId == assignmentId;
    }

    private readonly record struct AutoPublishProjectLocation(
        XingeAutoPublishMarker? Marker,
        bool ProjectExists,
        string Workspace,
        string ProjectDir,
        string AccountId,
        string AccountName,
        bool LookupFailed = false);

    private async Task<TikTokRemoteCommandResult> ExecuteXingeRemoteCommandAsync(TikTokRemoteCommand command)
    {
        await ClientFeaturePermissions.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        if (!ClientFeaturePermissions.Allows("tiktok"))
            return TikTokRemoteCommandResult.Failed(command.Command, "当前用户无 TikTok 功能权限");

        if (command.IsUploadCommand)
        {
            var seeds = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(CaptureXingeAccountSeeds);
            var facts = await Task.Run(() => XingeTikTokProcessedSeries.Collect(seeds)).ConfigureAwait(false);
            var source = FirstNonEmptyText(command.DownloadSource, command.SeriesSource);
            var specs = command.Series is { Count: > 0 }
                ? command.Series
                : (command.Titles ?? []).Select(title => new TikTokUploadSeriesSpec(title)).ToArray();
            var remaining = new List<TikTokUploadSeriesSpec>();
            var duplicates = new HashSet<XingeTikTokProcessedFact>();
            foreach (var item in specs)
            {
                var matches = XingeTikTokProcessedSeries.FindMatches(item, source, facts);
                if (matches.Count == 0)
                    remaining.Add(item);
                else
                    duplicates.UnionWith(matches);
            }
            if (duplicates.Count > 0 && remaining.Count == 0)
                return new TikTokRemoteCommandResult("duplicate_skipped", "重复跳过：客户端已有上传或归档记录", command.Command, ProcessedSeries: duplicates.Take(250).ToArray());
            if (duplicates.Count > 0)
                command = command with { Series = remaining, Titles = remaining.Select(item => item.Title).ToArray() };
            command = command with { UseXingeProcessedHistory = true };
        }
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            return await ExecuteRemoteCommandAsync(command);

        return await Avalonia.Threading.Dispatcher.UIThread
            .InvokeAsync(() => ExecuteRemoteCommandAsync(command));
    }

    private DateTimeOffset _lastXingeProcessedScanAt;



    private async Task<XingeRemoteRegistrationSnapshot> BuildXingeRemotePresenceSnapshotAsync(CancellationToken ct)
    {
        var captured = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(CaptureXingeRegistration);
        ct.ThrowIfCancellationRequested();
        return ComposeXingeRegistrationSnapshot(captured, ClientSettingsStore.Load(), uploadedToday: null);
    }

    private async Task<XingeRemoteRegistrationSnapshot> BuildXingeRemoteRegistrationSnapshotAsync(bool forceHistoryScan, CancellationToken ct)
    {
        var captured = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(CaptureXingeRegistration);
        ct.ThrowIfCancellationRequested();
        var settings = ClientSettingsStore.Load();
        var uploaded = await Task.Run(() => ScanUploadedToday(captured.Accounts), ct).ConfigureAwait(false);
        var snapshot = ComposeXingeRegistrationSnapshot(captured, settings, uploaded);
        if (!forceHistoryScan && DateTimeOffset.UtcNow - _lastXingeProcessedScanAt < TimeSpan.FromMinutes(2))
            return snapshot;
        try
        {
            var facts = await Task.Run(() => XingeTikTokProcessedSeries.Collect(captured.Accounts), ct).ConfigureAwait(false);
            _lastXingeProcessedScanAt = DateTimeOffset.UtcNow;
            return snapshot with { ProcessedSeries = facts };
        }
        catch (Exception) when (!ct.IsCancellationRequested && !forceHistoryScan)
        {
            AppendLog("读取 TikTok 上传和归档历史失败，下次连接将重试。");
            return snapshot;
        }
    }

    private readonly record struct XingeRegistrationCapture(
        string WorkspacePath,
        string ActiveAccountId,
        IReadOnlyList<XingeRemoteAccountSeed> Accounts);

    private XingeRegistrationCapture CaptureXingeRegistration() =>
        new(WorkspacePath ?? "", SelectedAccount?.Id ?? "", CaptureXingeAccountSeeds());

    private bool AssignmentAlreadyLocal(long assignmentId)
    {
        if (assignmentId <= 0)
            return false;
        IReadOnlyList<XingeRemoteAccountSeed> seeds;
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            seeds = CaptureXingeAccountSeeds();
        else
            seeds = Avalonia.Threading.Dispatcher.UIThread.Invoke(CaptureXingeAccountSeeds);
        var located = FindAutoPublishProject(assignmentId, seeds);
        return located.ProjectExists
            && (located.Marker?.IsActive == true || XingeAutoPublishRecovery.ShouldReclaimOnRestart(located.Marker));
    }

    private IReadOnlyList<long> LocalStoppedAssignmentIds()
    {
        var scope = XingeRuntimeConnection.Resolve(ClientSettingsStore.Load())?.Scope;
        IReadOnlyList<XingeRemoteAccountSeed> seeds;
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            seeds = CaptureXingeAccountSeeds();
        else
            seeds = Avalonia.Threading.Dispatcher.UIThread.Invoke(CaptureXingeAccountSeeds);
        var ids = new HashSet<long>();
        var seenWorkspaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in seeds)
        {
            string workspace;
            try
            {
                workspace = account.ResolveWorkspacePath();
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(workspace) || !seenWorkspaces.Add(workspace))
                continue;
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(workspace);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var dir in children)
            {
                var marker = XingeAutoPublishMarkerStore.TryRead(dir);
                if (marker is not null && MatchesXingeConnection(marker, scope) && XingeAutoPublishRecovery.ShouldReclaimOnRestart(marker))
                    ids.Add(marker.AssignmentId);
            }
        }

        return ids.ToArray();
    }

    private static bool IsUnstartedStoredDispatch(
        XingeRemoteCommandService.XingeOpenAutoPublishAssignment assignment,
        AutoPublishProjectLocation located)
    {
        if (!string.Equals(assignment.Status, "dispatched", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(assignment.MessageStatus, "sent", StringComparison.OrdinalIgnoreCase))
            return false;
        if (located.ProjectExists && located.Marker?.AssignmentId == assignment.Id)
            return false;
        return assignment.ReadCommand() is { IsAutoPublishAssignment: true };
    }

    private IReadOnlyList<XingeRemoteAccountSeed> CaptureXingeAccountSeeds()
    {
        var activeId = SelectedAccount?.Id ?? "";
        var seeds = new List<XingeRemoteAccountSeed>(Accounts.Count);
        foreach (var account in Accounts)
        {
            var model = account.Model;
            var authPath = "";
            try
            {
                authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(model);
            }
            catch
            {
                authPath = "";
            }

            seeds.Add(new XingeRemoteAccountSeed(
                account.Id,
                account.DisplayName,
                authPath,
                string.Equals(account.Id, activeId, StringComparison.OrdinalIgnoreCase),
                model.TiktokUploadProfilePath ?? "",
                model.LastWorkspace ?? "",
                model.TiktokArchiveRootDir ?? "",
                TikTokAccountProfile.NormalizeDramaMarket(model.TiktokDramaMarket)));
        }

        return seeds;
    }

    private IReadOnlyDictionary<string, IReadOnlyList<TikTokUploadedDrama>?>? ScanUploadedToday(
        IReadOnlyList<XingeRemoteAccountSeed> accounts)
    {
        lock (_xingeUploadScanSync)
        {
            if (_xingeUploadScanRunning)
                return _lastXingeUploadedToday;
            _xingeUploadScanRunning = true;
        }

        try
        {
            var scanned = XingeUploadedTodayScan.Collect(accounts);
            lock (_xingeUploadScanSync)
                _lastXingeUploadedToday = scanned;
            return scanned;
        }
        catch (Exception ex)
        {
            AppendLog($"同步今日已上传剧目失败：{ex.Message}");
            return null;
        }
        finally
        {
            lock (_xingeUploadScanSync)
                _xingeUploadScanRunning = false;
        }
    }

    private static XingeRemoteRegistrationSnapshot ComposeXingeRegistrationSnapshot(
        XingeRegistrationCapture captured,
        ClientSettings settings,
        IReadOnlyDictionary<string, IReadOnlyList<TikTokUploadedDrama>?>? uploadedToday)
    {
        var accounts = captured.Accounts
            .Select(account =>
            {
                IReadOnlyList<XingeRemoteUploadedDrama>? uploaded = null;
                if (uploadedToday is not null && uploadedToday.TryGetValue(account.Id, out var dramas) && dramas is not null)
                {
                    uploaded = dramas
                        .Select(item => new XingeRemoteUploadedDrama(item.SeriesId, item.Title, item.CompletedAt))
                        .ToArray();
                }

                var market = TikTokAccountProfile.NormalizeDramaMarket(account.DramaMarket);
                return new XingeRemoteAccountSnapshot(
                    account.Id,
                    account.Name,
                    HasAccountAuthFile(account.AuthPath),
                    account.IsCurrent,
                    uploaded,
                    DramaMarket: market,
                    DramaSource: VisibleDramaSource.ChainFor(settings, market));
            })
            .ToArray();

        return new XingeRemoteRegistrationSnapshot(
            captured.WorkspacePath,
            captured.ActiveAccountId,
            VisibleDramaSource.ChainFor(settings, TikTokAccountProfile.DramaMarketLocal),
            accounts);
    }

    private static bool HasAccountAuthFile(string authPath)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(authPath) && File.Exists(authPath);
        }
        catch
        {
            return false;
        }
    }

    private void OnOrchestratorManualInterventionPending(QueueProjectItem item, string errorMessage, string workspaceRoot)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _manualInterventionWorkspaceRoot = workspaceRoot;
            ManualInterventionPending = _queueOrchestrator.HasManualInterventionPending;
            if (ManualInterventionPending)
            {
                var title = string.IsNullOrWhiteSpace(item.Title) ? item.DisplayName : item.Title;
                ManualInterventionHint =
                    $"[{workspaceRoot}] 「{title}」上传失败，浏览器已保持打开。请在弹窗中选择人工处理后继续，或跳过此项目。错误：{errorMessage}";
                ManualInterventionDialogRequested?.Invoke(new ManualInterventionDialogRequest(
                    workspaceRoot,
                    title,
                    errorMessage,
                    ManualInterventionHint));
            }
            else
            {
                ManualInterventionHint = "";
                _manualInterventionWorkspaceRoot = null;
            }
        });
    }

    public bool ResolveManualIntervention(string action, string? workspaceRoot = null)
    {
        var handled = _queueOrchestrator.ResolveManualIntervention(action, workspaceRoot ?? _manualInterventionWorkspaceRoot);
        if (handled)
        {
            ManualInterventionPending = _queueOrchestrator.HasManualInterventionPending;
            if (!ManualInterventionPending)
            {
                ManualInterventionHint = "";
                _manualInterventionWorkspaceRoot = null;
            }
        }
        return handled;
    }

    private void RefreshRunningWorkspacesSummary()
    {
        var running = _queueOrchestrator.Snapshot().Where(item => item.IsRunning).ToList();
        RunningWorkspacesSummary = running.Count == 0
            ? ""
            : $"并行队列：{string.Join(" | ", running.Select(item => item.DisplayLabel))}";
        IsQueueRunning = _queueRunActive || _queueOrchestrator.AnyRunning;
    }

    private readonly AsyncLocal<string?> _queueLogAccount = new();
    private bool _showAllLogAccounts;
    private bool _logScopeEnabled;

    public void AppendLog(string text) => AppendLog(text, _queueLogAccount.Value ?? SelectedAccount?.Id);

    private void AppendLog(string text, string? accountId)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AppendLog(text, accountId));
            return;
        }

        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var level = LogService.InferLevel(text);
        Logs.Append($"[{timestamp}] {LogService.FormatLevel(level)} {text}", accountId);
    }

    public void SetLogAccountScope(bool all)
    {
        _logScopeEnabled = true;
        _showAllLogAccounts = all;
        RefreshLogSnapshot(force: true);
    }

    public void RefreshLogSnapshot(bool force = false)
    {
        if (!force && (DateTime.UtcNow - _lastLogSnapshotUtc).TotalMilliseconds < 1500)
            return;
        _lastLogSnapshotUtc = DateTime.UtcNow;
        if (!_logScopeEnabled)
        {
            Logs.UpdateSnapshot(QueueProjectRows, WorkspacePath, IsQueueRunning);
            return;
        }

        var account = SelectedAccount;
        Logs.SetAccountScope(_showAllLogAccounts ? null : account?.Id ?? "");
        var workspace = account?.Model.ResolveWorkspacePath();
        var rows = QueueProjectRows.Where(row =>
            string.Equals(row.Item.AccountProfileId, account?.Id, StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrWhiteSpace(row.Item.AccountProfileId) &&
             string.Equals(workspace, WorkspacePath, StringComparison.OrdinalIgnoreCase)));
        Logs.UpdateSnapshot(_showAllLogAccounts ? [] : rows, _showAllLogAccounts ? null : workspace,
            !_showAllLogAccounts && !string.IsNullOrWhiteSpace(workspace) && IsWorkspaceQueueRunning(workspace));
    }

    public void RequestNavigate(string pageTag) => NavigatePageRequested?.Invoke(pageTag);

    partial void OnAccountSearchTextChanged(string value)
    {
        if (_accountSearchTimer is null)
        {
            _accountSearchTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _accountSearchTimer.Tick += (_, _) => RefreshFilteredAccounts();
        }

        _accountSearchTimer.Stop();
        _accountSearchTimer.Start();
    }

    partial void OnQueueSearchTextChanged(string value)
    {
        _queueSearchTextByAccount[GetQueueSearchAccountKey()] = value ?? "";
        ApplyQueueProjectFilter();
    }

    partial void OnAutoArchiveAfterUploadChanged(bool value)
    {
        _queueRunOptions.AutoArchiveAfterUpload = value;
        PersistQueueRunOptions();
        PersistAccountQueueSettings();
    }

    partial void OnPreferUploadWhenReadyChanged(bool value)
    {
        _queueRunOptions.PreferUploadWhenReady = value;
        PersistQueueRunOptions();
        PersistAccountQueueSettings();
    }

    partial void OnSyncManagementAfterUploadChanged(bool value)
    {
        _queueRunOptions.SyncManagementAfterUpload = value;
        PersistQueueRunOptions();
        PersistAccountQueueSettings();
    }

    public AccountItemViewModel? FindAccount(string nameOrId)
    {
        var profile = _store.FindByNameOrId(nameOrId);
        return profile is null ? null : Accounts.FirstOrDefault(a => a.Id == profile.Id);
    }

    private AccountItemViewModel? FindAccountById(string? accountId)
    {
        var id = (accountId ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(id)
            ? null
            : Accounts.FirstOrDefault(account =>
                string.Equals(account.Id, id, StringComparison.Ordinal));
    }

    partial void OnSelectedAccountChanged(AccountItemViewModel? value)
    {
        OnPropertyChanged(nameof(SidebarSelectedAccount));
        DramaDownload.UseAccountMarket(value?.Model.TiktokDramaMarket);
        UpdateDramaDownloadQueueTarget();
        if (value is null)
        {
            RestoreQueueSearchTextForSelectedAccount();
            return;
        }

        if (_store.ActiveAccountId == value.Id)
        {
            RestoreQueueSearchTextForSelectedAccount();
            return;
        }

        _context.SwitchTo(value.Id, deferPersistence: true);
        RestoreQueueSearchTextForSelectedAccount();
        Interlocked.Increment(ref _workspaceRefreshGeneration);
        if (_accountWorkspaceRefreshPending)
            return;

        _accountWorkspaceRefreshPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _accountWorkspaceRefreshPending = false;
            if (SelectedAccount is not { } current)
                return;

            RefreshWorkspaceFromActiveAccount();
            AccountSwitchRequested?.Invoke(current);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private string GetQueueSearchAccountKey()
    {
        var accountId = SelectedAccount?.Id;
        return string.IsNullOrWhiteSpace(accountId) ? DefaultQueueSearchAccountKey : accountId.Trim();
    }

    private void RestoreQueueSearchTextForSelectedAccount()
    {
        var key = GetQueueSearchAccountKey();
        var text = _queueSearchTextByAccount.TryGetValue(key, out var stored) ? stored : "";
        if (!string.Equals(QueueSearchText, text, StringComparison.Ordinal))
            QueueSearchText = text;
        else
            ApplyQueueProjectFilter();
    }

    partial void OnForceRerunCompletedStepsChanged(bool value)
    {
        _queueRunOptions.ForceRerunCompletedSteps = value;
        PersistQueueRunOptions();
    }

    partial void OnQueueDownloadEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueVideoDedupEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueRewriteEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueGeneratePosterEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueGenerateEpisodeScriptEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueGenerateAiDramaMaterialsEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueGenerateAiScriptOutlineEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueGenerateRoleVectorEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueGenerateProofMaterialEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueGenerateTimestampCertificateEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueUploadEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueSmallVideoRepairEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueVideoTranslateEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueMaterialValidateEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();

    private void UpdateQueueRunOptionsFromUi()
    {
        if (_applyingQueueStepToggles) return;
        SyncEnabledStepsFromUi();
        PersistQueueRunOptions();
        PersistAccountQueueSettings();
    }

    [RelayCommand]
    private void AddAccount()
    {
        var account = _store.Add($"账号{Accounts.Count + 1}");
        var vm = new AccountItemViewModel(account);
        Accounts.Add(vm);
        RefreshFilteredAccounts();
        SelectedAccount = vm;
        StatusMessage = $"已添加「{account.Name}」，点击「登录」完成 TikTok 登录";
    }

    internal void MoveSidebarAccount(AccountItemViewModel account, AccountItemViewModel target, bool after)
    {
        var previous = Accounts.IndexOf(account);
        if (previous < 0 || !Accounts.Contains(target))
            return;
        try
        {
            if (_store.MoveRelativeTo(account.Id, target.Id, after) is not { } destination)
                return;
            Accounts.Move(previous, destination);
            RefreshFilteredAccounts();
            StatusMessage = "已保存 TikTok 账号顺序";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"保存账号顺序失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void RemoveAccount() => RemoveSelectedAccount();

    public void RemoveSelectedAccount()
    {
        if (SelectedAccount is null) return;
        var vm = SelectedAccount;
        _store.Remove(vm.Model);
        Accounts.Remove(vm);
        SelectedAccount = Accounts.Count > 0 ? Accounts[0] : null;
        RefreshFilteredAccounts();
        _xingeRemoteCommandService.RequestAccountRosterSync();
        StatusMessage = $"已删除「{vm.DisplayName}」";
    }

    public void RenameAccount(string newName)
    {
        if (SelectedAccount is null) return;
        if (!_store.Rename(SelectedAccount.Id, newName))
        {
            StatusMessage = "重命名失败：名称不能为空";
            return;
        }

        SelectedAccount.RefreshFromModel();
        RefreshFilteredAccounts();
        StatusMessage = $"已重命名为「{newName}」";
    }

    public void BeginAccountLogin(bool forceRelogin = false)
    {
        if (SelectedAccount is null)
        {
            StatusMessage = "请先在左侧选择一个账号";
            return;
        }

        if (forceRelogin)
            AccountLoginStatusHelper.DeleteAuthState(SelectedAccount.Model);

        var usesExternalBrowser = string.Equals(
            SelectedAccount.Model.TiktokUploadBrowserMode,
            "playwright",
            StringComparison.OrdinalIgnoreCase);
        var browserLabel = usesExternalBrowser ? "外部浏览器" : "内置浏览器";
        SelectedAccount.Status = AccountStatus.LoggingIn;
        SaveAccountProfile(SelectedAccount.Model);
        if (!forceRelogin && TikTokUploadChannels.IsApi(SelectedAccount.Model.TiktokUploadStrategy))
        {
            BrowserAuthStatus = "正在校验 TikTok 登录状态";
            StatusMessage = $"[{SelectedAccount.DisplayName}] 正在通过接口校验已有登录状态";
            AccountLoginRequested?.Invoke(SelectedAccount, false);
            return;
        }
        BrowserAuthStatus = forceRelogin
            ? $"请在{browserLabel}中重新完成 TikTok 登录"
            : $"请在{browserLabel}中完成 TikTok 登录";
        StatusMessage = forceRelogin
            ? $"[{SelectedAccount.DisplayName}] 正在重新打开{browserLabel}，请完成 TikTok 登录"
            : $"[{SelectedAccount.DisplayName}] 正在打开{browserLabel}，请完成 TikTok 登录";

        AccountLoginRequested?.Invoke(SelectedAccount, forceRelogin);
        if (!usesExternalBrowser)
            NavigatePageRequested?.Invoke("browser");
    }

    public void HandleEmbeddedAuthSaved(AccountItemViewModel account, EmbeddedAuthSaveResult result)
    {
        SaveAccountProfile(account.Model);
        account.Status = AccountStatus.Online;
        account.RefreshFromModel();
        BrowserAuthStatus = $"授权已保存（{result.CookieCount} 个 Cookie）";
        StatusMessage = $"[{account.DisplayName}] TikTok 登录成功，授权已保存";
        AppendLog(StatusMessage);
    }

    public void HandleExternalAuthSaved(AccountItemViewModel account, TikTokLoginResult result)
    {
        account.Model.TiktokStorageStatePath = result.AuthPath;
        account.Model.TiktokLastLoginEmail = result.Email;
        account.Model.TiktokLastLoginAt = result.LoggedInAt;
        SaveAccountProfile(account.Model);
        account.Status = AccountStatus.Online;
        account.RefreshFromModel();
        BrowserAuthStatus = "外部浏览器授权已保存";
        StatusMessage = $"[{account.DisplayName}] TikTok 登录成功，外部浏览器授权已保存";
        AppendLog(StatusMessage);
    }

    public void HandleApiLoginVerified(AccountItemViewModel account)
    {
        account.Status = AccountStatus.Online;
        if (ReferenceEquals(SelectedAccount, account))
            BrowserAuthStatus = "已有登录状态已通过接口校验";
        StatusMessage = $"[{account.DisplayName}] TikTok 已登录，接口会话可用";
        AppendLog(StatusMessage);
    }

    public void HandleEmbeddedAuthSaveFailed(string message)
    {
        BrowserAuthStatus = "保存失败";
        StatusMessage = $"保存 TikTok 授权失败：{message}";
        AppendLog(StatusMessage);
    }

    public void HandleEmbeddedAuthStatusChanged(string message) =>
        BrowserAuthStatus = message;

    public async Task SyncExpectedPriceOptionsAsync(TikTokAccountProfile profile, CancellationToken ct = default)
    {
        StatusMessage = "正在同步预期全集价格选项…";
        try
        {
            var options = await TikTokExpectedPriceSyncService.FetchAsync(
                profile,
                msg => AppendLog(msg),
                ct);

            profile.TiktokExpectedFullPriceOptionsJson = ExpectedFullPriceOptionsJson.Serialize(options);
            var currentValue = (profile.TiktokExpectedFullPriceValue ?? "").Trim();
            var knownValues = options.Select(o => o.Value).ToHashSet(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(currentValue) && !knownValues.Contains(currentValue))
            {
                profile.TiktokExpectedFullPriceValue = "";
                profile.TiktokExpectedFullPriceLabel = "";
            }

            SaveAccountProfile(profile);
            StatusMessage = $"已同步 {options.Count} 个预期全集价格选项";
        }
        catch (Exception ex)
        {
            StatusMessage = $"同步价格选项失败：{ex.Message}";
            AppendLog($"同步价格选项失败：{ex.Message}");
            throw;
        }
    }

    public async Task SyncPublishAccountsAsync(
        TikTokAccountProfile profile,
        IReadOnlyList<string> selectedKeys,
        CancellationToken ct = default)
    {
        StatusMessage = "正在获取已创建发布账号…";
        try
        {
            var accounts = await TikTokPublishAccountSyncService.FetchAsync(
                profile,
                AppendLog,
                ct);
            var retention = TikTokPublishAccountCatalog.Retain(selectedKeys, accounts);
            profile.TiktokPublishAccountCatalogJson = TikTokPublishAccountCatalog.Serialize(accounts);
            profile.TiktokSelectedPublishAccountKeys = retention.KeptKeys.ToList();
            profile.TiktokCustomPublishAccountsEnabled = true;
            if (retention.DroppedKeys.Count > 0)
            {
                AppendLog(
                    "以下已选发布账号已不在最新列表中，已取消勾选：" +
                    string.Join("、", retention.DroppedKeys.Select(TikTokPublishAccountCatalog.DescribeKey)));
            }

            SaveAccountProfile(profile);
            StatusMessage = $"已获取 {accounts.Count} 个发布账号";
            AppendLog(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = $"获取发布账号失败：{ex.Message}";
            AppendLog($"获取发布账号失败：{ex.Message}");
            throw;
        }
    }

    [RelayCommand]
    private void SelectAllQueueSteps()
    {
        _applyingQueueStepToggles = true;
        try
        {
            QueueDownloadEnabled = true;
            QueueVideoDedupEnabled = true;
            QueueRewriteEnabled = true;
            QueueGeneratePosterEnabled = true;
            QueueGenerateEpisodeScriptEnabled = true;
            QueueGenerateAiScriptOutlineEnabled = true;
            QueueGenerateAiDramaMaterialsEnabled = true;
            QueueGenerateRoleVectorEnabled = true;
            QueueGenerateProofMaterialEnabled = true;
            QueueGenerateTimestampCertificateEnabled = true;
            QueueSmallVideoRepairEnabled = true;
            QueueVideoTranslateEnabled = false;
            QueueMaterialValidateEnabled = true;
            QueueUploadEnabled = true;
            SyncManagementAfterUpload = true;
            AutoArchiveAfterUpload = true;
            PreferUploadWhenReady = true;
            // “全选步骤”不应开启破坏性较强的重跑选项，必须由用户单独勾选。
            ForceRerunCompletedSteps = false;
        }
        finally
        {
            _applyingQueueStepToggles = false;
        }
        UpdateQueueRunOptionsFromUi();
    }

    [RelayCommand]
    private void ClearAllQueueSteps()
    {
        _applyingQueueStepToggles = true;
        try
        {
            QueueDownloadEnabled = false;
            QueueVideoDedupEnabled = false;
            QueueRewriteEnabled = false;
            QueueGeneratePosterEnabled = false;
            QueueGenerateEpisodeScriptEnabled = false;
            QueueGenerateAiScriptOutlineEnabled = false;
            QueueGenerateAiDramaMaterialsEnabled = false;
            QueueGenerateRoleVectorEnabled = false;
            QueueGenerateProofMaterialEnabled = false;
            QueueGenerateTimestampCertificateEnabled = false;
            QueueSmallVideoRepairEnabled = false;
            QueueVideoTranslateEnabled = false;
            QueueMaterialValidateEnabled = false;
            QueueUploadEnabled = false;
            SyncManagementAfterUpload = false;
            AutoArchiveAfterUpload = false;
            PreferUploadWhenReady = false;
            ForceRerunCompletedSteps = false;
        }
        finally
        {
            _applyingQueueStepToggles = false;
        }
        UpdateQueueRunOptionsFromUi();
    }

    public void RefreshFilteredAccounts()
    {
        _accountSearchTimer?.Stop();
        var query = (AccountSearchText ?? "").Trim();
        var matches = Accounts.Where(account =>
            query.Length == 0
            || account.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || account.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || account.LoginEmail.Contains(query, StringComparison.OrdinalIgnoreCase)
            || account.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (FilteredAccounts.SequenceEqual(matches))
            return;

        _refreshingFilteredAccounts = true;
        try
        {
            FilteredAccounts = matches;
            OnPropertyChanged(nameof(FilteredAccounts));
            OnPropertyChanged(nameof(SidebarSelectedAccount));
        }
        finally
        {
            _refreshingFilteredAccounts = false;
        }
    }

    private void UpdateWorkspaceBindingSummary(string root)
    {
        WorkspaceBindingSummary = SelectedAccount is null
            ? "当前账号：未选择"
            : $"当前账号：{SelectedAccount.DisplayName}";
    }

    private void UpdateQueueSummaryText(bool refreshTodayUploadCount = true)
    {
        var checkedCount = _queueItems.Count(i => i.Enabled);
        var pending = WorkspaceQueueService.FilterPendingUpload(_queueItems).Count();
        QueueSummaryText =
            $"已加载 { _queueItems.Count} 个项目，勾选 {checkedCount} 个，待上传 {pending} 个" +
            (string.IsNullOrWhiteSpace(WorkspacePath) ? "" : $" · 工作目录 {WorkspacePath}");
        if (refreshTodayUploadCount)
            RefreshTodayUploadCount();
    }

    /// <summary>今日上传完成数：按当前账号隔离统计（对齐 Python <c>_count_today_uploaded_projects</c>）。</summary>
    public void RefreshTodayUploadCount()
    {
        lock (_todayUploadRefreshLock)
        {
            _pendingTodayUploadItems = _queueItems.ToArray();
            _pendingTodayUploadAccountId = SelectedAccount?.Id ?? "";
            _pendingTodayUploadWorkspace = WorkspacePath ?? "";
            _pendingTodayUploadArchiveRoot =
                SelectedAccount?.Model.ResolveArchiveRootPath(WorkspacePath) ?? "";
            _pendingTodayUploadGeneration = ++_todayUploadRefreshGeneration;
            if (_todayUploadRefreshScheduled)
                return;

            _todayUploadRefreshScheduled = true;
        }

        Avalonia.Threading.DispatcherTimer.RunOnce(
            FlushPendingTodayUploadCountRefresh,
            TodayUploadRefreshDelay);
    }

    private void FlushPendingTodayUploadCountRefresh()
    {
        QueueProjectItem[] items;
        string accountId;
        string workspace;
        string archiveRoot;
        int generation;
        lock (_todayUploadRefreshLock)
        {
            _todayUploadRefreshScheduled = false;
            items = _pendingTodayUploadItems;
            accountId = _pendingTodayUploadAccountId;
            workspace = _pendingTodayUploadWorkspace;
            archiveRoot = _pendingTodayUploadArchiveRoot;
            generation = _pendingTodayUploadGeneration;
        }

        _ = Task.Run(() =>
        {
            try
            {
                return TikTokTodayUploadCountService.CountTodayUploads(
                    items,
                    accountId,
                    workspace,
                    archiveRootDir: archiveRoot);
            }
            catch
            {
                return -1;
            }
        }).ContinueWith(task =>
        {
            if (task.IsCanceled || task.IsFaulted)
                return;
            if (task.Result < 0)
                return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // 队列重新加载会再排一次统计。先到的结果只要还是当前账号就先显示，
                // 更晚的一次算完后再覆盖，避免切账号后一直停在上一个数字。
                if (!string.Equals(accountId, _pendingTodayUploadAccountId, StringComparison.Ordinal) ||
                    !string.Equals(workspace, _pendingTodayUploadWorkspace, StringComparison.OrdinalIgnoreCase) ||
                    generation < _appliedTodayUploadGeneration)
                    return;

                _appliedTodayUploadGeneration = generation;
                TodayUploadCount = task.Result;
            });
        });
    }

    [RelayCommand]
    private void Login() => BeginAccountLogin(forceRelogin: false);

    [RelayCommand]
    private void Relogin() => BeginAccountLogin(forceRelogin: true);

    private static string NormalizeWorkspacePath(string path)
    {
        var workspace = (path ?? "").Trim();
        if (string.IsNullOrWhiteSpace(workspace)) return "";

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(workspace));
        }
        catch
        {
            return workspace;
        }
    }

    public void RefreshWorkspaceProjects(string? workspaceRoot = null, bool force = false) =>
        _ = RefreshWorkspaceProjectsAsync(workspaceRoot, force);

    public Task RefreshWorkspaceProjectsNowAsync(string? workspaceRoot = null, bool force = false) =>
        RefreshWorkspaceProjectsAsync(workspaceRoot, force);

    public async Task ApplyPreparedWorkspaceQueueSnapshotAsync(
        string workspaceRoot,
        IReadOnlyList<QueueProjectItem> items,
        QueueRunOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);
        var root = Path.GetFullPath(workspaceRoot);
        var itemSnapshot = CloneQueueItems(items);
        var optionSnapshot = options.Clone();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (IsActiveWorkspace(root))
                ApplyWorkspaceScanResult(root, itemSnapshot, optionSnapshot);
            else
                CacheWorkspaceQueueSnapshot(root, itemSnapshot, optionSnapshot);
        });
    }

    private async Task RefreshWorkspaceProjectsAsync(string? workspaceRoot = null, bool force = false)
    {
        var generation = Interlocked.Increment(ref _workspaceRefreshGeneration);
        var root = (workspaceRoot ?? WorkspacePath).Trim();
        if (string.IsNullOrEmpty(root))
        {
            await ClearWorkspaceProjectsOnUiAsync(generation);
            return;
        }

        // Exists 会碰到正在写视频的磁盘。放在 UI 线程上时，执行中点左侧账号会停住。
        var rootExists = await Task.Run(() => Directory.Exists(root)).ConfigureAwait(false);
        if (generation != Volatile.Read(ref _workspaceRefreshGeneration))
            return;
        if (!rootExists)
        {
            await ClearWorkspaceProjectsOnUiAsync(generation);
            return;
        }

        var showedData = false;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _workspaceRefreshGeneration) return;
            // A forced refresh is used after external filesystem/database mutations such
            // as restoring archived projects. Reusing the pre-restore snapshot here would
            // make the archive entry disappear while the restored project remained absent
            // from the production queue until the application was restarted.
            if (!force &&
                TryGetWorkspaceQueueSnapshot(root, out var cachedItems, out var cachedOptions))
            {
                ApplyWorkspaceScanResult(root, cachedItems, cachedOptions);
                showedData = true;
                return;
            }

            if (!string.Equals(_displayedWorkspaceRoot, SafeFullPath(root), StringComparison.OrdinalIgnoreCase))
                ShowWorkspaceLoadingState(root);
        }, Avalonia.Threading.DispatcherPriority.Background);

        // On a cold app start there is no in-memory snapshot. Populate the table from the
        // queue database first (normally tens of milliseconds) and reconcile filesystem
        // artifacts in the slower scan below. This avoids presenting an empty workspace
        // for the entire duration of a cold disk scan.
        if (generation != Volatile.Read(ref _workspaceRefreshGeneration))
            return;

        if (!showedData)
        {
            var persistedSnapshot = await Task.Run(() => WorkspaceQueueService.LoadPersistedSnapshot(root))
                .ConfigureAwait(false);
            if (persistedSnapshot.Items.Count > 0)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation != _workspaceRefreshGeneration) return;
                    ApplyWorkspaceScanResult(
                        root,
                        persistedSnapshot.Items.ToList(),
                        persistedSnapshot.Options);
                    showedData = true;
                    if (!(_queueRunActive || _queueOrchestrator.AnyRunning))
                        QueueSummaryText = $"已加载 {persistedSnapshot.Items.Count} 个项目，正在校验工作目录 {root}";
                });
            }
        }

        // 队列正在跑时，内存或数据库里的列表已经够切换账号。再扫工作目录会和下载抢磁盘，
        // 扫完又在 UI 线程整表重建一次。等这轮队列结束后再校对文件。
        var queueBusy = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => _queueRunActive || _queueOrchestrator.AnyRunning);
        if (!force && showedData && queueBusy)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _workspaceRefreshGeneration || !IsActiveWorkspace(root))
                    return;
                UpdateQueueSummaryText();
            });
            return;
        }

        if (generation != Volatile.Read(ref _workspaceRefreshGeneration))
            return;

        var scanResult = await Task.Run(() =>
        {
            _queueStatePersist.Flush(root, TimeSpan.FromMilliseconds(400));
            return (
                Items: WorkspaceQueueService.ScanProjects(root).ToList(),
                Options: WorkspaceQueueService.LoadRunOptions(root));
        }).ConfigureAwait(false);

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _workspaceRefreshGeneration) return;
            // 同一工作目录运行中时不重扫（避免把 Running 状态回收为已停止）；
            // 但切账号切到「另一个正在运行的工作目录」必须应用扫描结果，否则队列/日志面板停留在旧账号。
            if (!force &&
                IsWorkspaceQueueRunning(root) &&
                string.Equals(_displayedWorkspaceRoot, SafeFullPath(root), StringComparison.OrdinalIgnoreCase) &&
                _queueItems.Count > 0)
                return;
            ApplyWorkspaceScanResult(root, scanResult.Items, scanResult.Options);
        });
    }

    private async Task ClearWorkspaceProjectsOnUiAsync(int generation)
    {
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _workspaceRefreshGeneration) return;
            ClearWorkspaceProjectCollections();
            _queueItems.Clear();
            _queueRowByDir.Clear();
            _displayedWorkspaceRoot = "";
            UpdateWorkspaceBindingSummary("");
            UpdateQueueSummaryText();
            RefreshLogSnapshot(force: true);
        });
    }

    private async Task RefreshWorkspaceProjectsAfterQueueRunAsync(
        string workspaceRoot,
        IReadOnlyList<QueueProjectItem>? terminalItems = null,
        QueueRunOptions? terminalOptions = null)
    {
        var root = (workspaceRoot ?? "").Trim();
        if (string.IsNullOrWhiteSpace(root))
            return;

        var shouldRefresh = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            // 先排空已经进入 200ms 合并区的 onPersist 快照；单工作区运行再用 worker
            // 持有的完整列表覆盖，确保追加项目也不会因字段列表滞后一拍而遗漏。
            FlushPendingPersistedQueueItems();
            if (terminalItems is not null)
            {
                var finalOptions = terminalOptions ?? ResolveQueueOptionsForPersistedWorkspace(root);
                var visibleTerminal = WorkspaceQueueService.WithoutDeletedProjects(root, terminalItems);
                CacheWorkspaceQueueSnapshot(root, visibleTerminal, finalOptions);
                _queueStatePersist.Enqueue(root, visibleTerminal, finalOptions);
            }

            if (!IsActiveWorkspace(root))
                return false;

            if (terminalItems is null)
            {
                var finalOptions = terminalOptions ?? ResolveQueueOptionsForPersistedWorkspace(root);
                CacheWorkspaceQueueSnapshot(root, _queueItems, finalOptions);
                _queueStatePersist.Enqueue(root, _queueItems, finalOptions);
            }
            return true;
        });
        if (!shouldRefresh)
        {
            // 非当前工作目录无需重建表格，但必须等终态真正落盘后再释放 run lifecycle，
            // 否则远程导入的 idle Save 可能被迟到的旧快照覆盖。
            if (terminalItems is not null)
            {
                await Task.Run(() => _queueStatePersist.Flush(root, TimeSpan.FromMilliseconds(400)))
                    .ConfigureAwait(true);
            }
            return;
        }

        Exception? refreshError = null;
        try
        {
            // 保留运行保护，并等待最终扫描完成后再发一次 Reset。
            await RefreshWorkspaceProjectsAsync(root).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            refreshError = ex;
        }

        // 无论扫描成功、被同工作区的更新代替，还是扫描失败，都以当前内存终态
        // 重建一次 ItemsSource；这是停止后修复失效 ListBox 虚拟化容器的关键通知。
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!IsActiveWorkspace(root))
                return;

            if (refreshError is not null && terminalItems is not null)
                _queueItems = WorkspaceQueueService.WithoutDeletedProjects(root, terminalItems);
            ReconcileQueueProjectRows(_queueItems);
            ApplyQueueProjectFilter();
            // 表格只绑定过滤集合；即使项目引用序列没有变化，也要发出一次 Reset，
            // 让 Avalonia 丢弃停止过程中失效的虚拟化容器与滚动偏移。
            FilteredQueueProjectRows.ReplaceAll(FilteredQueueProjectRows.ToArray());
            UpdateQueueSummaryText();
            RefreshLogSnapshot(force: true);
        });

        // 刷新失败不能把已经正常完成/停止的队列改报为执行失败。
        if (refreshError is not null)
        {
            var ex = refreshError;
            AppendLog($"队列结束后刷新表格失败，已保留当前数据：{ex.Message}");
        }
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path.Trim(); }
    }

    public bool IsAccountQueueBusy(string accountId)
    {
        var account = FindAccountById(accountId)?.Model;
        if (account is null)
            return false;
        var workspace = account.ResolveWorkspacePath();
        if (string.IsNullOrWhiteSpace(workspace))
            return false;
        try
        {
            return IsWorkspaceQueueBusy(workspace);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool IsWorkspaceQueueRunning(string workspaceRoot)
    {
        var normalized = NormalizeWorkspaceRootKey(workspaceRoot);
        return _queueOrchestrator.Snapshot().Any(item =>
            item.IsRunning &&
            string.Equals(NormalizeWorkspaceRootKey(item.WorkspaceRoot), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public void NoteLocalCheckedQueue(string workspace, bool active)
    {
        if (string.IsNullOrWhiteSpace(workspace))
            return;
        string root;
        try
        {
            root = Path.GetFullPath(workspace);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        lock (_localCheckedQueueLock)
        {
            if (active)
                _localCheckedQueueWorkspaces.Add(root);
            else
                _localCheckedQueueWorkspaces.Remove(root);
        }
    }

    public bool IsLocalCheckedQueueRunning(string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
            return false;
        string root;
        try
        {
            root = Path.GetFullPath(workspace);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        lock (_localCheckedQueueLock)
            return _localCheckedQueueWorkspaces.Contains(root);
    }

    public bool IsWorkspaceQueueBusy(string workspaceRoot) => _queueExecution.IsBusy(workspaceRoot);
    public bool IsWorkspaceQueueCommandActive(string workspaceRoot) => _queueExecution.IsActive(workspaceRoot);

    private void ClearWorkspaceProjectCollections()
    {
        QueueProjectRows.Clear();
        FilteredQueueProjectRows.Clear();
    }

    private void ShowWorkspaceLoadingState(string root)
    {
        ClearWorkspaceProjectCollections();
        _queueItems.Clear();
        _queueRowByDir.Clear();
        _displayedWorkspaceRoot = SafeFullPath(root);
        UpdateWorkspaceBindingSummary(root);
        QueueSummaryText = $"正在加载工作目录 {root}";
        RefreshTodayUploadCount();
        RefreshLogSnapshot(force: true);
    }

    private void ApplyWorkspaceScanResult(string root, List<QueueProjectItem> items, QueueRunOptions options)
    {
        var previousDisplayedRoot = _displayedWorkspaceRoot;
        var nextDisplayedRoot = SafeFullPath(root);
        var workspaceChanged = !string.Equals(
            previousDisplayedRoot,
            nextDisplayedRoot,
            StringComparison.OrdinalIgnoreCase);
        var replaceCollections = workspaceChanged || QueueProjectRows.Count == 0;

        var insideWorkspace = new List<QueueProjectItem>(items.Count);
        foreach (var item in items)
        {
            if (WorkspaceQueueService.IsProjectInsideWorkspace(root, item.ProjectDir))
                insideWorkspace.Add(item);
        }
        items = insideWorkspace;

        if (IsWorkspaceQueueRunning(root))
            items = PreserveDisplayedRuntimeState(items);

        var accountAligned = AlignVisibleQueueToSelectedAccount(items);
        var currentForceRerunCompletedSteps = ForceRerunCompletedSteps;
        _displayedWorkspaceRoot = nextDisplayedRoot;
        _queueItems = items;
        _queueRunOptions = options;
        // 传入后台线程已加载的 options，避免在 UI 线程重复读工作目录运行配置。
        ApplyAccountQueueEnabledSteps(root, options);
        _queueRunOptions.ForceRerunCompletedSteps = currentForceRerunCompletedSteps;
        ForceRerunCompletedSteps = currentForceRerunCompletedSteps;
        AutoArchiveAfterUpload = _queueRunOptions.AutoArchiveAfterUpload;
        PreferUploadWhenReady = _queueRunOptions.PreferUploadWhenReady;
        SyncManagementAfterUpload = _queueRunOptions.SyncManagementAfterUpload;
        ApplyQueueStepTogglesFromOptions();
        UpdateWorkspaceBindingSummary(root);

        ReconcileQueueProjectRows(_queueItems, replaceCollections: replaceCollections);

        ApplyQueueProjectFilter(replaceCollection: replaceCollections);
        UpdateQueueSummaryText();
        RefreshLogSnapshot(force: true);
        CacheWorkspaceQueueSnapshot(root, _queueItems, _queueRunOptions);
        if (accountAligned && !IsWorkspaceQueueRunning(root))
            _queueStatePersist.Enqueue(root, _queueItems, _queueRunOptions);
    }

    private bool AlignVisibleQueueToSelectedAccount(IReadOnlyList<QueueProjectItem> items)
    {
        var account = SelectedAccount?.Model;
        if (account is null)
            return false;

        var changed = false;
        foreach (var item in items)
        {
            if (QueueAccountBindingResolver.AlignToEffectiveAccount(item, account))
                changed = true;
        }

        return changed;
    }

    private void CacheWorkspaceQueueOptions(string workspaceRoot, QueueRunOptions options)
    {
        var root = NormalizeWorkspaceRootKey(workspaceRoot);
        if (string.IsNullOrWhiteSpace(root)) return;
        var persistedOptions = ClonePersistentQueueRunOptions(options);

        lock (_workspaceQueueSnapshotsLock)
        {
            if (_workspaceQueueSnapshots.TryGetValue(root, out var snapshot))
                snapshot.Options = persistedOptions;
            else
                _workspaceQueueSnapshots[root] = new WorkspaceQueueSnapshot { Options = persistedOptions };
        }
    }

    private void CacheWorkspaceQueueSnapshot(
        string workspaceRoot,
        IReadOnlyList<QueueProjectItem> items,
        QueueRunOptions? options = null)
    {
        var root = NormalizeWorkspaceRootKey(workspaceRoot);
        if (string.IsNullOrWhiteSpace(root)) return;

        lock (_workspaceQueueSnapshotsLock)
        {
            var existingOptions = _workspaceQueueSnapshots.TryGetValue(root, out var existing)
                ? existing.Options?.Clone()
                : null;
            _workspaceQueueSnapshots[root] = new WorkspaceQueueSnapshot
            {
                Items = CloneQueueItems(items),
                Options = options is null ? existingOptions : ClonePersistentQueueRunOptions(options),
            };
        }
    }

    private static QueueRunOptions ClonePersistentQueueRunOptions(QueueRunOptions options)
    {
        return options.ClonePersistent();
    }

    private bool TryGetWorkspaceQueueSnapshot(
        string workspaceRoot,
        out List<QueueProjectItem> items,
        out QueueRunOptions options)
    {
        items = new List<QueueProjectItem>();
        options = new QueueRunOptions();
        var root = NormalizeWorkspaceRootKey(workspaceRoot);
        if (string.IsNullOrWhiteSpace(root)) return false;

        lock (_workspaceQueueSnapshotsLock)
        {
            if (!_workspaceQueueSnapshots.TryGetValue(root, out var snapshot) || snapshot.Items.Count == 0)
                return false;

            items = CloneQueueItems(snapshot.Items);
            options = ClonePersistentQueueRunOptions(snapshot.Options ?? new QueueRunOptions());
            return true;
        }
    }

    private static string NormalizeWorkspaceRootKey(string workspaceRoot) =>
        string.IsNullOrWhiteSpace(workspaceRoot) ? "" : Path.TrimEndingDirectorySeparator(SafeFullPath(workspaceRoot.Trim()));

    private void SetWorkspaceQueueExecutionContext(
        string workspaceRoot,
        string batchId,
        TikTokAccountProfile? account,
        string uploadEntryMode)
    {
        var root = NormalizeWorkspaceRootKey(workspaceRoot);
        if (string.IsNullOrWhiteSpace(root)) return;
        lock (_queueExecutionContextsLock)
            _queueExecutionContexts[root] = new WorkspaceQueueExecutionContext(
                batchId,
                account,
                uploadEntryMode);
    }

    private WorkspaceQueueExecutionContext? GetWorkspaceQueueExecutionContext(string workspaceRoot)
    {
        var root = NormalizeWorkspaceRootKey(workspaceRoot);
        lock (_queueExecutionContextsLock)
            return _queueExecutionContexts.GetValueOrDefault(root);
    }

    private void RemoveWorkspaceQueueExecutionContext(string workspaceRoot, string batchId)
    {
        var root = NormalizeWorkspaceRootKey(workspaceRoot);
        lock (_queueExecutionContextsLock)
        {
            if (_queueExecutionContexts.TryGetValue(root, out var current) &&
                string.Equals(current.BatchId, batchId, StringComparison.Ordinal))
            {
                _queueExecutionContexts.Remove(root);
            }
        }
    }

    private static List<QueueProjectItem> CloneQueueItems(IReadOnlyList<QueueProjectItem> items) =>
        items.Select(CloneQueueItem).ToList();

    private static QueueProjectItem CloneQueueItem(QueueProjectItem item)
    {
        var clone = QueueProjectItem.FromPayload(item.ToPayload());
        clone.PrimaryVideoPath = item.PrimaryVideoPath;
        clone.CoverPath = item.CoverPath;
        return clone;
    }

    private static TikTokAccountProfile CloneAccountProfileForExport(TikTokAccountProfile account) => new()
    {
        Id = account.Id,
        Name = account.Name,
        TiktokAccountNickname = account.TiktokAccountNickname,
        TiktokLoginEmail = account.TiktokLoginEmail,
        TiktokLastLoginEmail = account.TiktokLastLoginEmail,
        TiktokUploadProfilePath = account.TiktokUploadProfilePath,
        LastWorkspace = account.LastWorkspace,
        TiktokExcelReportPath = account.TiktokExcelReportPath,
        TiktokProofCopyrightCompanyName = account.TiktokProofCopyrightCompanyName,
        TiktokProofDeclarantCompanyName = account.TiktokProofDeclarantCompanyName,
        TiktokTimestampApplicantName = account.TiktokTimestampApplicantName,
        TiktokEpisodeScriptEpisodeCount = account.TiktokEpisodeScriptEpisodeCount,
        TiktokAiScriptOutlineEpisodeCount = account.TiktokAiScriptOutlineEpisodeCount,
        TiktokRoleVectorCharacterCount = account.TiktokRoleVectorCharacterCount,
        TiktokRoleVectorMinimumCharacterCount = account.TiktokRoleVectorMinimumCharacterCount,
        TiktokUploadAiScriptOutlineWithScreenshots = account.TiktokUploadAiScriptOutlineWithScreenshots,
        TiktokUploadSourceInfoRoleVector = account.TiktokUploadSourceInfoRoleVector,
        TiktokUploadSourceInfoRoleSceneScreenshot = account.TiktokUploadSourceInfoRoleSceneScreenshot,
        TiktokProofSealPath = account.TiktokProofSealPath,
        TiktokProofTemplateDocxPath = account.TiktokProofTemplateDocxPath,
        TiktokProofAccountConfigMigrated = account.TiktokProofAccountConfigMigrated,
        TiktokAiRewriteSynopsis = account.TiktokAiRewriteSynopsis,
        TiktokLocalDramaEnglishPublishText = account.TiktokLocalDramaEnglishPublishText,
        TiktokRewriteFollowsSourceLanguage = TikTokAccountProfile.RewriteFollowsImportedLanguage(account.TiktokDramaMarket),
        TiktokAiNewTitleRandomness = account.TiktokAiNewTitleRandomness,
        TiktokAiSynopsisRandomness = account.TiktokAiSynopsisRandomness,
    };

    private void ReconcileQueueProjectRows(IReadOnlyList<QueueProjectItem> items, bool replaceCollections = false)
    {
        var nextRows = new List<QueueProjectRowViewModel>(items.Count);
        var nextByDir = new Dictionary<string, QueueProjectRowViewModel>(StringComparer.OrdinalIgnoreCase);
        var rowIndex = 1;

        foreach (var project in items)
        {
            var key = NormalizeProjectDir(project.ProjectDir);
            // 优先复用当前显示映射，其次复用跨工作目录的持久缓存；都命中不到才新建并订阅事件。
            if (!_queueRowByDir.TryGetValue(key, out var row) &&
                !_rowVmCache.TryGetValue(key, out row))
            {
                row = new QueueProjectRowViewModel(project);
                row.EnabledChangedByUser += OnQueueRowEnabledChangedByUser;
                row.RemarkChangedByUser += OnQueueRowRemarkChangedByUser;
            }
            else
            {
                row.RefreshFrom(project);
            }

            row.RowIndex = rowIndex++;
            nextRows.Add(row);
            nextByDir[key] = row;
            _rowVmCache[key] = row;
        }

        if (replaceCollections)
            QueueProjectRows.ReplaceAll(nextRows);
        else
            ReconcileObservableCollection(QueueProjectRows, nextRows);
        _queueRowByDir.Clear();
        foreach (var (key, row) in nextByDir)
            _queueRowByDir[key] = row;

        PruneRowVmCache(nextByDir);
    }

    /// <summary>持久行 VM 缓存超上限时，移除不在当前显示集合中的条目（其它工作目录的行需要时会重建）。</summary>
    private void PruneRowVmCache(Dictionary<string, QueueProjectRowViewModel> keep)
    {
        if (_rowVmCache.Count <= RowVmCacheMax) return;
        foreach (var key in _rowVmCache.Keys.ToList())
        {
            if (!keep.ContainsKey(key))
                _rowVmCache.Remove(key);
        }
    }

    private static void ReconcileObservableCollection<T>(
        ObservableCollection<T> collection,
        IReadOnlyList<T> target)
        where T : class
    {
        for (var index = 0; index < target.Count; index++)
        {
            var item = target[index];
            if (index < collection.Count && ReferenceEquals(collection[index], item))
                continue;

            var existingIndex = IndexOfReference(collection, item, index + 1);
            if (existingIndex >= 0)
                collection.Move(existingIndex, index);
            else
                collection.Insert(index, item);
        }

        while (collection.Count > target.Count)
            collection.RemoveAt(collection.Count - 1);
    }

    private static int IndexOfReference<T>(IList<T> items, T target, int startIndex)
        where T : class
    {
        for (var index = Math.Max(0, startIndex); index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], target))
                return index;
        }

        return -1;
    }

    private List<QueueProjectItem> PreserveDisplayedRuntimeState(List<QueueProjectItem> scannedItems)
    {
        if (_queueItems.Count == 0 || scannedItems.Count == 0)
            return scannedItems;

        var displayedByDir = _queueItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ProjectDir))
            .GroupBy(item => NormalizeProjectDir(item.ProjectDir), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var item in scannedItems)
        {
            if (!displayedByDir.TryGetValue(NormalizeProjectDir(item.ProjectDir), out var displayed) ||
                !HasRuntimeState(displayed))
            {
                continue;
            }

            item.Enabled = displayed.Enabled;
            item.CurrentStep = displayed.CurrentStep;
            item.StatusText = displayed.StatusText;
            item.LastError = displayed.LastError;
            item.Remark = displayed.Remark;
            item.ManualUploadStatus = displayed.ManualUploadStatus;
            item.StepStates = new Dictionary<string, string>(displayed.StepStates);
            item.UploadCompletedAt = displayed.UploadCompletedAt;
            item.AccountProfileId = displayed.AccountProfileId;
            item.AccountProfileName = displayed.AccountProfileName;
            item.QueueEntryDramaType = displayed.QueueEntryDramaType;
        }

        return scannedItems;
    }

    private static bool HasRuntimeState(QueueProjectItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.CurrentStep))
            return true;

        return item.StepStates.Values.Any(status =>
            string.Equals(status, QueueStepStatus.Running, StringComparison.Ordinal) ||
            string.Equals(status, QueueStepStatus.WaitingUploadSlot, StringComparison.Ordinal) ||
            string.Equals(status, QueueStepStatus.ManualIntervention, StringComparison.Ordinal));
    }

    partial void OnShowOnlyPendingUploadChanged(bool value)
    {
        ApplyQueueProjectFilter();
    }

    private void ApplyQueueProjectFilter(bool replaceCollection = false)
    {
        NotifyQueueStatisticsChanged();
        var query = (QueueSearchText ?? "").Trim();
        IEnumerable<QueueProjectRowViewModel> rows = QueueProjectRows;
        if (ShowOnlyPendingUpload)
            rows = rows.Where(p => p.IsPendingUpload);
        if (!string.IsNullOrEmpty(query))
        {
            rows = rows.Where(p =>
                p.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.OriginalTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.NewTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.AccountName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.Remark.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.StatusText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.LastError.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var target = rows.ToList();

        // 结果集未变化时不动 ObservableCollection（避免队列运行期间每次刷新都整表重建行容器）。
        if (target.Count == FilteredQueueProjectRows.Count)
        {
            var identical = true;
            for (var i = 0; i < target.Count; i++)
            {
                if (!ReferenceEquals(target[i], FilteredQueueProjectRows[i]))
                {
                    identical = false;
                    break;
                }
            }

            if (identical)
            {
                for (var i = 0; i < target.Count; i++)
                    target[i].RowIndex = i + 1;
                return;
            }
        }

        var index = 1;
        foreach (var vm in target)
            vm.RowIndex = index++;
        if (replaceCollection || IsCurrentWorkspaceQueueRunning())
            FilteredQueueProjectRows.ReplaceAll(target);
        else
            ReconcileObservableCollection(FilteredQueueProjectRows, target);
    }

    private void NotifyQueueStatisticsChanged()
    {
        OnPropertyChanged(nameof(QueueTotalCount));
        OnPropertyChanged(nameof(QueuePendingCount));
        OnPropertyChanged(nameof(QueueRunningCount));
        OnPropertyChanged(nameof(QueueCompletedCount));
        OnPropertyChanged(nameof(QueueFailedCount));
        OnPropertyChanged(nameof(QueueStoppedCount));
    }

    public IReadOnlyList<QueueProjectItem> GetPendingUploadProjects() =>
        WorkspaceQueueService.FilterPendingUpload(_queueItems).ToList();

    public void SetFilteredQueueRowsEnabled(bool enabled)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root)) return;

        var visibleRows = FilteredQueueProjectRows.ToArray();
        if (visibleRows.Length == 0)
        {
            StatusMessage = enabled ? "没有可勾选的项目" : "没有可取消的项目";
            return;
        }

        var visibleDirs = visibleRows
            .Select(row => Path.GetFullPath(row.Item.ProjectDir))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = 0;
        foreach (var item in _queueItems)
        {
            if (!visibleDirs.Contains(Path.GetFullPath(item.ProjectDir))) continue;
            if (item.Enabled == enabled) continue;

            item.Enabled = enabled;
            changed++;
        }

        if (changed > 0)
        {
            PersistQueueItems();
        }
        else
        {
            RefreshQueueRowViewModels();
        }

        UpdateQueueSummaryText(refreshTodayUploadCount: false);
        StatusMessage = enabled
            ? $"已勾选 {visibleRows.Length} 个项目"
            : $"已取消勾选 {visibleRows.Length} 个项目";
    }

    public int SetQueueRowsEnabled(IEnumerable<QueueProjectRowViewModel> rows, bool enabled)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root)) return 0;

        var dirs = rows
            .Select(row => row.Item.ProjectDir)
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Select(NormalizeProjectDir)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (dirs.Count == 0) return 0;

        var matched = 0;
        var changed = 0;
        foreach (var item in _queueItems)
        {
            if (!dirs.Contains(NormalizeProjectDir(item.ProjectDir))) continue;
            matched++;
            if (item.Enabled == enabled) continue;

            item.Enabled = enabled;
            changed++;
        }

        if (changed > 0)
            PersistQueueItems();
        else
            RefreshQueueRowViewModels();

        UpdateQueueSummaryText(refreshTodayUploadCount: false);
        return matched;
    }

    public bool BindAccountToProjects(AccountItemViewModel account, IEnumerable<QueueProjectItem> projects)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root)) return false;

        var dirs = projects
            .Select(p => Path.GetFullPath(p.ProjectDir))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bound = 0;
        foreach (var item in _queueItems)
        {
            if (!dirs.Contains(Path.GetFullPath(item.ProjectDir))) continue;
            item.AccountProfileId = account.Id;
            item.AccountProfileName = account.DisplayName;
            item.Enabled = true;
            bound++;
        }

        if (bound == 0) return false;
        PersistQueueItems();
        RefreshQueueRowViewModels();
        return true;
    }

    public void MarkProjectUploadCompleted(string projectDir)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root)) return;

        WorkspaceQueueService.MarkUploadSeriesCompleted(
            root,
            projectDir,
            SelectedAccount?.Id,
            SelectedAccount?.DisplayName);
        RefreshWorkspaceProjects(root);
    }

    public void SetQueueProjectUploadStatus(QueueProjectRowViewModel row, bool completed) =>
        SetQueueProjectsUploadStatus(new[] { row }, completed);

    public int SetQueueProjectsUploadStatus(IEnumerable<QueueProjectRowViewModel> rows, bool completed)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("请先在「账号管理 → 发布配置」设置上传工作目录。");
        if (IsCurrentWorkspaceQueueRunning())
            throw new InvalidOperationException("当前工作目录队列正在运行，请停止后再修改上传状态。");

        var targetDirs = rows
            .Where(row => row is not null && !string.IsNullOrWhiteSpace(row.Item.ProjectDir))
            .Select(row => NormalizeProjectDir(row.Item.ProjectDir))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targetDirs.Count == 0)
            throw new InvalidOperationException("请先选择要修改的项目。");

        var changed = 0;
        foreach (var item in _queueItems)
        {
            if (!targetDirs.Contains(NormalizeProjectDir(item.ProjectDir)))
                continue;

            item.ManualUploadStatus = completed ? QueueStepStatus.Completed : QueueStepStatus.Failed;
            if (!completed)
                item.LastError = "手动标记上传失败";
            else if (SelectedAccount is not null)
            {
                if (string.IsNullOrWhiteSpace(item.AccountProfileId))
                    item.AccountProfileId = SelectedAccount.Id;
                if (string.IsNullOrWhiteSpace(item.AccountProfileName))
                    item.AccountProfileName = SelectedAccount.DisplayName;
            }

            WorkspaceQueueService.ApplyManualUploadStatus(item);
            item.NormalizeStepStates();
            changed++;
        }

        if (changed == 0)
            throw new InvalidOperationException("未找到要修改的队列项目。");

        PersistQueueItems();
        UpdateQueueSummaryText();
        AutoExportQueueExcel();

        var status = completed ? QueueStepStatus.Completed : QueueStepStatus.Failed;
        StatusMessage = changed == 1
            ? $"已将 1 个项目上传状态标记为{status}"
            : $"已将 {changed} 个项目上传状态标记为{status}";
        AppendLog(StatusMessage);
        return changed;
    }

    public async Task<QueueWorkerSummary?> RunQueueWorkerAsync(
        IQueuePublishHost host,
        Action<QueueWorkerProgress> onProgress,
        Action<string, IReadOnlyList<QueueProjectItem>> onPersist,
        CancellationToken ct,
        QueueRunOptions? optionsOverride = null,
        IReadOnlyCollection<string>? projectDirFilter = null,
        Action? onStarted = null,
        WorkspaceQueueTarget? targetOverride = null,
        DateTime? preserveProjectLogsSince = null)
    {
        var target = targetOverride ?? CaptureCurrentWorkspaceQueueTarget();
        if (target is null || string.IsNullOrWhiteSpace(target.WorkspaceRoot))
            return null;

        target = target with { ProjectDirFilter = target.ProjectDirFilter ?? projectDirFilter };
        return await _queueExecution.InSessionAsync([target], _store,
            (executions, token) => RunQueueWorkerCoreAsync(
                host, onProgress, onPersist, token, optionsOverride,
                onStarted, executions[0], preserveProjectLogsSince), ct).ConfigureAwait(true);
    }
    private async Task<QueueWorkerSummary?> RunQueueWorkerCoreAsync(
        IQueuePublishHost host,
        Action<QueueWorkerProgress> onProgress,
        Action<string, IReadOnlyList<QueueProjectItem>> onPersist,
        CancellationToken ct,
        QueueRunOptions? optionsOverride,
        Action? onStarted,
        QueueExecutionTarget execution,
        DateTime? preserveProjectLogsSince)
    {
        var target = execution.Target;
        var root = target.WorkspaceRoot;
        var isDisplayedWorkspace = IsActiveWorkspace(root) &&
            string.Equals(
                NormalizeWorkspaceRootKey(_displayedWorkspaceRoot),
                root,
                StringComparison.OrdinalIgnoreCase);
        // 后台准备阶段只能读取列表副本，界面集合仍可能被 UI 线程增删。
        var displayedSnapshot = isDisplayedWorkspace && _queueItems.Count > 0
            ? _queueItems.ToList()
            : null;

        var effectiveAccount = execution.Account;
        _queueLogAccount.Value = effectiveAccount.Id;

        var runOptions = optionsOverride?.Clone() ?? (isDisplayedWorkspace
            ? BuildQueueRunOptionsFromUi()
            : LoadQueueRunOptionsForAccountWorkspace(root, effectiveAccount));

        var preparation = await _queueExecution.PrepareAsync(execution, runOptions, _store, displayedSnapshot, ct)
            .ConfigureAwait(true);
        if (preparation is null)
            return null;
        ct.ThrowIfCancellationRequested();
        runOptions = preparation.Options;

        var queueItemsForRun = preparation.Items;
        var runItems = preparation.SelectedItems;
        var repairedAccountBindingCount = preparation.RepairedItems.Count;
        if (repairedAccountBindingCount > 0)
        {
            if (isDisplayedWorkspace)
            {
                foreach (var item in preparation.RepairedItems)
                    RefreshQueueRowFor(item);
            }

            AppendLog($"检测到账号删除、重建或重命名，已校正 {repairedAccountBindingCount} 个队列项目的账号绑定。");
        }

        if (optionsOverride is null && isDisplayedWorkspace)
            _queueStatePersist.Enqueue(root, queueItemsForRun, _queueRunOptions);
        else if (repairedAccountBindingCount > 0)
            _queueStatePersist.Enqueue(root, queueItemsForRun, runOptions);
        if (repairedAccountBindingCount > 0)
        {
            await Task.Run(() => _queueStatePersist.Flush(root, TimeSpan.FromSeconds(1)))
                .ConfigureAwait(true);
        }
        CacheWorkspaceQueueSnapshot(root, queueItemsForRun, runOptions);
        MarkQueueExcelExportPending(root);

        preparation.ValidationError?.Throw();
        var selectedRunCount = preparation.SelectedRunCount;

        Logs.ClearProjectEntries(runItems, preserveProjectLogsSince, effectiveAccount.Id);
        RefreshRunningWorkspacesSummary();
        var batchId = TikTokExecutionHistoryService.NewBatchId();
        var totalCount = selectedRunCount;
        var enabledSteps = runOptions.OrderedEnabledSteps();
        var projectConcurrency = runOptions.ProjectConcurrency;
        var uploadEntryMode = runOptions.UploadEntryMode;
        var account = effectiveAccount;
        var finalAction = target.FinalActionOverride ?? SelectedFinalAction?.Value ?? FinalAction.None;
        SetWorkspaceQueueExecutionContext(root, batchId, account, uploadEntryMode);
        _ = Task.Run(() => TikTokExecutionHistoryService.AppendEvent(
            "run_started",
            "running",
            root,
            batchId: batchId,
            message: "队列开始执行",
            metadata: new Dictionary<string, object?>
            {
                ["total_count"] = totalCount,
                ["enabled_steps"] = enabledSteps,
                ["project_concurrency"] = projectConcurrency,
                ["upload_entry_mode"] = uploadEntryMode,
            },
            account: account));
        try
        {
            var summary = await _queueExecution.StartPreparedAsync(
                preparation,
                _store,
                host,
                finalAction,
                onProgress,
                (workspaceRoot, items) =>
                {
                    if (string.Equals(Path.GetFullPath(workspaceRoot), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                        onPersist(workspaceRoot, items);
                },
                ct,
                onStarted);
            TikTokExecutionHistoryService.AppendEvent(
                "run_finished",
                summary?.Stopped == true ? "stopped" : "completed",
                root,
                batchId: batchId,
                message: "队列执行结束",
                metadata: new Dictionary<string, object?>
                {
                    ["total_count"] = summary?.TotalCount ?? 0,
                    ["success_count"] = summary?.SuccessCount ?? 0,
                    ["failed_count"] = summary?.FailedCount ?? 0,
                    ["stopped"] = summary?.Stopped ?? false,
                },
                account: account);
            return summary;
        }
        finally
        {
            try
            {
                try
                {
                    RefreshRunningWorkspacesSummary();
                    // 队列结束（尤其是手动停止）时必须发送一次 Reset 通知。
                    // 高频运行刷新采用逐项协调；Avalonia ListBox 偶尔会保留失效的虚拟化容器，
                    // 表现为汇总仍有数据但表格空白，直到切换账号触发整表重建。
                    await RefreshWorkspaceProjectsAfterQueueRunAsync(root, queueItemsForRun, runOptions)
                        .ConfigureAwait(true);
                    RefreshLogSnapshot();
                }
                finally
                {
                    // Flush(root, 400ms) 只是限时尝试，不能作为“旧终态已落盘”的严格屏障。
                    // 持续等待该工作目录无 pending/active 写入后，远程导入才能进入 idle Save。
                    await WaitForWorkspaceQueueStatePersistAsync(root).ConfigureAwait(true);
                }
            }
            finally
            {
                // orchestrator 会先把 IsRunning 置为 false；必须等最终快照刷新/落盘完成后，
                // 才允许远程导入按 idle 路径 Scan + Save。
                RemoveWorkspaceQueueExecutionContext(root, batchId);
            }
        }
    }

    public Task<IReadOnlyList<QueueWorkerSummary?>> RunAllAccountWorkspaceQueuesAsync(
        IQueuePublishHost host,
        Action<QueueWorkerProgress> onProgress,
        Action<string, IReadOnlyList<QueueProjectItem>> onPersist,
        CancellationToken ct,
        IReadOnlyList<WorkspaceQueueTarget>? targetsOverride = null,
        QueueRunOptions? optionsOverride = null)
    {
        var targets = targetsOverride ?? BuildAccountWorkspaceTargets();
        if (targets.Count == 0)
            return Task.FromResult<IReadOnlyList<QueueWorkerSummary?>>([]);

        return _queueExecution.InSessionAsync(targets, _store, (executions, token) =>
            RunAccountWorkspaceBatchAsync(executions, host, onProgress, onPersist, optionsOverride,
                targetsOverride is null, token), ct);
    }

    private async Task<IReadOnlyList<QueueWorkerSummary?>> RunAccountWorkspaceBatchAsync(
        IReadOnlyList<QueueExecutionTarget> executions,
        IQueuePublishHost host,
        Action<QueueWorkerProgress> onProgress,
        Action<string, IReadOnlyList<QueueProjectItem>> onPersist,
        QueueRunOptions? optionsOverride,
        bool saveVisibleOptions,
        CancellationToken ct)
    {
        if (!_queueOrchestrator.AnyRunning)
            Logs.ClearAllEntries();
        if (saveVisibleOptions)
        {
            SyncEnabledStepsFromUi();
            if (CanWriteDisplayedQueue(WorkspacePath))
                _queueStatePersist.Enqueue(WorkspacePath, _queueItems, _queueRunOptions);
            PersistAccountQueueSettings();
        }

        var preparedRuns = new List<PreparedQueueExecution>();
        var stoppedBeforeStart = 0;
        foreach (var execution in executions)
        {
            var root = execution.Target.WorkspaceRoot;
            var account = execution.Account;
            QueueRunOptions options;
            if (optionsOverride is not null)
            {
                options = optionsOverride.Clone();
                options.ProjectConcurrency = Math.Clamp(account.TiktokProjectConcurrency, 1, 20);
            }
            else if (account.Id == SelectedAccount?.Id && IsActiveWorkspace(root))
            {
                options = BuildQueueRunOptionsFromUi();
            }
            else
            {
                options = LoadQueueRunOptionsForAccountWorkspace(root, account);
            }

            PreparedQueueExecution? prepared;
            try
            {
                prepared = await _queueExecution.PrepareAsync(execution, options, _store, null, ct,
                    skipIdleWorkspace: true).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                stoppedBeforeStart++;
                continue;
            }
            if (prepared is null)
                continue;
            if (prepared.RepairedItems.Count > 0)
                _queueStatePersist.Enqueue(root, prepared.Items, options);
            prepared.ValidationError?.Throw();
            CacheWorkspaceQueueOptions(root, options);
            preparedRuns.Add(prepared);
        }

        if (preparedRuns.Count == 0 && stoppedBeforeStart == 0)
            throw new InvalidOperationException("没有可执行的工作目录队列，请勾选项目并核对执行步骤。");

        var batchId = TikTokExecutionHistoryService.NewBatchId();
        var fallbackFinalAction = SelectedFinalAction?.Value ?? FinalAction.None;
        var summaries = await Task.WhenAll(preparedRuns.Select(RunPreparedAsync)).ConfigureAwait(true);
        return summaries.Concat(Enumerable.Range(0, stoppedBeforeStart).Select(_ => new QueueWorkerSummary { Stopped = true })).ToArray();

        async Task<QueueWorkerSummary?> RunPreparedAsync(PreparedQueueExecution prepared)
        {
            var execution = prepared.Execution;
            var root = execution.Target.WorkspaceRoot;
            MarkQueueExcelExportPending(root);
            SetWorkspaceQueueExecutionContext(root, batchId, execution.Account, prepared.Options.UploadEntryMode);
            TikTokExecutionHistoryService.AppendEvent(
                "run_started", "running", root, batchId: batchId, message: "多工作目录队列开始执行",
                metadata: new Dictionary<string, object?> { ["display_label"] = execution.Target.DisplayLabel },
                account: execution.Account);
            try
            {
                return await _queueExecution.StartPreparedAsync(prepared, _store, host, fallbackFinalAction,
                    onProgress, onPersist, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return new QueueWorkerSummary { Stopped = true };
            }
            finally
            {
                try
                {
                    RefreshRunningWorkspacesSummary();
                    await RefreshWorkspaceProjectsAfterQueueRunAsync(root, prepared.Items, prepared.Options)
                        .ConfigureAwait(true);
                }
                finally
                {
                    try { await WaitForWorkspaceQueueStatePersistAsync(root).ConfigureAwait(true); }
                    finally { RemoveWorkspaceQueueExecutionContext(root, batchId); }
                }
            }
        }
    }
    public IReadOnlyList<WorkspaceQueueTarget> BuildAccountWorkspaceTargets()
        => BuildAccountWorkspaceTargets(_store.Accounts, skipMissingWorkspace: true, out _);

    public WorkspaceQueueTarget? CaptureCurrentWorkspaceQueueTarget()
    {
        var workspace = WorkspacePath.Trim();
        if (string.IsNullOrWhiteSpace(workspace))
            return null;

        var account = SelectedAccount;
        return new WorkspaceQueueTarget(
            workspace,
            $"{account?.DisplayName ?? "当前账号"} · {workspace}",
            account?.Id,
            FinalActionOverride: SelectedFinalAction?.Value ?? FinalAction.None);
    }

    internal ManualUploadTitleImportTarget CaptureManualUploadTitleImportTarget()
    {
        var target = CaptureCurrentWorkspaceQueueTarget()
            ?? throw new InvalidOperationException("请先在「账号管理 → 发布配置」设置上传工作目录。");
        return CaptureManualUploadTitleImportTarget(target);
    }

    private ManualUploadTitleImportTarget CaptureManualUploadTitleImportTarget(WorkspaceQueueTarget target)
    {
        var account = FindAccountById(target.AccountProfileId)?.Model
            ?? throw new InvalidOperationException("找不到上传短剧所选账号，请重新选择账号后再试。");
        var settings = ClientSettingsStore.Load();
        var route = TikTokUploadTitleRoute.Capture(settings, account);
        return new ManualUploadTitleImportTarget(target, route, route.ApplyTo(settings), account.DisplayName);
    }

    private IReadOnlyList<WorkspaceQueueTarget> BuildAccountWorkspaceTargets(
        IEnumerable<TikTokAccountProfile> accounts,
        bool skipMissingWorkspace,
        out IReadOnlyList<string> missingWorkspaceAccounts)
    {
        var targets = new Dictionary<string, WorkspaceQueueTarget>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var account in accounts)
        {
            var workspace = account.ResolveWorkspacePath();
            if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
            {
                if (!skipMissingWorkspace)
                    missing.Add(account.DisplayName);
                continue;
            }

            var normalized = Path.GetFullPath(workspace);
            if (targets.ContainsKey(normalized))
                continue;
            targets[normalized] = new WorkspaceQueueTarget(
                normalized,
                $"{account.DisplayName} · {normalized}",
                account.Id,
                FinalActionOverride: SelectedFinalAction?.Value ?? FinalAction.None);
        }

        missingWorkspaceAccounts = missing;
        return targets.Values.OrderBy(target => target.DisplayLabel, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private QueueRunOptions BuildQueueRunOptionsFromUi()
    {
        SyncEnabledStepsFromUi();
        var concurrency = SelectedAccount?.Model.TiktokProjectConcurrency ?? _queueRunOptions.ProjectConcurrency;
        _queueRunOptions.ProjectConcurrency = Math.Clamp(concurrency < 1 ? 4 : concurrency, 1, 20);
        _queueRunOptions.UploadEntryMode = "";
        return _queueRunOptions.Clone();
    }

    public QueueRunOptions CreateCurrentQueueRunOptionsSnapshot() => BuildQueueRunOptionsFromUi();

    public bool IsCurrentWorkspaceQueueRunning()
    {
        var root = WorkspacePath.Trim();
        return !string.IsNullOrEmpty(root) && IsWorkspaceQueueRunning(root);
    }

    public bool RequestStopQueue(string? workspaceRoot = null)
    {
        var root = (workspaceRoot ?? WorkspacePath).Trim();
        var stopped = _queueExecution.TryStopWorkspace(root, SelectedAccount?.Id,
            () => ReleaseUserStoppedAutoPublish(root));
        RefreshRunningWorkspacesSummary();
        return stopped;
    }
    public async Task<TikTokRemoteCommandResult> ExecuteRemoteCommandAsync(TikTokRemoteCommand command)
    {
        try
        {
            return command.Command switch
            {
                TikTokRemoteCommandNames.ShowHelpText =>
                    TikTokRemoteCommandResult.Success(command.Command, ClientSettingsDefaults.RemoteCommandHelpText),
                TikTokRemoteCommandNames.ShowHelpCard =>
                    TikTokRemoteCommandResult.SuccessCard(
                        command.Command,
                        "TikTok 远程上传命令卡片教程",
                        TikTokRemoteHelpCardBuilder.BuildCommandTutorialCardJson()),
                TikTokRemoteCommandNames.QueryStatus =>
                    TikTokRemoteCommandResult.Success(command.Command, BuildRemoteRuntimeStatusText()),
                TikTokRemoteCommandNames.StopQueue => ExecuteRemoteStopQueue(command),
                TikTokRemoteCommandNames.SwitchAccountProfile => ExecuteRemoteSwitchAccount(command),
                TikTokRemoteCommandNames.StartQueue => await ExecuteRemoteStartQueueAsync(command),
                TikTokRemoteCommandNames.UploadSeries => await ExecuteRemoteUploadSeriesAsync(command),
                TikTokRemoteCommandNames.RetryQueue => await ExecuteRemoteRetryQueueAsync(command),
                TikTokRemoteCommandNames.RemoveQueueTask => ExecuteRemoteRemoveQueueTask(command),
                _ => TikTokRemoteCommandResult.Failed(command.Command, $"未支持的远程命令：{command.Command}"),
            };
        }
        catch (Exception ex)
        {
            AppendLog($"远程命令执行失败：{ex.Message}");
            return TikTokRemoteCommandResult.Failed(command.Command, ex.Message);
        }
    }

    private TikTokRemoteCommandResult ExecuteRemoteStopQueue(TikTokRemoteCommand command)
    {
        if (!_queueExecution.TryStopAll(() => ReleaseUserStoppedAutoPublish(null)))
            return TikTokRemoteCommandResult.Success(command.Command, "当前没有运行中的 TikTok 队列。");
        RefreshRunningWorkspacesSummary();
        StatusMessage = "已接收远程停止队列命令，正在等待所有运行中的队列安全结束。";
        AppendLog(StatusMessage);
        return TikTokRemoteCommandResult.Accepted(command.Command, StatusMessage);
    }

    private TikTokRemoteCommandResult ExecuteRemoteSwitchAccount(TikTokRemoteCommand command)
    {
        if (command.HasMultiAccountSelection)
            return TikTokRemoteCommandResult.Failed(command.Command, "切换账号命令一次只能指定一个 TikTok 账号。");
        if (!TryApplyRemoteAccountSelection(command, "", out var error))
            return TikTokRemoteCommandResult.Failed(command.Command, error);

        var text = $"已切换账号「{SelectedAccount?.DisplayName ?? ""}」";
        return TikTokRemoteCommandResult.Success(command.Command, text);
    }

    private async Task<TikTokRemoteCommandResult> ExecuteRemoteStartQueueAsync(TikTokRemoteCommand command)
    {
        if (IsQueueRunning)
            return TikTokRemoteCommandResult.Failed(command.Command, XingeAutoPublishFailureClassifier.TikTokQueueBusyDetail);
        if (command.HasMultiAccountSelection)
            return await ExecuteRemoteStartMultiAccountQueueAsync(command);

        var hasExplicitAccount = command.HasExplicitAccountSelection;
        if (hasExplicitAccount && !TryApplyRemoteAccountSelection(command, "", out var accountError))
            return TikTokRemoteCommandResult.Failed(command.Command, accountError);
        if (!TryResolveRemoteWorkspace(command, out var workspace, out var error))
            return TikTokRemoteCommandResult.Failed(command.Command, error);
        if (!hasExplicitAccount && !TryApplyRemoteAccountSelection(command, workspace, out error))
            return TikTokRemoteCommandResult.Failed(command.Command, error);
        ActivateRemoteWorkspace(workspace);

        var options = BuildRemoteEnabledStepOptions(command);
        if (options is not null)
            options.ProjectConcurrency = Math.Clamp(SelectedAccount?.Model.TiktokProjectConcurrency ?? options.ProjectConcurrency, 1, 20);

        var runQueueRequested = RemoteQueueRunRequested;
        if (runQueueRequested is null)
            return TikTokRemoteCommandResult.Failed(command.Command, "队列视图尚未初始化，无法执行远程队列。");

        var target = CaptureCurrentWorkspaceQueueTarget();
        if (target is null)
            return TikTokRemoteCommandResult.Failed(command.Command, "无法捕获远程队列的目标工作目录。");

        var started = await runQueueRequested.Invoke(target, options);
        if (!started)
            return TikTokRemoteCommandResult.Failed(command.Command, $"TikTok 队列未启动，工作目录可能已被其它任务占用：{workspace}");

        return TikTokRemoteCommandResult.Accepted(command.Command, $"TikTok 队列已启动，工作目录：{workspace}");
    }

    private TikTokRemoteCommandResult ExecuteRemoteRemoveQueueTask(TikTokRemoteCommand command)
    {
        var project = command.RetryItems?.SingleOrDefault();
        var account = FindAccountById(command.AccountProfileId)?.Model;
        var workspace = account?.ResolveWorkspacePath();
        if (project is null || account is null || string.IsNullOrWhiteSpace(workspace))
            return TikTokRemoteCommandResult.Failed(command.Command, "找不到要移出的项目或账号工作目录。");
        if (IsLocalCheckedQueueRunning(workspace))
            return DeferRemoteCommandWhileLocalCheckedQueueRuns(command, workspace);
        using var reservation = _queueExecution.TryReserveWorkspaceForDeletion(workspace);
        if (reservation is null)
            return TikTokRemoteCommandResult.Failed(command.Command, "队列正在运行，请先停止任务。");
        try
        {
            QueueRetryUseCase.RemoveReleasedProject(workspace, account.Id, project);
            RefreshWorkspaceProjects(workspace);
            return TikTokRemoteCommandResult.Success(command.Command, "已移出客户端队列，保留本地文件。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return TikTokRemoteCommandResult.Failed(command.Command, ex.Message);
        }
    }

    private async Task<TikTokRemoteCommandResult> ExecuteRemoteRetryQueueAsync(TikTokRemoteCommand command)
    {
        var runQueueRequested = RemoteQueueRunRequested;
        if (runQueueRequested is null)
            return TikTokRemoteCommandResult.Failed(command.Command, "队列视图尚未初始化，无法执行重试。");

        var projects = (command.RetryItems ?? [])
            .Select(item => new QueueRetryProject(item.ProjectPath, item.AutoPublishAssignmentId, item.ClientDownloadMigration)).ToArray();
        var result = await _queueRetry.ExecuteAsync(command.AccountProfileId, _store, projects,
            IsLocalCheckedQueueRunning,
            target => runQueueRequested(target, null),
            (workspace, prepared) =>
            {
                if (IsActiveWorkspace(workspace))
                    ApplyWorkspaceScanResult(workspace, prepared.AllItems.ToList(), prepared.RunOptions);
            },
            AppendLog);
        if (result.Deferred)
            return DeferRemoteCommandWhileLocalCheckedQueueRuns(command, result.WorkspaceRoot);
        if (!result.Accepted)
            return TikTokRemoteCommandResult.Failed(command.Command, result.Message);

        StatusMessage = result.Message;
        AppendLog(result.Message);
        return TikTokRemoteCommandResult.Accepted(command.Command, result.Message);
    }
    private static string FirstNonEmptyText(params string?[] values) =>
        values.Select(value => value?.Trim() ?? "").FirstOrDefault(value => value.Length > 0) ?? "";

    // 依据远程命令生成导入用的原始文本与匹配模式：
    // title_episode 模式下按 series 逐行输出「剧名 集数」，供精准按集数匹配；否则退回仅剧名。
    private static (string RawText, string MatchMode) BuildRemoteUploadImportInput(
        TikTokRemoteCommand command,
        IReadOnlyList<string> titles)
    {
        if (command.UsesEpisodeMatching && command.Series is { Count: > 0 })
        {
            var lines = command.Series
                .Where(spec => !string.IsNullOrWhiteSpace(spec.Title))
                .Select(spec => spec.EpisodeCnt > 0 ? $"{spec.Title} {spec.EpisodeCnt}" : spec.Title);
            return (string.Join(Environment.NewLine, lines), UploadTitleImportService.MatchModeTitleEpisode);
        }

        return (string.Join(Environment.NewLine, titles), UploadTitleImportService.MatchModeTitle);
    }

    private async Task<TikTokRemoteCommandResult> ExecuteRemoteUploadSeriesAsync(TikTokRemoteCommand command)
    {
        var assignment = XingeAutoPublishAssignment.FromCommand(command);
        if (assignment is null)
            return await ExecuteRemoteUploadSeriesCoreAsync(command, null);

        var autoPublish = new RemoteAutoPublishContext(assignment);
        TikTokRemoteCommandResult result;
        try
        {
            result = await ExecuteRemoteUploadSeriesCoreAsync(command, autoPublish);
        }
        catch (Exception ex)
        {
            result = TikTokRemoteCommandResult.Failed(command.Command, ex.Message);
        }

        // 在命令回执之前同步回传，确保服务端按客户端给出的失败类型处理，而不是按回执文本猜测。
        if (!autoPublish.TerminalReported && string.Equals(result.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            await ReportRemoteAutoPublishFailureAsync(
                autoPublish,
                "import",
                result.SummaryText,
                XingeAutoPublishFailureClassifier.Classify("", result.SummaryText));
        }

        return result;
    }

    private static Task<TikTokRemoteCommandResult> AcceptAutoPublishQueueDeferralAsync(
        TikTokRemoteCommand command,
        RemoteAutoPublishContext? autoPublish,
        string detail)
    {
        if (autoPublish is null)
            return Task.FromResult(TikTokRemoteCommandResult.Failed(command.Command, detail));
        XingeAutoPublishProgressReporter.Shared.Enqueue(XingeAutoPublishProgress.For(
            autoPublish.Assignment,
            XingeAutoPublishStatuses.Accepted,
            "import",
            detail));
        return Task.FromResult(TikTokRemoteCommandResult.Accepted(command.Command, detail));
    }

    private async Task ReportRemoteAutoPublishFailureAsync(
        RemoteAutoPublishContext autoPublish,
        string step,
        string detail,
        string failureKind)
    {
        autoPublish.TerminalReported = true;
        foreach (var projectDir in autoPublish.ProjectDirs)
        {
            try { XingeAutoPublishMarkerStore.MarkFinal(projectDir, autoPublish.Assignment.AssignmentId, XingeAutoPublishStatuses.Failed); }
            catch (Exception ex) { AppendLog($"更新自动上新标记失败：{ex.Message}"); }
        }

        var delivered = await XingeAutoPublishProgressReporter.Shared.ReportAsync(
            XingeAutoPublishProgress.For(autoPublish.Assignment, XingeAutoPublishStatuses.Failed, step, detail, failureKind),
            CancellationToken.None);
        if (!delivered)
            AppendLog($"自动上新派单 {autoPublish.Assignment.AssignmentId} 的失败结果暂未送达 XINGE，将在连接恢复后补发。");
    }

    private sealed class RemoteAutoPublishContext(XingeAutoPublishAssignment assignment)
    {
        public XingeAutoPublishAssignment Assignment { get; } = assignment;
        public List<string> ProjectDirs { get; } = [];
        public bool TerminalReported { get; set; }
    }

    private sealed class PendingCheckedQueue
    {
        public string AccountId { get; set; } = "";
        public List<string> ProjectDirs { get; } = [];
        public int Generation { get; set; }
        public int StopEpoch { get; set; }
        public QueueRunOptions? ServerOptions { get; set; }
    }

    private readonly HashSet<string> _checkedQueueFollowUpRunning = new(StringComparer.OrdinalIgnoreCase);

    // 手动停止：停掉本机队列，名额继续占用。重启后提示继续执行，不另派新剧。
    private void ReleaseUserStoppedAutoPublish(string? workspaceRoot)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            if (TryNormalizeWorkspace(workspaceRoot, out var one))
                roots.Add(one);
        }
        else
        {
            foreach (var workspace in _queueExecution.ActiveWorkspaces())
            {
                if (TryNormalizeWorkspace(workspace, out var running) &&
                    !roots.Contains(running, StringComparer.OrdinalIgnoreCase))
                    roots.Add(running);
            }

            if (TryNormalizeWorkspace(WorkspacePath, out var current) &&
                !roots.Contains(current, StringComparer.OrdinalIgnoreCase))
                roots.Add(current);
            lock (_checkedQueueFollowUpLock)
            {
                foreach (var key in _checkedQueueFollowUps.Keys)
                {
                    if (!roots.Contains(key, StringComparer.OrdinalIgnoreCase))
                        roots.Add(key);
                }
            }
        }

        var released = 0;
        foreach (var root in roots)
            released += ReleaseUserStoppedAutoPublishWorkspace(root);
        if (released == 0)
            return;

        var text = $"已手动停止，{released} 部服务器派单仍保留。勾选保留，重启后会提示继续执行，60 秒后自动开始。";
        StatusMessage = text;
        AppendLog(text);
    }

    private int ReleaseUserStoppedAutoPublishWorkspace(string root)
    {
        List<string> extraDirs;
        _dispatchScheduler.ClearPending(root);
        lock (_checkedQueueFollowUpLock)
        {
            _manualStopEpoch[root] = _manualStopEpoch.GetValueOrDefault(root) + 1;
            extraDirs = _checkedQueueFollowUps.TryGetValue(root, out var pending)
                ? pending.ProjectDirs.ToList()
                : [];
            _checkedQueueFollowUps.Remove(root);
            _checkedQueueFollowUpArmed.Remove(root);
        }

        var itemsByDir = new Dictionary<string, QueueProjectItem>(StringComparer.OrdinalIgnoreCase);
        void Remember(QueueProjectItem? item)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.ProjectDir))
                return;
            if (!TryNormalizeWorkspace(item.ProjectDir, out var full))
                return;
            itemsByDir[full] = item;
        }

        try
        {
            foreach (var item in WorkspaceQueueDatabase.Load(root).Items)
                Remember(item);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLog($"读取队列以停止派单失败：{ex.Message}");
        }

        if (IsActiveWorkspace(root))
        {
            foreach (var item in _queueItems)
                Remember(item);
        }

        foreach (var dir in extraDirs)
        {
            if (TryNormalizeWorkspace(dir, out var full) && !itemsByDir.ContainsKey(full))
                itemsByDir[full] = new QueueProjectItem { ProjectDir = full };
        }

        var releasedDirs = new List<string>();
        foreach (var (dir, item) in itemsByDir)
        {
            if (QueueWorkerRunner.HasFinishedUpload(item))
                continue;
            var marker = XingeAutoPublishMarkerStore.TryRead(dir);
            if (marker is not { IsActive: true })
                continue;
            try
            {
                XingeAutoPublishMarkerStore.MarkFinal(dir, marker.AssignmentId, XingeAutoPublishStatuses.UserStopped);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppendLog($"更新自动上新标记失败：{ex.Message}");
                continue;
            }

            XingeAutoPublishProgressReporter.Shared.Enqueue(XingeAutoPublishProgress.For(
                marker.ToAssignment(),
                XingeAutoPublishStatuses.UserStopped,
                "upload_series",
                "用户手动停止",
                XingeAutoPublishFailureKinds.UserStopped));
            _stoppedThisSession.Add(marker.AssignmentId);
            releasedDirs.Add(dir);
        }

        if (releasedDirs.Count == 0)
            return 0;

        MarkUserStoppedProjects(root, releasedDirs);
        return releasedDirs.Count;
    }

    private void MarkUserStoppedProjects(string root, IReadOnlyCollection<string> projectDirs)
    {
        var stopped = projectDirs as HashSet<string> ?? new HashSet<string>(projectDirs, StringComparer.OrdinalIgnoreCase);
        var changed = false;
        if (IsActiveWorkspace(root))
        {
            foreach (var item in _queueItems)
                changed |= MarkRunningProjectStopped(item, stopped);

            if (changed)
            {
                RefreshQueueRowViewModels();
                UpdateQueueSummaryText();
            }
        }

        try
        {
            var state = WorkspaceQueueDatabase.Load(root);
            var saved = false;
            foreach (var item in state.Items)
                saved |= MarkRunningProjectStopped(item, stopped);

            if (saved)
                WorkspaceQueueService.SaveProjects(root, state.Items, state.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLog($"记录已停止派单的步骤失败：{ex.Message}");
        }
    }

    private bool MarkRunningProjectStopped(QueueProjectItem item, IReadOnlyCollection<string> stopped)
    {
        if (string.IsNullOrWhiteSpace(item.ProjectDir))
            return false;
        if (!TryNormalizeWorkspace(item.ProjectDir, out var full) || !stopped.Contains(full))
            return false;

        var running = string.Equals(item.StatusText, QueueStepStatus.Running, StringComparison.Ordinal)
            || string.Equals(item.StatusText, QueueStepStatus.WaitingUploadSlot, StringComparison.Ordinal);
        if (!running)
            return false;

        var step = item.CurrentStep ?? "";
        item.CurrentStep = "";
        item.StatusText = QueueStepStatus.Stopped;
        if (!string.IsNullOrWhiteSpace(step))
            item.StepStates[step] = QueueStepStatus.Stopped;
        return true;
    }

    private void RememberDispatchedProjectsOnScreen(string root, IReadOnlyList<QueueProjectItem> candidates)
    {
        if (!IsActiveWorkspace(root) || candidates.Count == 0)
            return;

        var existing = _queueItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ProjectDir))
            .Select(item => Path.GetFullPath(item.ProjectDir))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = false;
        foreach (var item in candidates)
        {
            if (string.IsNullOrWhiteSpace(item.ProjectDir))
                continue;
            if (!existing.Add(Path.GetFullPath(item.ProjectDir)))
                continue;
            _queueItems.Add(item);
            added = true;
        }

        if (!added)
            return;

        ReconcileQueueProjectRows(_queueItems, replaceCollections: true);
        ApplyQueueProjectFilter(replaceCollection: true);
        FilteredQueueProjectRows.ReplaceAll(FilteredQueueProjectRows.ToArray());
        UpdateQueueSummaryText();
    }

    // 已分发的剧本地失败后先再跑，不满 3 次不向服务器交还，服务器也就不会重新分发。
    private void ScheduleLocalAutoPublishRetries(string workspace)
    {
        if (!TryNormalizeWorkspace(workspace, out var root))
            return;
        List<QueueProjectItem> items;
        try
        {
            items = WorkspaceQueueDatabase.Load(root).Items.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var item in items)
        {
            if (!string.Equals(item.StatusText, QueueStepStatus.Failed, StringComparison.Ordinal))
                continue;
            var marker = XingeAutoPublishMarkerStore.TryRead(item.ProjectDir);
            if (marker is not { IsActive: true } || marker.LocalFailures <= 0 ||
                marker.LocalFailures >= XingeAutoPublishRecovery.LocalRetryLimit)
                continue;
            if (!item.Enabled)
            {
                item.Enabled = true;
                changed = true;
            }
            var accountId = (item.AccountProfileId ?? marker.AccountProfileId ?? "").Trim();
            if (!groups.TryGetValue(accountId, out var dirs))
            {
                dirs = [];
                groups[accountId] = dirs;
            }
            dirs.Add(item.ProjectDir);
        }

        if (groups.Count == 0)
            return;
        if (changed)
        {
            try
            {
                WorkspaceQueueService.SaveRunOptions(root, items, WorkspaceQueueService.LoadRunOptions(root));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppendLog($"保存本地重试勾选失败：{ex.Message}");
            }
        }

        var count = groups.Values.Sum(dirs => dirs.Count);
        foreach (var (accountId, dirs) in groups)
            NoteAutoPublishDispatch(root, accountId, dirs, null);
        AppendLog($"本地已分发的 {count} 部执行失败，先在本机重试，不向服务器重新要单。");
    }

    // 当前勾选队列结束后，如果期间又有派单更新了勾选，再走一次「执行勾选队列」。
    // 用户手动停止会抬高停止序号并清掉当时的跟进，这一轮结束后不再自动开跑。
    public void NotifyCheckedQueueRunFinished(string workspace)
    {
        _dispatchScheduler.Complete(workspace);
        ScheduleLocalAutoPublishRetries(workspace);
        _ = PumpAutoPublishQueuesAsync();
        if (!TryNormalizeWorkspace(workspace, out var root))
            return;
        lock (_checkedQueueFollowUpLock)
        {
            if (!_checkedQueueFollowUps.TryGetValue(root, out var pending))
                return;
            if (pending.StopEpoch != _manualStopEpoch.GetValueOrDefault(root))
            {
                _checkedQueueFollowUps.Remove(root);
                return;
            }
        }

        ArmCheckedQueueFollowUp(root);
    }

    private async Task<TikTokRemoteCommandResult> ExecuteAutoPublishCheckedQueueAsync(
        TikTokRemoteCommand command,
        RemoteAutoPublishContext autoPublish,
        string workspace,
        TikTokAccountProfile? account,
        FinalAction finalAction,
        IReadOnlyList<string> titles)
    {
        ClientSettingsStore.RememberServerDispatchMode(command.DispatchMode);
        var root = Path.GetFullPath(workspace);
        var accountId = FirstNonEmptyText(command.AccountProfileId, account?.Id);
        var ensured = new List<string>();
        var located = FindAutoPublishProject(autoPublish.Assignment.AssignmentId, CaptureXingeAccountSeeds());
        if (located.ProjectExists && Directory.Exists(located.ProjectDir))
        {
            QueueRetryUseCase.RefreshAssignment(located.ProjectDir, autoPublish.Assignment.AssignmentId, accountId, AppendLog);
            ensured.Add(Path.GetFullPath(located.ProjectDir));
            AppendLog($"自动上新派单重新执行勾选队列：{located.ProjectDir}");
        }
        else
        {
            var (rawText, matchMode) = BuildRemoteUploadImportInput(command, titles);
            var importMarket = string.Equals(command.DownloadSource, "dramabox", StringComparison.OrdinalIgnoreCase)
                ? TikTokAccountProfile.DramaMarketOverseas
                : account?.TiktokDramaMarket;
            var importChain = VisibleDramaSource.ChainFor(ClientSettingsStore.Load(), importMarket);
            var importOutcome = await ImportRemoteUploadTitlesAsync(
                root,
                account,
                rawText,
                UploadTitleImportService.DefaultEpisodeMin,
                UploadTitleImportService.DefaultEpisodeMax,
                matchMode,
                finalAction,
                allowAppendToRunningQueue: false,
                CancellationToken.None,
                autoPublish: autoPublish,
                keepExistingSelection: true,
                downloadSource: command.DownloadSource,
                forcedSeriesId: autoPublish.Assignment.SeriesId.StartsWith("dramabox:", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(importChain, "dramabox", StringComparison.OrdinalIgnoreCase)
                    ? autoPublish.Assignment.SeriesId
                    : "",
                useXingeProcessedHistory: command.UseXingeProcessedHistory);
            var result = importOutcome.ImportResult;
            if (result.ProjectDirs.Count == 0)
            {
                var duplicateSuffix = result.Duplicates.Count > 0 ? $"，重复 {result.Duplicates.Count} 个" : "";
                var failurePreview = result.Failures.Count > 0
                    ? UploadTitleImportService.BuildFailurePreview(result.Failures)
                    : "没有匹配到可执行项目。";
                if (result.Duplicates.Count > 0 && result.Failures.Count == 0)
                {
                    await ReportRecoveredAutoPublishAsync(
                        autoPublish.Assignment,
                        XingeAutoPublishStatuses.Success,
                        "import",
                        $"管理系统已存在上传记录：{string.Join("、", result.Duplicates)}");
                    return TikTokRemoteCommandResult.Success(command.Command, $"没有需要上传的新剧（全部已存在）{duplicateSuffix}。");
                }

                await ReportRemoteAutoPublishFailureAsync(
                    autoPublish,
                    "import",
                    failurePreview,
                    XingeAutoPublishFailureClassifier.ClassifyImportFailure(result.Failures.FirstOrDefault()?.Reason));
                return TikTokRemoteCommandResult.Failed(command.Command, $"TikTok 剧集导入失败：{failurePreview}{duplicateSuffix}");
            }

            ensured.AddRange(BuildOrderedDistinctProjectDirs(result.ProjectDirs));
            XingeAutoPublishProgressReporter.Shared.Enqueue(XingeAutoPublishProgress.For(
                autoPublish.Assignment,
                XingeAutoPublishStatuses.Accepted,
                "import",
                $"已导入 {result.QueuedCount} 个项目"));
        }

        var serverOptions = command.EnabledSteps is { Count: > 0 }
            ? TikTokRemoteRunOptions.BuildTikTokUploadRunOptions(command)
            : null;
        if (serverOptions is not null)
            ApplyAutoPublishServerSteps(account, root, serverOptions);

        var dispatchMode = QueueProjectSlotCoordinator.NormalizeServerDispatchMode(command.DispatchMode);
        if (dispatchMode.Length == 0)
            dispatchMode = ClientSettingsStore.Load().ServerDispatchMode;
        var roundRobin = QueueProjectSlotCoordinator.ServerUsesRoundRobin(dispatchMode);
        var manualQueue = IsLocalCheckedQueueRunning(root);
        var queueBusy = IsWorkspaceQueueBusy(root) || manualQueue;
        var joinedRunningQueue = 0;
        if (!queueBusy)
            ApplyTodayAutoPublishChecks(root, accountId, ensured);
        else
        {
            var candidates = BuildRemoteAppendCandidates(ensured, account);
            var executeNow = !roundRobin && !manualQueue;
            joinedRunningQueue = executeNow
                ? _queueOrchestrator.TryAppendItemsToRunningWorkspace(root, candidates)
                : _queueOrchestrator.ShowItemsInRunningWorkspace(root, candidates);
            if (joinedRunningQueue == 0)
                RememberDispatchedProjectsOnScreen(root, candidates);
        }

        NoteAutoPublishDispatch(root, accountId, ensured, serverOptions);
        if (joinedRunningQueue > 0 && !roundRobin && !manualQueue)
            _dispatchScheduler.DropPending(root, ensured);
        var orderText = roundRobin ? "按轮流顺序" : "按账号顺序";
        var text = !queueBusy
            ? $"已收下今日派发的 {ensured.Count} 部，将{orderText}执行勾选队列。"
            : roundRobin || manualQueue
                ? $"已收下今日派发的 {ensured.Count} 部，已显示在列表里，当前这部结束后{orderText}继续。"
                : $"已收下今日派发的 {ensured.Count} 部，已加入正在执行的队列。";
        StatusMessage = text;
        AppendLog(text);
        return TikTokRemoteCommandResult.Accepted(command.Command, text);
    }

    private void OnAutoPublishPollIdle()
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            _ = PumpAutoPublishQueuesAsync();
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = PumpAutoPublishQueuesAsync());
    }

    private void NoteAutoPublishDispatch(
        string workspace,
        string? accountId,
        IEnumerable<string> projectDirs,
        QueueRunOptions? options,
        XingeAutoPublishProgress? startNotice = null)
    {
        if (!TryNormalizeWorkspace(workspace, out var root))
            return;
        var dirs = projectDirs
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (dirs.Length == 0)
            return;
        if (options is not null)
            _dispatchOptions[root] = options.Clone();
        if (startNotice is not null)
        {
            foreach (var dir in dirs)
                _dispatchStartNotices[dir] = startNotice;
        }

        _dispatchScheduler.Note(root, accountId, AccountListOrder(accountId), dirs);
    }

    private int AccountListOrder(string? accountId)
    {
        var id = (accountId ?? "").Trim();
        var accounts = _store.Accounts;
        for (var i = 0; i < accounts.Count; i++)
        {
            if (string.Equals(accounts[i].Id, id, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return int.MaxValue;
    }

    private async Task PumpAutoPublishQueuesAsync()
    {
        if (!await _dispatchPump.WaitAsync(0).ConfigureAwait(true))
            return;
        try
        {
            var settings = ClientSettingsStore.Load();
            var roundRobin = QueueProjectSlotCoordinator.ServerUsesRoundRobin(settings.ServerDispatchMode);
            var global = settings.TiktokGlobalProjectConcurrency;
            while (true)
            {
                var starts = _dispatchScheduler.Take(global, roundRobin, IsWorkspaceQueueBusy);
                if (starts.Count == 0)
                    return;
                var pendingStart = starts.ToList();
                foreach (var start in starts)
                {
                    pendingStart.Remove(start);
                    try
                    {
                        if (await StartAutoPublishSliceAsync(start, settings, roundRobin).ConfigureAwait(true))
                            continue;
                        _dispatchScheduler.Restore(start.Workspace, start.ProjectDirs);
                    }
                    catch (Exception ex)
                    {
                        _dispatchScheduler.Restore(start.Workspace, start.ProjectDirs);
                        AppendLog($"按顺序执行勾选队列失败：{ex.Message}");
                    }

                    foreach (var left in pendingStart)
                        _dispatchScheduler.Restore(left.Workspace, left.ProjectDirs);
                    return;
                }
            }
        }
        finally
        {
            _dispatchPump.Release();
        }
    }

    private async Task<bool> StartAutoPublishSliceAsync(
        TikTokDispatchStart start,
        ClientSettings settings,
        bool roundRobin)
    {
        var run = RemoteQueueRunRequested;
        if (run is null)
        {
            AppendLog("队列视图尚未初始化，自动派单仍留在执行顺序里。");
            return false;
        }

        if (!IsWorkspaceQueueBusy(start.Workspace) && !IsLocalCheckedQueueRunning(start.Workspace))
            ApplyTodayAutoPublishChecks(start.Workspace, start.AccountId, start.ProjectDirs);
        var options = _dispatchOptions.TryGetValue(start.Workspace, out var saved)
            ? saved.Clone()
            : CreateCurrentQueueRunOptionsSnapshot();
        var accountParallel = FindAccountById(start.AccountId)?.Model.TiktokProjectConcurrency ?? options.ProjectConcurrency;
        if (accountParallel < 1)
            accountParallel = 4;
        var machineParallel = Math.Clamp(
            settings.TiktokGlobalProjectConcurrency <= 0 ? QueueProjectSlotCoordinator.DefaultCapacity : settings.TiktokGlobalProjectConcurrency,
            1,
            QueueProjectSlotCoordinator.MaxCapacity);
        options.ProjectConcurrency = roundRobin ? 1 : Math.Clamp(Math.Min(accountParallel, machineParallel), 1, 20);
        var account = FindAccountById(start.AccountId)?.Model;
        var target = new WorkspaceQueueTarget(
            start.Workspace,
            $"{account?.DisplayName ?? start.AccountId} · {start.Workspace}",
            start.AccountId,
            start.ProjectDirs);
        AppendLog(roundRobin
            ? $"按轮流顺序执行勾选队列：{account?.DisplayName ?? start.AccountId}，1 部。"
            : $"按账号顺序执行勾选队列：{account?.DisplayName ?? start.AccountId}，{start.ProjectDirs.Count} 部。");
        var started = await run(target, options).ConfigureAwait(true);
        if (!started)
            return false;
        foreach (var dir in start.ProjectDirs)
        {
            var full = Path.GetFullPath(dir);
            if (!_dispatchStartNotices.Remove(full, out var notice))
                continue;
            XingeAutoPublishProgressReporter.Shared.Enqueue(notice);
        }

        return true;
    }

    private async Task<bool> StartCheckedQueueFromUiAsync()
    {
        var run = CheckedQueueRunRequested;
        if (run is null)
        {
            AppendLog("队列视图尚未初始化，无法执行勾选队列。");
            return false;
        }

        return await run().ConfigureAwait(true);
    }

    private int ApplyTodayAutoPublishChecks(
        string workspaceRoot,
        string? accountId,
        IReadOnlyCollection<string> ensureProjectDirs)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var busy = IsWorkspaceQueueBusy(root);
        var today = DateTime.Today;
        var ensured = BuildOrderedDistinctProjectDirs(ensureProjectDirs);
        List<QueueProjectItem> items;
        if (!busy)
        {
            var existing = WorkspaceQueueService.ScanProjects(root)
                .Where(item => !string.IsNullOrWhiteSpace(item.ProjectDir))
                .Select(item => Path.GetFullPath(item.ProjectDir))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = ensured.Where(dir => !existing.Contains(dir)).ToArray();
            if (missing.Length > 0)
            {
                var account = FindAccountById(accountId)?.Model;
                WorkspaceQueueService.AddProjectsToQueue(root, missing, accountId, account?.DisplayName);
            }

            items = WorkspaceQueueService.ScanProjects(root).ToList();
        }
        else if (IsActiveWorkspace(root))
        {
            items = _queueItems;
            var existing = items
                .Where(item => !string.IsNullOrWhiteSpace(item.ProjectDir))
                .Select(item => Path.GetFullPath(item.ProjectDir))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = ensured.Where(dir => !existing.Contains(dir)).ToArray();
            if (missing.Length > 0)
                items.AddRange(BuildRemoteAppendCandidates(missing, FindAccountById(accountId)?.Model));
        }
        else
        {
            return 0;
        }

        var checkedCount = 0;
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ProjectDir))
                continue;
            item.Enabled = XingeAutoPublishMarkerStore.ShouldKeepChecked(
                XingeAutoPublishMarkerStore.TryRead(item.ProjectDir),
                accountId,
                today);
            if (item.Enabled)
                checkedCount++;
        }

        if (!busy)
        {
            WorkspaceQueueService.SaveRunOptions(root, items, WorkspaceQueueService.LoadRunOptions(root));
            if (IsActiveWorkspace(root))
            {
                _queueItems = items;
                RefreshQueueRowViewModels();
                UpdateQueueSummaryText();
            }
        }
        else
        {
            RefreshQueueRowViewModels();
            UpdateQueueSummaryText();
        }

        AppendLog($"已按今日派单更新勾选：{checkedCount} 部。");
        return checkedCount;
    }

    private int CountCheckedQueueItems(string workspaceRoot)
    {
        if (IsActiveWorkspace(workspaceRoot))
            return _queueItems.Count(item => item.Enabled);
        try
        {
            return WorkspaceQueueService.ScanProjects(workspaceRoot).Count(item => item.Enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void RequestCheckedQueueFollowUp(
        string workspace,
        string accountId,
        IReadOnlyCollection<string> projectDirs,
        QueueRunOptions? serverOptions = null)
    {
        var root = Path.GetFullPath(workspace);
        lock (_checkedQueueFollowUpLock)
        {
            if (!_checkedQueueFollowUps.TryGetValue(root, out var pending))
            {
                pending = new PendingCheckedQueue();
                _checkedQueueFollowUps[root] = pending;
            }

            if (!string.IsNullOrWhiteSpace(accountId))
                pending.AccountId = accountId;
            if (serverOptions is not null)
                pending.ServerOptions = serverOptions.Clone();
            foreach (var dir in projectDirs)
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                var full = Path.GetFullPath(dir);
                if (!pending.ProjectDirs.Contains(full, StringComparer.OrdinalIgnoreCase))
                    pending.ProjectDirs.Add(full);
            }

            pending.StopEpoch = _manualStopEpoch.GetValueOrDefault(root);
            pending.Generation++;
        }

        ArmCheckedQueueFollowUp(root);
    }

    private void ArmCheckedQueueFollowUp(string workspace)
    {
        if (!TryNormalizeWorkspace(workspace, out var root))
            return;
        lock (_checkedQueueFollowUpLock)
        {
            if (!_checkedQueueFollowUpArmed.Add(root))
                return;
        }

        void Tick()
        {
            lock (_checkedQueueFollowUpLock)
                _checkedQueueFollowUpArmed.Remove(root);
            _ = TryStartCheckedQueueFollowUpAsync(root);
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            Avalonia.Threading.DispatcherTimer.RunOnce(Tick, TimeSpan.FromMilliseconds(400));
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                Avalonia.Threading.DispatcherTimer.RunOnce(Tick, TimeSpan.FromMilliseconds(400)));
    }

    private async Task TryStartCheckedQueueFollowUpAsync(string workspace)
    {
        PendingCheckedQueue pending;
        int seen;
        int stopEpoch;
        lock (_checkedQueueFollowUpLock)
        {
            if (!_checkedQueueFollowUps.TryGetValue(workspace, out pending!))
                return;
            if (!_checkedQueueFollowUpRunning.Add(workspace))
                return;
            seen = pending.Generation;
            stopEpoch = pending.StopEpoch;
        }

        var started = false;
        QueueRunOptions? serverOptions;
        try
        {
            if (IsWorkspaceQueueBusy(workspace))
                return;

            string accountId;
            string[] dirs;
            lock (_checkedQueueFollowUpLock)
            {
                accountId = pending.AccountId;
                dirs = pending.ProjectDirs.ToArray();
                serverOptions = pending.ServerOptions?.Clone();
            }

            ApplyTodayAutoPublishChecks(workspace, accountId, dirs);
            if (!IsActiveWorkspace(workspace))
                await ShowAutoPublishWorkspaceAsync(workspace).ConfigureAwait(true);
            if (serverOptions is not null)
                ApplyAutoPublishServerSteps(FindAccountById(accountId)?.Model, workspace, serverOptions);
            lock (_checkedQueueFollowUpLock)
            {
                if (_manualStopEpoch.GetValueOrDefault(workspace) != stopEpoch)
                {
                    if (_checkedQueueFollowUps.TryGetValue(workspace, out var stopped) && stopped.Generation == seen)
                        _checkedQueueFollowUps.Remove(workspace);
                    AppendLog("队列已手动停止，不再自动执行这一轮勾选。");
                    return;
                }
            }

            var checkedCount = CountCheckedQueueItems(workspace);
            if (checkedCount == 0)
            {
                lock (_checkedQueueFollowUpLock)
                {
                    if (_checkedQueueFollowUps.TryGetValue(workspace, out var current) && current.Generation == seen)
                        _checkedQueueFollowUps.Remove(workspace);
                }

                AppendLog("今日派单没有可执行项目，未再启动勾选队列。");
                return;
            }

            started = await StartCheckedQueueFromUiAsync().ConfigureAwait(true);
            lock (_checkedQueueFollowUpLock)
            {
                if (_checkedQueueFollowUps.TryGetValue(workspace, out var current) &&
                    current.Generation == seen &&
                    (started || !IsWorkspaceQueueBusy(workspace)))
                {
                    _checkedQueueFollowUps.Remove(workspace);
                }
            }

            if (started)
                AppendLog($"队列结束后已按「执行勾选队列」执行今日派发的 {checkedCount} 部。");
            else if (!IsWorkspaceQueueBusy(workspace))
                AppendLog("今日派单已勾选，但勾选队列未启动。");
        }
        finally
        {
            var again = false;
            lock (_checkedQueueFollowUpLock)
            {
                _checkedQueueFollowUpRunning.Remove(workspace);
                again = _checkedQueueFollowUps.ContainsKey(workspace);
            }

            if (again)
                ArmCheckedQueueFollowUp(workspace);
        }
    }

    private async Task ShowAutoPublishWorkspaceAsync(string workspace)
    {
        var full = Path.GetFullPath(workspace);
        WorkspacePath = full;
        SystemSettings.UpdateWorkspacePath(full);
        ArchivedProjects.SetWorkspace(full);
        await RefreshWorkspaceProjectsNowAsync(full).ConfigureAwait(true);
    }

    private static bool TryNormalizeWorkspace(string workspace, out string root)
    {
        root = "";
        if (string.IsNullOrWhiteSpace(workspace))
            return false;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private async Task<TikTokRemoteCommandResult> ExecuteRemoteUploadSeriesCoreAsync(
        TikTokRemoteCommand command,
        RemoteAutoPublishContext? autoPublish)
    {
        var titles = command.Titles?.Where(title => !string.IsNullOrWhiteSpace(title)).ToList() ?? [];
        if (titles.Count == 0)
            return TikTokRemoteCommandResult.Failed(command.Command, "未提供可上传的 TikTok 剧名。");
        if (command.HasMultiAccountSelection)
        {
            // 多账号远程运行仍沿用原有的全局互斥语义；单账号命令则可以追加到目标工作目录的运行队列。
            if (IsQueueRunning)
                return TikTokRemoteCommandResult.Failed(command.Command, XingeAutoPublishFailureClassifier.TikTokQueueBusyDetail);
            return await ExecuteRemoteUploadSeriesMultiAccountAsync(command, titles);
        }

        if (autoPublish is null &&
            TryPeekRemoteCommandWorkspace(command, out var blockedWorkspace) &&
            IsLocalCheckedQueueRunning(blockedWorkspace))
        {
            return DeferRemoteCommandWhileLocalCheckedQueueRuns(command, blockedWorkspace);
        }

        var hasExplicitAccount = command.HasExplicitAccountSelection;
        if (hasExplicitAccount && !TryApplyRemoteAccountSelection(command, "", out var accountError))
            return TikTokRemoteCommandResult.Failed(command.Command, accountError);
        if (!TryResolveRemoteWorkspace(command, out var workspace, out var error))
            return TikTokRemoteCommandResult.Failed(command.Command, error);
        if (!hasExplicitAccount && !TryApplyRemoteAccountSelection(command, workspace, out error))
            return TikTokRemoteCommandResult.Failed(command.Command, error);

        // 后续导入会跨越网络与磁盘 await。必须在 await 前固定目标，不能在完成后再读取可能已被用户切换的 UI 状态。
        var targetWorkspace = Path.GetFullPath(workspace);
        if (autoPublish is null && IsLocalCheckedQueueRunning(targetWorkspace))
            return DeferRemoteCommandWhileLocalCheckedQueueRuns(command, targetWorkspace);
        var targetAccount = SelectedAccount?.Model;
        var finalAction = SelectedFinalAction?.Value ?? FinalAction.None;
        if (autoPublish is not null)
            return await ExecuteAutoPublishCheckedQueueAsync(command, autoPublish, targetWorkspace, targetAccount, finalAction, titles);
        var runQueueRequested = RemoteQueueRunRequested;
        ActivateRemoteWorkspace(targetWorkspace);

        var options = TikTokRemoteRunOptions.BuildTikTokUploadRunOptions(command);
        options.ProjectConcurrency = Math.Clamp(targetAccount?.TiktokProjectConcurrency ?? options.ProjectConcurrency, 1, 20);
        if (command.EnabledSteps is { Count: > 0 })
        {
            ApplyServerConfiguredQueueSteps(targetAccount, options);
            options = QueueOptionsFromLocalChecks(targetAccount, targetWorkspace, options);
            _queueOrchestrator.ApplyRunningQueueSteps(targetWorkspace, options);
        }

        var (singleRawText, singleMatchMode) = BuildRemoteUploadImportInput(command, titles);
        var importOutcome = await ImportRemoteUploadTitlesAsync(
            targetWorkspace,
            targetAccount,
            singleRawText,
            UploadTitleImportService.DefaultEpisodeMin,
            UploadTitleImportService.DefaultEpisodeMax,
            singleMatchMode,
            finalAction,
            allowAppendToRunningQueue: command.AutoRun,
            CancellationToken.None,
            autoPublish: autoPublish,
            useXingeProcessedHistory: command.UseXingeProcessedHistory);

        if (command.AutoRun && IsLocalCheckedQueueRunning(targetWorkspace))
            return DeferRemoteCommandWhileLocalCheckedQueueRuns(command, targetWorkspace);

        var result = importOutcome.ImportResult;
        if (result.ProjectDirs.Count == 0)
        {
            var duplicateSuffix = result.Duplicates.Count > 0 ? $"，重复 {result.Duplicates.Count} 个" : "";
            var failurePreview = result.Failures.Count > 0
                ? UploadTitleImportService.BuildFailurePreview(result.Failures)
                : "没有匹配到可执行项目。";
            if (autoPublish is not null)
            {
                var duplicateOnly = result.Duplicates.Count > 0 && result.Failures.Count == 0;
                if (duplicateOnly)
                {
                    await ReportRecoveredAutoPublishAsync(
                        autoPublish.Assignment,
                        XingeAutoPublishStatuses.Success,
                        "import",
                        $"管理系统已存在上传记录：{string.Join("、", result.Duplicates)}");
                    return TikTokRemoteCommandResult.Success(command.Command, $"没有需要上传的新剧（全部已存在）{duplicateSuffix}。");
                }

                // 剧不存在或不符合导入条件：这部剧不应再派给任何账号。
                await ReportRemoteAutoPublishFailureAsync(
                    autoPublish,
                    "import",
                    failurePreview,
                    XingeAutoPublishFailureClassifier.ClassifyImportFailure(result.Failures.FirstOrDefault()?.Reason));
            }
            return result.Duplicates.Count > 0 && result.Failures.Count == 0
                ? TikTokRemoteCommandResult.Success(command.Command, $"没有需要上传的新剧（全部已存在）{duplicateSuffix}。")
                : TikTokRemoteCommandResult.Failed(command.Command, $"TikTok 剧集导入失败：{failurePreview}{duplicateSuffix}");
        }

        if (autoPublish is not null)
        {
            XingeAutoPublishProgressReporter.Shared.Enqueue(XingeAutoPublishProgress.For(
                autoPublish.Assignment,
                XingeAutoPublishStatuses.Accepted,
                "import",
                $"已导入 {result.QueuedCount} 个项目"));
        }

        var authorExcludeNotice = UploadTitleImportService.BuildAuthorExcludeNotice(result.Failures);
        if (!command.AutoRun)
        {
            var text = $"已导入 {result.QueuedCount} 个 TikTok 项目。"
                       + (result.FailedCount > 0 ? $" 未导入 {result.FailedCount} 个。" : "")
                       + (result.Duplicates.Count > 0 ? $" 重复 {result.Duplicates.Count} 个。" : "")
                       + (string.IsNullOrWhiteSpace(authorExcludeNotice) ? "" : $" {authorExcludeNotice}");
            return TikTokRemoteCommandResult.Success(command.Command, text);
        }

        TikTokRemoteCommandResult BuildAppendAcceptedResult(int appendedCount)
        {
            var text = $"远程上传任务已导入：已按当前运行队列配置接纳 {appendedCount} 个并追加到末尾，未导入 {result.FailedCount} 个。"
                       + (string.IsNullOrWhiteSpace(authorExcludeNotice) ? "" : $" {authorExcludeNotice}");
            StatusMessage = text;
            AppendLog(text);
            return TikTokRemoteCommandResult.Accepted(command.Command, text);
        }

        var applyOutcome = importOutcome.ApplyOutcome;
        if (!applyOutcome.QueueWasRunning &&
            applyOutcome.AppendedCount == 0 &&
            applyOutcome.AppendCandidateCount > 0 &&
            IsWorkspaceQueueRunning(targetWorkspace))
        {
            ClearManualSelectionBeforeAutoPublish(targetWorkspace, autoPublish, applyOutcome.OrderedProjectDirs);
            var lateAppended = _queueOrchestrator.TryAppendItemsToRunningWorkspace(
                targetWorkspace,
                applyOutcome.AppendCandidates);
            applyOutcome = applyOutcome with
            {
                QueueWasRunning = true,
                AppendedCount = lateAppended,
            };
        }

        if (applyOutcome.AppendedCount > 0)
            return BuildAppendAcceptedResult(applyOutcome.AppendedCount);

        // AddItems 返回的是实际接纳数量。若队列恰好进入收尾而拒绝追加，短暂等待其退出后按本次导入过滤器启动新一轮。
        var queueFinishedClosing = true;
        if (applyOutcome.QueueWasRunning && applyOutcome.AppendCandidateCount > 0)
        {
            queueFinishedClosing = await WaitForWorkspaceQueueToFinishClosingAsync(
                targetWorkspace,
                TimeSpan.FromSeconds(3));
        }

        if (!queueFinishedClosing)
        {
            const string failureText = "远程上传任务已生成项目目录，但目标队列仍在执行持久化收尾，尚不能安全接纳新项目；请稍后重试。";
            StatusMessage = failureText;
            AppendLog(failureText);
            return await AcceptAutoPublishQueueDeferralAsync(command, autoPublish, failureText);
        }

        if (IsWorkspaceQueueRunning(targetWorkspace))
        {
            const string failureText = "远程上传任务已生成项目目录，但运行中的目标队列未接纳新项目，且队列尚未结束；请稍后重试。";
            StatusMessage = failureText;
            AppendLog(failureText);
            return await AcceptAutoPublishQueueDeferralAsync(command, autoPublish, failureText);
        }

        if (applyOutcome.QueueWasRunning)
        {
            // 运行分支刻意没有落盘；只有确认旧 runner 已退出后，才把本次项目加入持久队列并准备启动。
            var idlePreparation = PrepareRemoteUploadProjectsWhenIdle(
                targetWorkspace,
                applyOutcome.OrderedProjectDirs,
                targetAccount,
                clearOtherSelection: autoPublish is not null,
                preserveExistingFailures: autoPublish is not null);
            if (idlePreparation.ClearedSelectionCount > 0)
                AppendLog($"分发前已取消勾选 {idlePreparation.ClearedSelectionCount} 个手动选择的项目。");
            ApplyRemoteUploadIdlePreparationToDisplayedWorkspace(targetWorkspace, idlePreparation);
            applyOutcome = applyOutcome with
            {
                QueueWasRunning = false,
                AppendCandidates = idlePreparation.AppendCandidates,
            };
        }

        if (IsWorkspaceQueueRunning(targetWorkspace))
        {
            ClearManualSelectionBeforeAutoPublish(targetWorkspace, autoPublish, applyOutcome.OrderedProjectDirs);
            var lateAppended = _queueOrchestrator.TryAppendItemsToRunningWorkspace(
                targetWorkspace,
                applyOutcome.AppendCandidates);
            if (lateAppended > 0)
                return BuildAppendAcceptedResult(lateAppended);

            const string failureText = "远程上传任务已导入，但目标队列被其它操作抢先启动且未接纳新项目；未重复启动队列。";
            StatusMessage = failureText;
            AppendLog(failureText);
            return await AcceptAutoPublishQueueDeferralAsync(command, autoPublish, failureText);
        }

        if (runQueueRequested is null)
            return TikTokRemoteCommandResult.Failed(command.Command, "剧集已导入，但队列视图尚未初始化，无法启动 TikTok 队列。");

        var started = await runQueueRequested.Invoke(importOutcome.RunTarget, options);
        if (!started)
        {
            ClearManualSelectionBeforeAutoPublish(targetWorkspace, autoPublish, applyOutcome.OrderedProjectDirs);
            var appendedAfterRejectedStart = _queueOrchestrator.TryAppendItemsToRunningWorkspace(
                targetWorkspace,
                applyOutcome.AppendCandidates);
            if (appendedAfterRejectedStart > 0)
                return BuildAppendAcceptedResult(appendedAfterRejectedStart);

            const string failureText = "远程上传任务已导入，但目标队列既未启动，也未接纳新项目。";
            StatusMessage = failureText;
            AppendLog(failureText);
            return TikTokRemoteCommandResult.Failed(command.Command, failureText);
        }

        return TikTokRemoteCommandResult.Accepted(
            command.Command,
            $"远程上传任务已导入并启动队列：已加入执行 {applyOutcome.OrderedProjectDirs.Count} 个，未导入 {result.FailedCount} 个。"
            + (string.IsNullOrWhiteSpace(authorExcludeNotice) ? "" : $" {authorExcludeNotice}"));
    }

    private async Task<TikTokRemoteCommandResult> ExecuteRemoteStartMultiAccountQueueAsync(TikTokRemoteCommand command)
    {
        if (!TryResolveRemoteAccountQueueTargets(command, out var targets, out var error))
            return TikTokRemoteCommandResult.Failed(command.Command, error);
        if (RemoteAllQueueRunRequested is null)
            return TikTokRemoteCommandResult.Failed(command.Command, "队列视图尚未初始化，无法执行远程多账号队列。");

        var options = BuildRemoteEnabledStepOptions(command);
        var started = await RemoteAllQueueRunRequested.Invoke(options, targets);
        if (!started)
            return TikTokRemoteCommandResult.Failed(command.Command, "TikTok 多账号队列未启动，目标工作目录可能已被其它任务占用。");
        return TikTokRemoteCommandResult.Accepted(
            command.Command,
            $"TikTok 多账号队列已启动：{targets.Count} 个工作目录。");
    }

    private async Task<TikTokRemoteCommandResult> ExecuteRemoteUploadSeriesMultiAccountAsync(
        TikTokRemoteCommand command,
        IReadOnlyList<string> titles)
    {
        if (!TryResolveRemoteAccountQueueTargets(command, out var targets, out var error))
            return TikTokRemoteCommandResult.Failed(command.Command, error);

        var (rawTitles, remoteMatchMode) = BuildRemoteUploadImportInput(command, titles);
        var totalQueued = 0;
        var totalFailed = 0;
        var totalDuplicates = 0;
        var totalAppended = 0;
        var runTargets = new List<WorkspaceQueueTarget>();
        var failures = new List<UploadTitleImportFailure>();

        foreach (var target in targets)
        {
            var account = FindAccountById(target.AccountProfileId);
            if (account is null)
            {
                failures.Add(new UploadTitleImportFailure(target.DisplayLabel, "未找到账号"));
                continue;
            }

            var importOutcome = await ImportRemoteUploadTitlesAsync(
                target.WorkspaceRoot,
                account.Model,
                rawTitles,
                UploadTitleImportService.DefaultEpisodeMin,
                UploadTitleImportService.DefaultEpisodeMax,
                remoteMatchMode,
                target.FinalActionOverride,
                allowAppendToRunningQueue: command.AutoRun,
                CancellationToken.None,
                useXingeProcessedHistory: command.UseXingeProcessedHistory);
            var result = importOutcome.ImportResult;

            totalQueued += result.QueuedCount;
            totalFailed += result.FailedCount;
            totalDuplicates += result.Duplicates.Count;
            if (result.Failures.Count > 0)
                failures.AddRange(result.Failures.Select(item => item with { Title = $"{account.DisplayName}/{item.Title}" }));
            if (result.ProjectDirs.Count > 0)
            {
                if (command.AutoRun)
                {
                    var preparation = await PrepareUploadTitleImportAutoRunAsync(importOutcome);
                    totalAppended += preparation.AppendedCount;
                    if (preparation.RunTarget is not null)
                        runTargets.Add(preparation.RunTarget);
                }
                else
                {
                    runTargets.Add(importOutcome.RunTarget);
                }
            }
        }

        if (totalQueued == 0)
        {
            var duplicateSuffix = totalDuplicates > 0 ? $"，重复 {totalDuplicates} 个" : "";
            if (failures.Count == 0 && totalDuplicates > 0)
                return TikTokRemoteCommandResult.Success(command.Command, $"没有需要上传的新剧（全部已存在）{duplicateSuffix}。");
            var failurePreview = failures.Count > 0
                ? UploadTitleImportService.BuildFailurePreview(failures, 5)
                : "没有匹配到可执行项目。";
            return TikTokRemoteCommandResult.Failed(command.Command, $"TikTok 多账号剧集导入失败：{failurePreview}{duplicateSuffix}");
        }

        var multiAuthorExcludeNotice = UploadTitleImportService.BuildAuthorExcludeNotice(failures, 5);
        if (!command.AutoRun)
        {
            var text = $"已为 {runTargets.Count} 个账号工作目录导入 {totalQueued} 个 TikTok 项目。"
                       + (totalFailed > 0 ? $" 未导入 {totalFailed} 个。" : "")
                       + (totalDuplicates > 0 ? $" 重复 {totalDuplicates} 个。" : "")
                       + (string.IsNullOrWhiteSpace(multiAuthorExcludeNotice) ? "" : $" {multiAuthorExcludeNotice}");
            return TikTokRemoteCommandResult.Success(command.Command, text);
        }

        if (runTargets.Count == 0 && totalAppended > 0)
        {
            return TikTokRemoteCommandResult.Accepted(
                command.Command,
                $"远程多账号上传任务已导入：{totalAppended} 个项目已追加到运行中的目标队列。"
                + (string.IsNullOrWhiteSpace(multiAuthorExcludeNotice) ? "" : $" {multiAuthorExcludeNotice}"));
        }

        if (RemoteAllQueueRunRequested is null)
            return TikTokRemoteCommandResult.Failed(command.Command, "剧集已导入，但队列视图尚未初始化，无法启动 TikTok 多账号队列。");

        var options = TikTokRemoteRunOptions.BuildTikTokUploadRunOptions(command);
        var started = await RemoteAllQueueRunRequested.Invoke(options, runTargets);
        if (!started)
            return TikTokRemoteCommandResult.Failed(command.Command, "TikTok 多账号剧集已导入，但目标队列未启动，请查看运行日志。");
        return TikTokRemoteCommandResult.Accepted(
            command.Command,
            $"远程多账号上传任务已导入并启动队列：{runTargets.Count} 个工作目录，加入执行 {totalQueued} 个，已追加 {totalAppended} 个，未导入 {totalFailed} 个。"
            + (string.IsNullOrWhiteSpace(multiAuthorExcludeNotice) ? "" : $" {multiAuthorExcludeNotice}"));
    }

    private QueueRunOptions? BuildRemoteEnabledStepOptions(TikTokRemoteCommand command)
    {
        if (command.EnabledSteps is not { Count: > 0 })
            return null;

        var options = CreateCurrentQueueRunOptionsSnapshot();
        options.EnabledSteps = QueueStepRegistry.OrderEnabledSteps(command.EnabledSteps).ToList();
        return options;
    }

    private bool TryResolveRemoteAccountQueueTargets(
        TikTokRemoteCommand command,
        out IReadOnlyList<WorkspaceQueueTarget> targets,
        out string error)
    {
        if (!TryResolveRemoteTargetAccounts(command, out var accounts, out error))
        {
            targets = [];
            return false;
        }

        targets = BuildAccountWorkspaceTargets(
            accounts.Select(account => account.Model),
            skipMissingWorkspace: command.AllAccounts,
            out var missingWorkspaceAccounts);
        if (missingWorkspaceAccounts.Count > 0)
        {
            error = $"以下账号没有配置有效工作目录：{string.Join("、", missingWorkspaceAccounts.Take(5))}";
            return false;
        }

        if (targets.Count == 0)
        {
            error = command.AllAccounts
                ? "没有可执行的账号工作目录，请先在账号基础设置中配置工作目录。"
                : "远程命令指定的账号没有可执行工作目录。";
            return false;
        }

        error = "";
        return true;
    }

    private bool TryResolveRemoteTargetAccounts(
        TikTokRemoteCommand command,
        out IReadOnlyList<AccountItemViewModel> accounts,
        out string error)
    {
        if (command.AllAccounts)
        {
            accounts = Accounts.ToList();
            error = accounts.Count > 0 ? "" : "当前没有可用 TikTok 账号。";
            return accounts.Count > 0;
        }

        var selectors = new List<string>();
        if (command.AccountSelectors is { Count: > 0 })
            selectors.AddRange(command.AccountSelectors);
        else
        {
            if (!string.IsNullOrWhiteSpace(command.AccountProfileId))
                selectors.Add(command.AccountProfileId);
            if (!string.IsNullOrWhiteSpace(command.AccountProfileName) &&
                !selectors.Contains(command.AccountProfileName, StringComparer.OrdinalIgnoreCase))
                selectors.Add(command.AccountProfileName);
        }

        if (selectors.Any(TikTokRemoteCommandParser.IsAllAccountsSelector))
        {
            accounts = Accounts.ToList();
            error = accounts.Count > 0 ? "" : "当前没有可用 TikTok 账号。";
            return accounts.Count > 0;
        }

        var resolved = new List<AccountItemViewModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selector in selectors.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            var account = FindAccount(selector);
            if (account is null)
            {
                accounts = [];
                error = $"未找到远程命令指定的 TikTok 账号：{selector}";
                return false;
            }

            if (seen.Add(account.Id))
                resolved.Add(account);
        }

        if (resolved.Count == 0)
        {
            accounts = [];
            error = "远程多账号命令未指定账号。";
            return false;
        }

        accounts = resolved;
        error = "";
        return true;
    }

    private TikTokRemoteCommandResult DeferRemoteCommandWhileLocalCheckedQueueRuns(
        TikTokRemoteCommand command,
        string workspace)
    {
        AppendLog($"本地正在执行勾选队列，已跳过本次远程命令，等待该队列结束后再派发：{workspace}");
        return TikTokRemoteCommandResult.Failed(command.Command, XingeAutoPublishFailureClassifier.TikTokQueueBusyDetail);
    }

    private bool TryPeekRemoteCommandWorkspace(TikTokRemoteCommand command, out string workspace)
    {
        workspace = "";
        var account = FindAccountById(command.AccountProfileId)?.Model;
        if (account is null && !string.IsNullOrWhiteSpace(command.AccountProfileName))
            account = FindAccount(command.AccountProfileName)?.Model;
        if (account is null && command.AccountSelectors is { Count: > 0 })
            account = FindAccount(command.AccountSelectors.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? "")?.Model;
        var candidates = new[]
        {
            account?.ResolveWorkspacePath(),
            command.WorkspacePath,
        };
        foreach (var candidate in candidates)
        {
            var path = (candidate ?? "").Trim();
            if (string.IsNullOrWhiteSpace(path))
                continue;
            try
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
                if (Directory.Exists(full))
                {
                    workspace = full;
                    return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return false;
    }

    private bool TryResolveRemoteWorkspace(TikTokRemoteCommand command, out string workspace, out string error)
    {
        var selectedAccountWorkspace = SelectedAccount?.Model.ResolveWorkspacePath() ?? "";
        var candidates = command.HasExplicitAccountSelection
            ? new[]
            {
                command.WorkspacePath,
                selectedAccountWorkspace,
                WorkspacePath,
            }
            : new[]
            {
                command.WorkspacePath,
                WorkspacePath,
                selectedAccountWorkspace,
            };

        foreach (var candidate in candidates)
        {
            var path = (candidate ?? "").Trim();
            if (string.IsNullOrWhiteSpace(path))
                continue;
            try
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
                if (Directory.Exists(full))
                {
                    workspace = full;
                    error = "";
                    return true;
                }
            }
            catch
            {
                // ignore invalid path and keep looking, matching Python fallback behavior.
            }
        }

        workspace = "";
        error = "未找到可用工作目录，请先配置账号的上传工作目录，或在命令中指定工作目录。";
        return false;
    }

    private bool TryApplyRemoteAccountSelection(TikTokRemoteCommand command, string workspace, out string error)
    {
        var profileId = (command.AccountProfileId ?? "").Trim();
        var profileName = (command.AccountProfileName ?? "").Trim();
        var selector = command.AccountSelectors?.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))?.Trim() ?? "";
        var selected = "";
        if (!string.IsNullOrWhiteSpace(profileId))
            selected = profileId;
        else if (!string.IsNullOrWhiteSpace(profileName))
            selected = profileName;
        else if (!string.IsNullOrWhiteSpace(selector))
            selected = selector;

        if (string.IsNullOrWhiteSpace(selected))
        {
            error = "";
            return true;
        }

        var account = FindAccount(selected);
        if (account is null)
        {
            error = "未找到远程命令指定的 TikTok 账号。";
            return false;
        }

        SelectedAccount = account;
        error = "";
        return true;
    }

    private void ActivateRemoteWorkspace(string workspace)
    {
        var full = Path.GetFullPath(workspace);
        WorkspacePath = full;
        RefreshWorkspaceProjects(full);
        SystemSettings.UpdateWorkspacePath(full);
        ArchivedProjects.SetWorkspace(full);
    }

    private async Task WaitForWorkspaceQueueStatePersistAsync(string workspace)
    {
        var waitingLogged = false;
        while (!await Task.Run(() =>
                       _queueStatePersist.Flush(workspace, TimeSpan.FromSeconds(3)))
                   .ConfigureAwait(true))
        {
            if (!waitingLogged)
            {
                AppendLog($"仍在等待队列终态落盘：{workspace}");
                waitingLogged = true;
            }

            await Task.Delay(50).ConfigureAwait(true);
        }
    }

    private bool IsWorkspaceQueueRunLifecycleActive(string workspace) => _queueExecution.IsBusy(workspace);

    private Task<bool> WaitForWorkspaceQueueToFinishClosingAsync(string workspace, TimeSpan timeout) =>
        _queueExecution.WaitForIdleAsync(workspace, timeout);
    private string BuildRemoteRuntimeStatusText()
    {
        var checkedCount = _queueItems.Count(item => item.Enabled);
        var active = _queueItems.FirstOrDefault(item =>
            string.Equals(item.StatusText, QueueStepStatus.Running, StringComparison.Ordinal) ||
            string.Equals(item.StatusText, QueueStepStatus.WaitingUploadSlot, StringComparison.Ordinal));
        var lines = new List<string>
        {
            IsQueueRunning ? "TikTok 队列运行中" : "当前没有运行中的 TikTok 队列",
            "",
            $"工作目录：{(string.IsNullOrWhiteSpace(WorkspacePath) ? "未选择" : WorkspacePath)}",
            $"项目总数：{_queueItems.Count}",
            $"当前勾选：{checkedCount}",
        };
        if (SelectedAccount is not null)
            lines.Add($"当前账号：{SelectedAccount.DisplayName}");
        if (active is not null)
        {
            lines.Add($"当前项目：{active.Title}");
            if (!string.IsNullOrWhiteSpace(active.CurrentStep))
                lines.Add($"当前步骤：{QueueStepRegistry.LabelOf(active.CurrentStep)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public void BeginQueueRun()
    {
        _activeQueueRunCount++;
        _queueRunActive = true;
        _dailyLimitNotified = false;
        RefreshRunningWorkspacesSummary();
    }

    public void EndQueueRun()
    {
        _activeQueueRunCount = Math.Max(0, _activeQueueRunCount - 1);
        if (_activeQueueRunCount == 0)
            ScheduleFinalQueueExcelExport();
        RefreshRunningWorkspacesSummary();
    }
    public void HandleQueueWorkerProgress(QueueWorkerProgress progress)
    {
        // QueueUiProgressSink delivers the batched progress on the UI thread.
        RefreshRunningWorkspacesSummary();
        NotifyDailyLimitIfPresent(progress);
        var executionContext = GetWorkspaceQueueExecutionContext(progress.WorkspaceRoot);
        var isActiveWorkspace = IsActiveWorkspace(progress.WorkspaceRoot);
        if (!isActiveWorkspace)
        {
            // 非当前账号的队列进度也写入全局日志（带项目名前缀），切换账号后可直接查看。
            // 进度类消息限频后再写日志与执行历史，避免双队列并行时刷爆 UI 与 SQLite。
            if (ShouldAppendProgressLog(progress))
            {
                var inactiveProject = progress.Item?.Title ?? progress.Item?.DisplayName ?? "";
                var inactivePrefix = string.IsNullOrWhiteSpace(inactiveProject) ? "" : $"[{inactiveProject}] ";
                AppendLog($"{inactivePrefix}{progress.Message}");

                var batchId = executionContext?.BatchId ?? "";
                _ = Task.Run(() => TikTokExecutionHistoryService.AppendEvent(
                    "queue_progress",
                    progress.Item?.StatusText ?? "info",
                    progress.WorkspaceRoot,
                    progress.Item,
                    progress.StepKey ?? "",
                    progress.Message,
                    progress.Item?.LastError ?? "",
                    batchId,
                    metadata: new Dictionary<string, object?>
                    {
                        ["upload_entry_mode"] = executionContext?.UploadEntryMode ?? "",
                    },
                    account: executionContext?.Account));
            }

            return;
        }

        if (progress.Item is not null)
        {
            if (progress.Item.Archived)
                ApplyArchivedQueueProjects([progress.Item.ProjectDir]);
            else
                RefreshQueueRowFor(progress.Item);
        }

        UpdateStatusMessageFromQueueProgress(progress);

        if (!ShouldAppendProgressLog(progress))
            return;

        var project = progress.Item?.Title ?? progress.Item?.DisplayName ?? "";
        var prefix = string.IsNullOrWhiteSpace(project) ? "" : $"[{project}] ";
        AppendLog($"{prefix}{progress.Message}");
        RefreshLogSnapshot();

        var activeBatchId = executionContext?.BatchId ?? "";
        var account = executionContext?.Account;
        _ = Task.Run(() => TikTokExecutionHistoryService.AppendEvent(
            "queue_progress",
            progress.Item?.StatusText ?? "info",
            progress.WorkspaceRoot,
            progress.Item,
            progress.StepKey ?? "",
            progress.Message,
            progress.Item?.LastError ?? "",
            activeBatchId,
            metadata: new Dictionary<string, object?>
            {
                ["upload_entry_mode"] = executionContext?.UploadEntryMode ?? "",
            },
            account: account));
    }

    private readonly Dictionary<string, string> _normalizedPathCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>进度回调每条都要比较工作目录；缓存 GetFullPath 结果避免高频文件系统路径规范化。</summary>
    private string NormalizePathCached(string path)
    {
        if (_normalizedPathCache.TryGetValue(path, out var cached))
            return cached;

        var normalized = SafeFullPath(path);
        if (_normalizedPathCache.Count > 512)
            _normalizedPathCache.Clear();
        _normalizedPathCache[path] = normalized;
        return normalized;
    }

    private bool IsActiveWorkspace(string? workspaceRoot)
    {
        var root = WorkspacePath.Trim();
        // 当前账号没有工作目录时，不能把其他账号正在运行的队列当成当前表格。
        if (string.IsNullOrEmpty(root) || string.IsNullOrWhiteSpace(workspaceRoot))
            return false;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(NormalizePathCached(workspaceRoot)),
            Path.TrimEndingDirectorySeparator(NormalizePathCached(root)),
            StringComparison.OrdinalIgnoreCase);
    }

    private void NotifyDailyLimitIfPresent(QueueWorkerProgress progress)
    {
        var text = progress.Message ?? "";
        if (!text.Contains("单日创建剧集上限", StringComparison.Ordinal) ||
            !text.Contains("已停止", StringComparison.Ordinal))
            return;

        var accountId = progress.Item?.AccountProfileId?.Trim() ?? "";
        if (accountId.Length > 0)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (!_dailyLimitReportedAccounts.TryGetValue(accountId, out var reportedDay) || reportedDay != today)
            {
                _dailyLimitReportedAccounts[accountId] = today;
                _xingeRemoteCommandService.ReportDailySeriesLimit(accountId);
            }
        }

        if (_dailyLimitNotified)
            return;

        _dailyLimitNotified = true;
        StatusMessage = "已达单日创建剧集上限，队列已停止";
        DailyLimitReached?.Invoke(text);
    }

    private readonly Dictionary<string, DateTime> _lastProgressLogTimeByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ProgressLogMinInterval = TimeSpan.FromMilliseconds(600);

    private bool ShouldAppendProgressLog(QueueWorkerProgress progress)
    {
        var message = progress.Message ?? "";
        if (QueueStepLogFilters.RequiresLosslessUiDelivery(progress.StepKey, message)
            || message.Contains("失败", StringComparison.Ordinal)
            || message.Contains("异常", StringComparison.Ordinal)
            || message.Contains("错误", StringComparison.Ordinal)
            || message.Contains("超时", StringComparison.Ordinal)
            || message.Contains("未通过", StringComparison.Ordinal)
            || message.Contains("标题校验", StringComparison.Ordinal)
            || message.Contains("残留文字", StringComparison.Ordinal)
            || message.Contains("海报布局", StringComparison.Ordinal)
            || message.Contains("完成", StringComparison.Ordinal)
            || message.StartsWith("开始", StringComparison.Ordinal)
            || message.Contains('⚠')
            || message.Contains("队列", StringComparison.Ordinal))
        {
            return true;
        }

        var key = $"{progress.WorkspaceRoot}|{progress.Item?.ProjectDir}|{progress.StepKey}";
        if (_lastProgressMessageByKey.TryGetValue(key, out var last) && string.Equals(last, message, StringComparison.Ordinal))
            return false;

        // 下载/上传进度类消息每条内容都不同（百分比/速度变化），按 key 限频，
        // 双队列并行时避免日志与执行历史写入压垮 UI 线程和后台线程池。
        var now = DateTime.UtcNow;
        if (_lastProgressLogTimeByKey.TryGetValue(key, out var lastTime) &&
            now - lastTime < ProgressLogMinInterval)
        {
            return false;
        }

        _lastProgressLogTimeByKey[key] = now;
        _lastProgressMessageByKey[key] = message;
        return true;
    }

    private void UpdateStatusMessageFromQueueProgress(QueueWorkerProgress progress)
    {
        if (IsUploadSeriesProgress(progress))
        {
            _uploadStatusPriorityActive = true;
            _lastUploadStatusUtc = DateTime.UtcNow;
            StatusMessage = progress.Message;
            return;
        }

        if (progress.Item is null)
        {
            _uploadStatusPriorityActive = false;
            StatusMessage = progress.Message;
            OtherRunningStatusMessage = "";
            return;
        }

        OtherRunningStatusMessage = progress.Message;
        if (_uploadStatusPriorityActive &&
            (HasRunningUploadInCurrentWorkspace() || DateTime.UtcNow - _lastUploadStatusUtc < UploadStatusPriorityGrace))
        {
            return;
        }

        _uploadStatusPriorityActive = false;
    }

    private bool HasRunningUploadInCurrentWorkspace() =>
        _queueItems.Any(item =>
            string.Equals(item.CurrentStep, QueueStepRegistry.UploadSeries, StringComparison.Ordinal) ||
            string.Equals(
                item.StepStates.GetValueOrDefault(QueueStepRegistry.UploadSeries),
                QueueStepStatus.Running,
                StringComparison.Ordinal));

    private static bool IsUploadSeriesProgress(QueueWorkerProgress progress) =>
        string.Equals(progress.StepKey, QueueStepRegistry.UploadSeries, StringComparison.Ordinal);

    /// <summary>
    /// 队列 worker 每次 mutate 都会回调持久化；并行多账号时若每条都在 UI 线程执行
    /// <see cref="ApplyPersistedQueueItems(string, IReadOnlyList{QueueProjectItem})"/>
    /// （深拷贝全部项目 + 缓存快照 + 入队持久化），会打满 UI 线程，切账号时表现为卡顿。
    /// 这里按工作目录合并为最多每 200ms 应用一次最新快照。可从任意线程调用。
    /// </summary>
    public void EnqueuePersistedQueueItems(string workspaceRoot, IReadOnlyList<QueueProjectItem> items)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            QueuePersistedItemsOnUiThread(workspaceRoot, items);
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(() => QueuePersistedItemsOnUiThread(workspaceRoot, items));
    }

    private void QueuePersistedItemsOnUiThread(string workspaceRoot, IReadOnlyList<QueueProjectItem> items)
    {
        var root = (workspaceRoot ?? "").Trim();
        if (string.IsNullOrEmpty(root)) return;

        bool schedule;
        lock (_persistCoalesceLock)
        {
            _pendingPersistByRoot[NormalizeWorkspaceRootKey(root)] = (root, items);
            schedule = !_persistFlushScheduled;
            if (schedule) _persistFlushScheduled = true;
        }

        if (schedule)
            Avalonia.Threading.DispatcherTimer.RunOnce(FlushPendingPersistedQueueItems, PersistCoalesceInterval);
    }

    private void FlushPendingPersistedQueueItems()
    {
        List<(string Root, IReadOnlyList<QueueProjectItem> Items)> batch;
        lock (_persistCoalesceLock)
        {
            _persistFlushScheduled = false;
            if (_pendingPersistByRoot.Count == 0) return;
            batch = _pendingPersistByRoot.Values.ToList();
            _pendingPersistByRoot.Clear();
        }

        foreach (var (root, items) in batch)
            ApplyPersistedQueueItems(root, items);
    }

    public void ApplyPersistedQueueItems(IReadOnlyList<QueueProjectItem> items) =>
        ApplyPersistedQueueItems(WorkspacePath, items);

    public void ApplyPersistedQueueItems(string workspaceRoot, IReadOnlyList<QueueProjectItem> items)
    {
        var root = (workspaceRoot ?? "").Trim();
        if (string.IsNullOrEmpty(root)) return;

        var visible = new List<QueueProjectItem>(items.Count);
        var droppedArchived = false;
        var deletedDropped = false;
        foreach (var item in items)
        {
            if (item.Archived)
            {
                droppedArchived = true;
                continue;
            }

            if (!WorkspaceQueueService.IsProjectInsideWorkspace(root, item.ProjectDir))
                continue;
            if (WorkspaceQueueService.IsDeletedProjectDir(root, item.ProjectDir))
            {
                deletedDropped = true;
                continue;
            }
            visible.Add(item);
        }
        if (visible.Count == 0 && items.Count > 0 && !droppedArchived && !deletedDropped)
            return;

        CacheWorkspaceQueueSnapshot(root, visible);
        _queueStatePersist.Enqueue(root, visible, ResolveQueueOptionsForPersistedWorkspace(root));

        if (!IsActiveWorkspace(root))
            return;

        _queueItems = visible;
        ScheduleQueueRowRefresh();
    }

    private QueueRunOptions ResolveQueueOptionsForPersistedWorkspace(string workspaceRoot)
    {
        var root = NormalizeWorkspaceRootKey(workspaceRoot);
        var displayedRoot = NormalizeWorkspaceRootKey(WorkspacePath);
        if (string.Equals(root, displayedRoot, StringComparison.OrdinalIgnoreCase))
            return ClonePersistentQueueRunOptions(_queueRunOptions);

        lock (_workspaceQueueSnapshotsLock)
        {
            if (_workspaceQueueSnapshots.TryGetValue(root, out var snapshot) && snapshot.Options is not null)
                return ClonePersistentQueueRunOptions(snapshot.Options);
        }

        return ClonePersistentQueueRunOptions(WorkspaceQueueService.LoadRunOptions(root));
    }

    private bool _queueRowRefreshPending;
    private DateTime _lastQueueRowRefreshUtc = DateTime.MinValue;

    /// <summary>队列运行时持久化回调非常频繁；合并为最多每 500ms 一次全量行刷新，避免 UI 线程被打满。</summary>
    private void ScheduleQueueRowRefresh()
    {
        if (_queueRowRefreshPending)
            return;

        var elapsed = DateTime.UtcNow - _lastQueueRowRefreshUtc;
        if (elapsed >= TimeSpan.FromMilliseconds(500))
        {
            _lastQueueRowRefreshUtc = DateTime.UtcNow;
            RefreshQueueRowViewModels();
            return;
        }

        _queueRowRefreshPending = true;
        var delay = TimeSpan.FromMilliseconds(500) - elapsed;
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            _queueRowRefreshPending = false;
            _lastQueueRowRefreshUtc = DateTime.UtcNow;
            RefreshQueueRowViewModels();
        }, delay);
    }

    private void PersistQueueItems(IReadOnlyList<QueueProjectItem> items, bool refreshRows = true)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root) || !CanWriteDisplayedQueue(root))
            return;
        var visible = new List<QueueProjectItem>(items.Count);
        foreach (var item in items)
        {
            if (WorkspaceQueueService.IsProjectInsideWorkspace(root, item.ProjectDir))
                visible.Add(item);
        }
        if (visible.Count == 0 && items.Count > 0)
            return;
        _queueItems = visible;
        _queueStatePersist.Enqueue(root, _queueItems, _queueRunOptions);
        if (refreshRows)
            RefreshQueueRowViewModels();
    }

    /// <summary>
    /// 工作目录刚切过去、列表还是上一个账号的时候，不能把那份列表写进新的队列库。
    /// </summary>
    private bool CanWriteDisplayedQueue(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(_displayedWorkspaceRoot))
            return true;
        return string.Equals(
            SafeFullPath(workspaceRoot),
            SafeFullPath(_displayedWorkspaceRoot),
            StringComparison.OrdinalIgnoreCase);
    }

    private void PersistQueueItems() => PersistQueueItems(_queueItems);

    private void OnQueueRowEnabledChangedByUser(QueueProjectRowViewModel row)
    {
        PersistQueueItems(_queueItems, refreshRows: false);
        UpdateQueueSummaryText(refreshTodayUploadCount: false);
        StatusMessage = row.IsEnabled
            ? $"已勾选「{row.NewTitle}」"
            : $"已取消勾选「{row.NewTitle}」";
    }

    private void OnQueueRowRemarkChangedByUser(QueueProjectRowViewModel row)
    {
        PersistQueueItems(_queueItems, refreshRows: false);
        StatusMessage = string.IsNullOrWhiteSpace(row.Remark)
            ? $"已清空「{row.NewTitle}」备注"
            : $"已保存「{row.NewTitle}」备注";
    }

    public void MarkManualRoleMaterialChanged(QueueProjectItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.StepStates[QueueStepRegistry.GenerateRoleVector] = QueueStepStatus.Pending;
        item.StepStates[QueueStepRegistry.GenerateProofMaterial] = QueueStepStatus.Pending;
        item.StepStates[QueueStepRegistry.GenerateTimestampCertificate] = QueueStepStatus.Pending;
        item.LastError = string.Empty;
        if (string.Equals(item.CurrentStep, QueueStepRegistry.GenerateRoleVector, StringComparison.Ordinal))
            item.CurrentStep = string.Empty;
        PersistQueueItems();
    }

    public async Task<QueueProjectTitleRenameResult> RenameQueueProjectNewTitleAsync(
        QueueProjectRowViewModel row,
        string newTitle)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("请先在「账号管理 → 发布配置」设置上传工作目录。");
        if (row is null || string.IsNullOrWhiteSpace(row.Item.ProjectDir))
            throw new InvalidOperationException("请先选择要修改的项目。");
        if (IsCurrentWorkspaceQueueRunning())
            throw new InvalidOperationException("当前工作目录队列正在运行，请停止后再修改新剧名。");

        var projectDir = row.Item.ProjectDir;
        var result = await Task.Run(() =>
            QueueProjectTitleRenameService.RenameNewTitle(root, projectDir, newTitle)).ConfigureAwait(true);

        var scanResult = await Task.Run(() =>
            (
                Items: WorkspaceQueueService.ScanProjects(root).ToList(),
                Options: WorkspaceQueueService.LoadRunOptions(root))).ConfigureAwait(true);
        ApplyWorkspaceScanResult(root, scanResult.Items, scanResult.Options);
        AutoExportQueueExcel();

        StatusMessage = BuildRenameNewTitleStatus(result);
        AppendLog(StatusMessage);
        return result;
    }

    private static string BuildRenameNewTitleStatus(QueueProjectTitleRenameResult result)
    {
        var resets = new List<string>();
        if (result.ResetPoster) resets.Add("海报");
        if (result.ResetProofMaterial) resets.Add("证明材料");
        if (result.ResetMaterialValidate) resets.Add("素材校验");
        if (result.ResetUpload) resets.Add("上传");

        var suffix = resets.Count == 0
            ? ""
            : $"，已将{string.Join("、", resets)}步骤重置为待执行";
        return $"已修改新剧名：{result.OldTitle} -> {result.NewTitle}{suffix}";
    }

    private void RefreshQueueRowFor(QueueProjectItem item)
    {
        var normalized = NormalizeProjectDir(item.ProjectDir);
        if (_queueRowByDir.TryGetValue(normalized, out var row))
            row.RefreshFrom(item);
    }

    private static string NormalizeProjectDir(string projectDir)
    {
        try { return Path.GetFullPath(projectDir); }
        catch { return projectDir.Trim(); }
    }

    private void RefreshQueueRowViewModels()
    {
        ReconcileQueueProjectRows(_queueItems);
        ApplyQueueProjectFilter();
        RefreshTodayUploadCount();
    }

    private void PersistQueueRunOptions()
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root) || !CanWriteDisplayedQueue(root))
            return;
        _queueStatePersist.Enqueue(root, _queueItems, ClonePersistentQueueRunOptions(_queueRunOptions));
    }

    private void ApplyQueueStepTogglesFromOptions()
    {
        _applyingQueueStepToggles = true;
        try
        {
            QueueDownloadEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.Download);
            QueueVideoDedupEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.VideoDedup);
            QueueRewriteEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.RewriteInfo);
            QueueGeneratePosterEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.GeneratePoster);
            QueueGenerateEpisodeScriptEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.GenerateEpisodeScript);
            QueueGenerateAiScriptOutlineEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.GenerateAiScriptOutline);
            QueueGenerateAiDramaMaterialsEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.GenerateAiDramaMaterials);
            QueueGenerateRoleVectorEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.GenerateRoleVector);
            QueueGenerateProofMaterialEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.GenerateProofMaterial);
            QueueGenerateTimestampCertificateEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.GenerateTimestampCertificate);
            QueueSmallVideoRepairEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.SmallVideoRepair);
            QueueVideoTranslateEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.VideoTranslate);
            QueueMaterialValidateEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.MaterialValidate);
            QueueUploadEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.UploadSeries);
        }
        finally
        {
            _applyingQueueStepToggles = false;
        }
    }

    private void SyncEnabledStepsFromUi()
    {
        var steps = new List<string>();
        if (QueueDownloadEnabled) steps.Add(QueueStepRegistry.Download);
        if (QueueVideoDedupEnabled) steps.Add(QueueStepRegistry.VideoDedup);
        if (QueueRewriteEnabled) steps.Add(QueueStepRegistry.RewriteInfo);
        if (QueueGeneratePosterEnabled) steps.Add(QueueStepRegistry.GeneratePoster);
        if (QueueGenerateEpisodeScriptEnabled) steps.Add(QueueStepRegistry.GenerateEpisodeScript);
        if (QueueGenerateAiScriptOutlineEnabled) steps.Add(QueueStepRegistry.GenerateAiScriptOutline);
        if (QueueGenerateAiDramaMaterialsEnabled) steps.Add(QueueStepRegistry.GenerateAiDramaMaterials);
        if (QueueGenerateRoleVectorEnabled) steps.Add(QueueStepRegistry.GenerateRoleVector);
        if (QueueGenerateProofMaterialEnabled) steps.Add(QueueStepRegistry.GenerateProofMaterial);
        if (QueueGenerateTimestampCertificateEnabled) steps.Add(QueueStepRegistry.GenerateTimestampCertificate);
        if (QueueSmallVideoRepairEnabled) steps.Add(QueueStepRegistry.SmallVideoRepair);
        if (QueueVideoTranslateEnabled) steps.Add(QueueStepRegistry.VideoTranslate);
        if (QueueMaterialValidateEnabled) steps.Add(QueueStepRegistry.MaterialValidate);
        if (QueueUploadEnabled) steps.Add(QueueStepRegistry.UploadSeries);
        _queueRunOptions.EnabledSteps = QueueStepRegistry.OrderEnabledSteps(steps).ToList();
        _queueRunOptions.ForceRerunCompletedSteps = ForceRerunCompletedSteps;
        _queueRunOptions.AutoArchiveAfterUpload = AutoArchiveAfterUpload;
        _queueRunOptions.PreferUploadWhenReady = PreferUploadWhenReady;
        _queueRunOptions.SyncManagementAfterUpload = SyncManagementAfterUpload;
        var concurrency = SelectedAccount?.Model.TiktokProjectConcurrency ?? _queueRunOptions.ProjectConcurrency;
        _queueRunOptions.ProjectConcurrency = Math.Clamp(concurrency < 1 ? 4 : concurrency, 1, 20);
        _queueRunOptions.UploadEntryMode = "";
    }

    private QueueRunOptions QueueOptionsFromLocalChecks(TikTokAccountProfile? account, string workspace, QueueRunOptions serverOptions)
    {
        QueueRunOptions local;
        if (account is null || string.Equals(SelectedAccount?.Model.Id, account.Id, StringComparison.Ordinal))
        {
            SyncEnabledStepsFromUi();
            local = _queueRunOptions.Clone();
        }
        else
        {
            local = LoadQueueRunOptionsForAccountWorkspace(workspace, account);
            local.ForceRerunCompletedSteps = serverOptions.ForceRerunCompletedSteps;
        }

        local.ProjectConcurrency = Math.Clamp(account?.TiktokProjectConcurrency ?? local.ProjectConcurrency, 1, 20);
        return local;
    }

    // 自动派单和手点「执行勾选队列」用的是界面勾选。派单到达后先把服务器步骤写到勾选上，再启动。
    private void ApplyAutoPublishServerSteps(TikTokAccountProfile? account, string workspace, QueueRunOptions options)
    {
        ApplyServerConfiguredQueueSteps(account, options);
        if (!IsActiveWorkspace(workspace))
            return;
        var steps = NormalizeQueueEnabledSteps(options.EnabledSteps);
        if (steps.Count == 0)
            return;
        _queueRunOptions.EnabledSteps = steps;
        _queueRunOptions.AutoArchiveAfterUpload = options.AutoArchiveAfterUpload;
        _queueRunOptions.ForceRerunCompletedSteps = options.ForceRerunCompletedSteps;
        _queueRunOptions.PreferUploadWhenReady = options.PreferUploadWhenReady;
        _queueRunOptions.SyncManagementAfterUpload = options.SyncManagementAfterUpload;
        ApplyQueueStepTogglesFromOptions();
        ForceRerunCompletedSteps = options.ForceRerunCompletedSteps;
        AutoArchiveAfterUpload = options.AutoArchiveAfterUpload;
        PreferUploadWhenReady = options.PreferUploadWhenReady;
        SyncManagementAfterUpload = options.SyncManagementAfterUpload;
        PersistQueueRunOptions();
    }

    private void ApplyServerConfiguredQueueSteps(TikTokAccountProfile? account, QueueRunOptions options)
    {
        var steps = NormalizeQueueEnabledSteps(options.EnabledSteps);
        if (steps.Count == 0)
            return;
        options.EnabledSteps = steps.ToList();
        if (account is not null)
        {
            account.TiktokQueueEnabledSteps = steps.ToList();
            account.TiktokQueueAutoArchiveAfterUpload = options.AutoArchiveAfterUpload;
            account.TiktokQueuePreferUploadWhenReady = options.PreferUploadWhenReady;
            account.TiktokQueueSyncManagementAfterUpload = options.SyncManagementAfterUpload;
            _context.NotifyProfileUpdated(account);
        }

        var appliesToDisplayedAccount = account is null || SelectedAccount?.Model.Id == account.Id;
        if (!appliesToDisplayedAccount)
            return;

        _queueRunOptions.EnabledSteps = steps.ToList();
        _queueRunOptions.AutoArchiveAfterUpload = options.AutoArchiveAfterUpload;
        _queueRunOptions.ForceRerunCompletedSteps = options.ForceRerunCompletedSteps;
        _queueRunOptions.PreferUploadWhenReady = options.PreferUploadWhenReady;
        _queueRunOptions.SyncManagementAfterUpload = options.SyncManagementAfterUpload;
        ApplyQueueStepTogglesFromOptions();
        ForceRerunCompletedSteps = options.ForceRerunCompletedSteps;
        AutoArchiveAfterUpload = options.AutoArchiveAfterUpload;
        PreferUploadWhenReady = options.PreferUploadWhenReady;
        SyncManagementAfterUpload = options.SyncManagementAfterUpload;
    }

    private void ApplyAccountQueueEnabledSteps(string workspaceRoot, QueueRunOptions? preloadedOptions = null)
    {
        _queueRunOptions = LoadQueueRunOptionsForAccountWorkspace(workspaceRoot, SelectedAccount?.Model, preloadedOptions);
    }

    private QueueRunOptions LoadQueueRunOptionsForAccountWorkspace(
        string workspaceRoot,
        TikTokAccountProfile? account,
        QueueRunOptions? preloadedOptions = null)
    {
        var options = preloadedOptions ?? WorkspaceQueueService.LoadRunOptions(workspaceRoot);
        var hasAccountSteps = account?.TiktokQueueEnabledSteps is not null;
        var enabledSteps = hasAccountSteps
            ? NormalizeQueueEnabledSteps(account?.TiktokQueueEnabledSteps)
            : new List<string>();
        var changedAccountSettings = false;
        if (hasAccountSteps && account is not null &&
            !account.TiktokQueueEnabledSteps!.SequenceEqual(enabledSteps, StringComparer.Ordinal))
        {
            account.TiktokQueueEnabledSteps = enabledSteps.ToList();
            changedAccountSettings = true;
        }
        if (!hasAccountSteps)
        {
            enabledSteps = NormalizeQueueEnabledSteps(options.EnabledSteps);
            if (account is not null)
            {
                account.TiktokQueueEnabledSteps = enabledSteps.ToList();
                changedAccountSettings = true;
            }
        }

        options.EnabledSteps = enabledSteps.ToList();
        if (account is not null)
        {
            if (!account.TiktokQueueAutoArchiveAfterUpload.HasValue)
            {
                account.TiktokQueueAutoArchiveAfterUpload = options.AutoArchiveAfterUpload;
                changedAccountSettings = true;
            }
            if (!account.TiktokQueuePreferUploadWhenReady.HasValue)
            {
                account.TiktokQueuePreferUploadWhenReady = options.PreferUploadWhenReady;
                changedAccountSettings = true;
            }
            if (!account.TiktokQueueSyncManagementAfterUpload.HasValue)
            {
                account.TiktokQueueSyncManagementAfterUpload = options.SyncManagementAfterUpload;
                changedAccountSettings = true;
            }

            options.AutoArchiveAfterUpload = account.TiktokQueueAutoArchiveAfterUpload.GetValueOrDefault();
            options.PreferUploadWhenReady = account.TiktokQueuePreferUploadWhenReady.GetValueOrDefault();
            options.SyncManagementAfterUpload = account.TiktokQueueSyncManagementAfterUpload.GetValueOrDefault();

            if (changedAccountSettings)
                _context.NotifyProfileUpdated(account);
        }

        var concurrency = account?.TiktokProjectConcurrency ?? options.ProjectConcurrency;
        options.ProjectConcurrency = Math.Clamp(concurrency < 1 ? 4 : concurrency, 1, 20);
        options.UploadEntryMode = "";
        return options;
    }

    private void PersistAccountQueueSettings()
    {
        var account = SelectedAccount?.Model;
        if (account is null) return;

        var enabledSteps = NormalizeQueueEnabledSteps(_queueRunOptions.EnabledSteps);
        var changed = false;

        if (!NormalizeQueueEnabledSteps(account.TiktokQueueEnabledSteps).SequenceEqual(enabledSteps))
        {
            account.TiktokQueueEnabledSteps = enabledSteps.ToList();
            changed = true;
        }

        if (account.TiktokQueueAutoArchiveAfterUpload != _queueRunOptions.AutoArchiveAfterUpload)
        {
            account.TiktokQueueAutoArchiveAfterUpload = _queueRunOptions.AutoArchiveAfterUpload;
            changed = true;
        }
        if (account.TiktokQueuePreferUploadWhenReady != _queueRunOptions.PreferUploadWhenReady)
        {
            account.TiktokQueuePreferUploadWhenReady = _queueRunOptions.PreferUploadWhenReady;
            changed = true;
        }
        if (account.TiktokQueueSyncManagementAfterUpload != _queueRunOptions.SyncManagementAfterUpload)
        {
            account.TiktokQueueSyncManagementAfterUpload = _queueRunOptions.SyncManagementAfterUpload;
            changed = true;
        }

        if (changed)
            _context.NotifyProfileUpdated(account);
    }

    private static List<string> NormalizeQueueEnabledSteps(IEnumerable<string>? steps)
    {
        if (steps is null) return new List<string>();

        var known = QueueStepRegistry.UserSelectable.Select(step => step.Key).ToHashSet(StringComparer.Ordinal);
        return QueueStepRegistry.OrderUserSelectableSteps(
                steps.Select(step => (step ?? "").Trim())
                    .Where(step => known.Contains(step))
                    .Distinct(StringComparer.Ordinal))
            .ToList();
    }

    private void RefreshWorkspaceFromActiveAccount()
    {
        var workspace = SelectedAccount?.Model.ResolveWorkspacePath() ?? "";
        WorkspacePath = workspace;
        RefreshWorkspaceProjects(workspace);

        SystemSettings.UpdateWorkspacePath(WorkspacePath);
        ArchivedProjects.SetWorkspace(WorkspacePath, refresh: false);
    }

    private string ResolveSelectedAccountWorkspacePath()
    {
        var account = SelectedAccount?.Model;
        if (account is not null)
        {
            foreach (var candidate in new[] { account.TiktokUploadProfilePath, account.LastWorkspace })
            {
                var workspace = NormalizeWorkspacePath(candidate);
                if (!string.IsNullOrWhiteSpace(workspace))
                    return workspace;
            }
        }

        return NormalizeWorkspacePath(WorkspacePath);
    }

    private TikTokQueueImportTarget? CaptureSelectedAccountQueueImportTarget()
    {
        var target = ResolveSelectedAccountQueueImportTarget();
        if (target is null)
        {
            StatusMessage = "请先为左侧选择账号配置有效的 TikTok 上传工作目录";
            return null;
        }

        if (!ValidateTikTokQueueImportTarget(target, out var error))
        {
            StatusMessage = error;
            AppendLog(error);
            return null;
        }
        return target;
    }

    private Func<IReadOnlyList<TikTokQueueImportTarget>>? _kuaishouDownloadTargets;
    private Func<TikTokQueueImportRequest, Task>? _importKuaishouDownloads;

    public void BindKuaishouDownloadQueue(Func<TikTokQueueImportTarget?> selectedTarget,
        Func<IReadOnlyList<TikTokQueueImportTarget>> targets, Func<TikTokQueueImportRequest, Task> import)
    {
        _kuaishouDownloadTargets = targets;
        _importKuaishouDownloads = import;
        DramaDownload.ConfigurePlatformQueues([(DramaQueuePlatforms.TikTok, "TikTok"), (DramaQueuePlatforms.Kuaishou, "快手")]);
        DramaDownload.PlatformQueueTargetRequested += key => key switch
        {
            DramaQueuePlatforms.TikTok => ResolveSelectedAccountQueueImportTarget(),
            DramaQueuePlatforms.Kuaishou => selectedTarget(),
            _ => null,
        };
    }

    private IReadOnlyList<TikTokQueueImportTarget> ListTikTokQueueImportTargets()
    {
        var targets = new List<TikTokQueueImportTarget>();
        foreach (var account in Accounts)
        {
            var configured = NormalizeWorkspacePath(account.Model.TiktokUploadProfilePath);
            var lastWorkspace = NormalizeWorkspacePath(account.Model.LastWorkspace);
            var workspace = string.IsNullOrWhiteSpace(configured) ? lastWorkspace : configured;
            targets.Add(new TikTokQueueImportTarget(account.Id, account.DisplayName, workspace,
                _kuaishouDownloadTargets is null ? "" : DramaQueuePlatforms.TikTok,
                "TikTok") { LoginEmail = account.LoginEmail, FallbackWorkspaceRoot = lastWorkspace });
        }

        if (_kuaishouDownloadTargets is { } kuaishouTargets)
            targets.AddRange(kuaishouTargets());
        return targets;
    }

    private TikTokQueueImportTarget? ResolveSelectedAccountQueueImportTarget()
    {
        var account = SelectedAccount?.Model;
        if (account is null) return null;
        var workspace = NormalizeWorkspacePath(account.ResolveWorkspacePath());
        return string.IsNullOrWhiteSpace(workspace)
            ? null
            : new TikTokQueueImportTarget(account.Id, account.DisplayName, workspace,
                _kuaishouDownloadTargets is null ? "" : DramaQueuePlatforms.TikTok, "TikTok");
    }

    private void UpdateDramaDownloadQueueTarget() =>
        DramaDownload.UpdateTikTokQueueTarget(ResolveSelectedAccountQueueImportTarget());

    private bool ValidateTikTokQueueImportTarget(
        TikTokQueueImportTarget target,
        out string error)
    {
        error = "";
        var account = FindAccountById(target.AccountProfileId)?.Model;
        if (account is null)
        {
            error = $"目标账号已不存在：{target.AccountProfileName} ({target.AccountProfileId})";
            return false;
        }

        var root = NormalizeWorkspacePath(target.WorkspaceRoot);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            error = $"账号「{target.AccountProfileName}」的 TikTok 上传工作目录不存在：{target.WorkspaceRoot}";
            return false;
        }
        var currentRoot = NormalizeWorkspacePath(account.ResolveWorkspacePath());
        if (!string.Equals(Path.TrimEndingDirectorySeparator(root),
                Path.TrimEndingDirectorySeparator(currentRoot), StringComparison.OrdinalIgnoreCase))
        {
            error = $"账号「{target.AccountProfileName}」的工作目录已变更，请重新选取账号";
            return false;
        }

        return true;
    }

    public void ReloadAccountsFromDisk()
    {
        _store.Load();
        ReloadAccounts();
    }

    public void ApplySyncedAccounts(IReadOnlyList<TikTokAccountProfile> accounts)
    {
        var removedDirs = _store.Accounts
            .Where(account => accounts.All(item => item.Id != account.Id))
            .Select(account => account.ProfileDir)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        _store.ReplaceAll(accounts);
        foreach (var directory in removedDirs)
        {
            try
            {
                var full = Path.GetFullPath(directory);
                var root = Path.GetFullPath(AppPaths.ProfilesRoot) + Path.DirectorySeparatorChar;
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
                    Directory.Delete(full, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        ReloadAccounts();
    }

    public void ReloadAccounts()
    {
        var selectedId = SelectedAccount?.Id ?? _store.ActiveAccountId;
        Accounts.Clear();
        foreach (var account in _store.Accounts)
            Accounts.Add(new AccountItemViewModel(account));
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == selectedId)
                          ?? Accounts.FirstOrDefault(a => a.Id == _store.ActiveAccountId)
                          ?? Accounts.FirstOrDefault();
        RefreshFilteredAccounts();
        RefreshWorkspaceFromActiveAccount();
    }

    public void SaveAccountProfile(TikTokAccountProfile profile)
    {
        _context.NotifyProfileUpdated(profile);
        var vm = Accounts.FirstOrDefault(a => a.Id == profile.Id);
        vm?.RefreshFromModel();
        if (SelectedAccount?.Id == profile.Id)
        {
            RefreshWorkspaceFromActiveAccount();
        }
        UpdateDramaDownloadQueueTarget();
        AccountProfileNetworkChanged?.Invoke(profile);
    }

    public async Task ImportDramaProjectsToQueueAsync(TikTokQueueImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Target;
        if (target.PlatformKey == DramaQueuePlatforms.Kuaishou)
        {
            if (_importKuaishouDownloads is null)
                throw new InvalidOperationException("快手上传工作区未就绪。");
            await _importKuaishouDownloads(request);
            return;
        }
        if (!ValidateTikTokQueueImportTarget(target, out var error))
        {
            StatusMessage = error;
            AppendLog(error);
            throw new InvalidOperationException(error);
        }

        var root = NormalizeWorkspacePath(target.WorkspaceRoot);
        StatusMessage = $"正在加入「{target.AccountProfileName}」上传队列…";
        var importResult = await Task.Run(() =>
        {
            if (request.ProjectDirs.Any(path => !WorkspaceProjectScanner.IsValidProjectDirectory(path)))
                throw new InvalidOperationException("入队项目目录已变更，请检查目录后重试。");
            var added = WorkspaceQueueService.AddProjectsToQueue(
                root,
                request.ProjectDirs,
                target.AccountProfileId,
                target.AccountProfileName);
            return added;
        }).ConfigureAwait(true);

        try
        {
            if (request.IsBatch && !string.Equals(SelectedAccount?.Id, target.AccountProfileId, StringComparison.Ordinal))
            {
                lock (_workspaceQueueSnapshotsLock)
                    _workspaceQueueSnapshots.Remove(NormalizeWorkspaceRootKey(root));
            }
            else
            {
                var snapshot = await Task.Run(() => WorkspaceQueueService.LoadPersistedSnapshot(root));
                if (string.Equals(SelectedAccount?.Id, target.AccountProfileId, StringComparison.Ordinal))
                {
                    WorkspacePath = root;
                    SystemSettings.UpdateWorkspacePath(root);
                    ArchivedProjects.SetWorkspace(root, refresh: false);
                    ApplyWorkspaceScanResult(root, snapshot.Items.ToList(), snapshot.Options);
                }
                else
                {
                    CacheWorkspaceQueueSnapshot(root, snapshot.Items, snapshot.Options);
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"「{target.AccountProfileName}」已入队，队列刷新失败：{ex.Message}");
        }

        StatusMessage = importResult.Count > 0
            ? $"已导入 {importResult.Count} 个项目到「{target.AccountProfileName}」上传队列"
            : $"没有可导入「{target.AccountProfileName}」队列的新项目";
        AppendLog(StatusMessage);
        if (request.IsBatch && importResult.Count != request.ProjectDirs.Count)
            throw new QueueImportPartialException(importResult.Select(item => Path.GetFullPath(item.ProjectDir)));
    }

    public async Task<LocalManualDramaImportResult?> ImportLocalManualDramaAsync(string sourceProjectDir)
    {
        var root = ResolveSelectedAccountWorkspacePath();
        if (string.IsNullOrWhiteSpace(root))
        {
            StatusMessage = "请先为左侧选择账号配置上传工作目录";
            return null;
        }

        var account = SelectedAccount?.Model;
        Directory.CreateDirectory(root);

        if (!string.Equals(NormalizeWorkspacePath(WorkspacePath), root, StringComparison.OrdinalIgnoreCase))
        {
            WorkspacePath = root;
            SystemSettings.UpdateWorkspacePath(root);
            ArchivedProjects.SetWorkspace(root, refresh: false);
        }

        StatusMessage = $"正在导入本地剧集：{Path.GetFileName(sourceProjectDir)}";
        AppendLog(StatusMessage);

        LocalManualDramaImportResult result;
        try
        {
            result = await Task.Run(() => LocalManualDramaImportService.Import(
                    root,
                    sourceProjectDir,
                    AppendLog,
                    ClientSettingsStore.Load()))
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入本地剧集失败：{ex.Message}";
            AppendLog(StatusMessage);
            throw;
        }

        var added = WorkspaceQueueService.AddProjectsToQueue(root, [result.SourceProjectDir]);
        var importedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(result.SourceProjectDir),
        };
        ApplyImportedProjectsToCurrentAccount(root, importedKeys, account);

        if (IsCurrentWorkspaceQueueRunning())
        {
            var appended = AppendImportedProjectsWhileRunning(importedKeys);
            if (appended.Count > 0)
            {
                var appendedCount = _queueOrchestrator.TryAppendItemsToRunningWorkspace(root, appended);
                AppendLog(appendedCount > 0
                    ? $"已请求追加 {appendedCount} 个本地剧集到运行中的队列末尾。"
                    : $"已导入 {appended.Count} 个本地剧集到队列列表。");
            }
        }
        else
        {
            await RefreshWorkspaceProjectsAsync(root, force: true).ConfigureAwait(true);
        }

        var accountName = account?.DisplayName ?? "当前账号";
        StatusMessage = added.Count > 0
            ? $"已导入本地剧集「{result.DisplayName}」到「{accountName}」上传队列，共 {result.EpisodeCount} 集"
            : $"本地剧集「{result.DisplayName}」已在「{accountName}」上传队列中，共 {result.EpisodeCount} 集";
        AppendLog(StatusMessage);
        return result;
    }

    public async Task<IReadOnlyList<LocalManualDramaImportPreview>> ListLocalManualDramaCandidatesAsync()
    {
        var root = ResolveSelectedAccountWorkspacePath();
        if (string.IsNullOrWhiteSpace(root))
        {
            StatusMessage = "请先为左侧选择账号配置上传工作目录";
            return Array.Empty<LocalManualDramaImportPreview>();
        }

        return await Task.Run(() => LocalManualDramaImportService.ListCandidates(root))
            .ConfigureAwait(true);
    }

    public async Task<IReadOnlyList<LocalManualDramaImportConflictFinder.Conflict>> FindLocalManualImportConflictsAsync(
        IReadOnlyList<LocalManualDramaImportPreview> selected)
    {
        var root = ResolveSelectedAccountWorkspacePath();
        if (string.IsNullOrWhiteSpace(root) || selected.Count == 0)
            return Array.Empty<LocalManualDramaImportConflictFinder.Conflict>();

        var archiveRoot = SelectedAccount?.Model.ResolveArchiveRootPath(root);
        var (queueItems, archived, archiveError) = await Task.Run(() =>
        {
            var queue = WorkspaceQueueService.ScanProjects(root);
            try
            {
                return (queue, TikTokArchivedProjectService.List(root, archiveRoot), "");
            }
            catch (Exception ex)
            {
                return (queue, (IReadOnlyList<ArchivedProjectItem>)Array.Empty<ArchivedProjectItem>(), ex.Message);
            }
        }).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(archiveError))
            AppendLog($"读取归档项目失败，本次不按归档确认：{archiveError}");

        IReadOnlySet<string>? managementDuplicates = null;
        var settings = ClientSettingsStore.Load();
        if (settings.ManagementDedupEnabled)
        {
            StatusMessage = "正在按管理系统去重检查本地剧集…";
            var check = await TikTokManagementUploadRecordSyncService.CheckDuplicateOriginalNamesAsync(
                selected.Select(item => item.DisplayName),
                settings.ManagementDedupScope,
                SelectedAccount?.Model,
                CancellationToken.None).ConfigureAwait(true);
            if (!check.Ok)
                AppendLog($"管理系统去重检查失败，本次不按管理系统确认：{check.Message}");
            else
                managementDuplicates = check.Duplicates;
        }

        return LocalManualDramaImportConflictFinder.Find(selected, queueItems, archived, managementDuplicates);
    }

    public async Task<LocalManualDramaBatchImportResult> ImportLocalManualDramasAsync(
        IReadOnlyList<string> sourceProjectDirs)
    {
        var requestedDirs = sourceProjectDirs
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var root = ResolveSelectedAccountWorkspacePath();
        if (string.IsNullOrWhiteSpace(root))
        {
            StatusMessage = "请先为左侧选择账号配置上传工作目录";
            return new LocalManualDramaBatchImportResult(requestedDirs.Length, 0, 0, [], ["请先为左侧选择账号配置上传工作目录"]);
        }

        var account = SelectedAccount?.Model;
        Directory.CreateDirectory(root);

        if (!string.Equals(NormalizeWorkspacePath(WorkspacePath), root, StringComparison.OrdinalIgnoreCase))
        {
            WorkspacePath = root;
            SystemSettings.UpdateWorkspacePath(root);
            ArchivedProjects.SetWorkspace(root, refresh: false);
        }

        var existingBefore = WorkspaceQueueService.ScanProjects(root)
            .Select(item => Path.GetFullPath(item.ProjectDir))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overLimitSettings = ClientSettingsStore.Load();
        var results = new List<LocalManualDramaImportResult>();
        var failures = new List<string>();

        for (var index = 0; index < requestedDirs.Length; index++)
        {
            var sourceProjectDir = requestedDirs[index];
            var name = Path.GetFileName(sourceProjectDir);
            StatusMessage = $"正在导入本地剧集（{index + 1}/{requestedDirs.Length}）：{name}";
            AppendLog(StatusMessage);

            try
            {
                var result = await Task.Run(() => LocalManualDramaImportService.Import(
                        root,
                        sourceProjectDir,
                        AppendLog,
                        overLimitSettings))
                    .ConfigureAwait(true);
                results.Add(result);
                AppendLog($"已导入本地剧集：{result.DisplayName}（{result.EpisodeCount} 集）");
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
                AppendLog($"导入本地剧集失败：{name} -> {ex.Message}");
            }
        }

        var importedKeys = results
            .Select(result => Path.GetFullPath(result.SourceProjectDir))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (importedKeys.Count > 0)
        {
            WorkspaceQueueService.AddProjectsToQueue(root, importedKeys);
            ApplyImportedProjectsToCurrentAccount(root, importedKeys, account);

            if (IsCurrentWorkspaceQueueRunning())
            {
                var appended = AppendImportedProjectsWhileRunning(importedKeys);
                if (appended.Count > 0)
                {
                    var appendedCount = _queueOrchestrator.TryAppendItemsToRunningWorkspace(root, appended);
                    AppendLog(appendedCount > 0
                        ? $"已请求追加 {appendedCount} 个本地剧集到运行中的队列末尾。"
                        : $"已导入 {appended.Count} 个本地剧集到队列列表。");
                }
            }
            else
            {
                await RefreshWorkspaceProjectsAsync(root, force: true).ConfigureAwait(true);
            }
        }

        var addedCount = importedKeys.Count(key => !existingBefore.Contains(key));
        var existingCount = Math.Max(0, importedKeys.Count - addedCount);
        var batchResult = new LocalManualDramaBatchImportResult(
            requestedDirs.Length,
            addedCount,
            existingCount,
            results,
            failures);

        StatusMessage = batchResult.SummaryText;
        AppendLog(StatusMessage);
        return batchResult;
    }

    public async Task<LocalUnimportedDuplicateDeletionResult> DeleteUnimportedLocalDuplicatesAsync(
        LocalManualDramaImportConflictFinder.UnimportedLocalDeletionPlan plan)
    {
        var root = ResolveSelectedAccountWorkspacePath();
        if (string.IsNullOrWhiteSpace(root))
        {
            StatusMessage = "请先为左侧选择账号配置上传工作目录";
            var blockedImports = plan.Targets
                .SelectMany(target => target.RelatedImportDirs)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new LocalUnimportedDuplicateDeletionResult(0, blockedImports, ["请先为左侧选择账号配置上传工作目录"]);
        }

        if (plan.Targets.Count == 0)
            return new LocalUnimportedDuplicateDeletionResult(0, [], []);

        var protectedDirs = plan.ProtectedImportDirs;
        var archiveRoot = SelectedAccount?.Model.ResolveArchiveRootPath(root) ?? "";
        var account = SelectedAccount?.Model;
        var queueItems = await Task.Run(() => WorkspaceQueueService.ScanProjects(root)).ConfigureAwait(true);
        var queueByDir = queueItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ProjectDir))
            .GroupBy(item => Path.GetFullPath(item.ProjectDir), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var deleted = 0;
        var failures = new List<string>();
        var failedImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removedQueueDirs = new List<string>();
        foreach (var target in plan.Targets)
        {
            var directory = Path.GetFullPath(target.Directory);
            if (LocalManualDramaImportConflictFinder.IsProtectedImportPath(directory, protectedDirs) ||
                LocalManualDramaImportConflictFinder.IsUnderDirectory(directory, archiveRoot))
            {
                AppendLog($"跳过受保护目录，未删除：{directory}");
                continue;
            }

            queueByDir.TryGetValue(directory, out var queueItem);
            if (queueItem is not null)
            {
                TikTokExecutionHistoryService.AppendEvent(
                    "project_deleted",
                    "deleted",
                    root,
                    queueItem,
                    message: "导入本地剧集前删除未导入的重复项目，已保存版权恢复快照",
                    account: account);
            }

            try
            {
                var removed = await Task.Run(() => DeleteUnimportedLocalDuplicateDirectory(
                        root,
                        directory,
                        queueItem,
                        protectedDirs,
                        archiveRoot))
                    .ConfigureAwait(true);
                deleted += removed.DeletedDirectoryCount;
                if (!string.IsNullOrWhiteSpace(removed.QueueProjectDir))
                    removedQueueDirs.Add(removed.QueueProjectDir);
                AppendLog(removed.DeletedDirectoryCount > 0
                    ? $"已删除本地未导入重复剧集：{directory}"
                    : $"本地未导入重复剧集不在工作目录内，已跳过目录删除：{directory}");
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(directory)}: {ex.Message}");
                foreach (var importDir in target.RelatedImportDirs)
                    failedImports.Add(Path.GetFullPath(importDir));
                AppendLog($"删除本地未导入重复剧集失败：{directory} -> {ex.Message}");
            }
        }

        if (removedQueueDirs.Count > 0)
            WorkspaceQueueService.RemoveProjectsFromQueue(root, removedQueueDirs);
        if (deleted > 0 || removedQueueDirs.Count > 0)
            await RefreshWorkspaceProjectsAsync(root, force: true).ConfigureAwait(true);

        StatusMessage = failures.Count == 0
            ? $"已删除 {deleted} 个本地未导入重复剧集目录"
            : $"删除本地未导入重复剧集：成功 {deleted} 个，失败 {failures.Count} 个";
        AppendLog(StatusMessage);
        return new LocalUnimportedDuplicateDeletionResult(deleted, failedImports.ToArray(), failures);
    }

    private readonly record struct UnimportedLocalDuplicateRemoval(int DeletedDirectoryCount, string? QueueProjectDir);

    private static UnimportedLocalDuplicateRemoval DeleteUnimportedLocalDuplicateDirectory(
        string workspaceRoot,
        string directory,
        QueueProjectItem? queueItem,
        IReadOnlyList<string> protectedDirs,
        string archiveRoot)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (queueItem is not null)
        {
            try
            {
                var context = ProjectWorkspaceService.LoadContext(queueItem.ProjectDir);
                AddUnimportedDeleteTarget(context.SourceProjectDir, workspaceRoot, protectedDirs, archiveRoot, targets);
                AddUnimportedDeleteTarget(context.WorkflowProjectDir, workspaceRoot, protectedDirs, archiveRoot, targets);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
        }

        AddUnimportedDeleteTarget(directory, workspaceRoot, protectedDirs, archiveRoot, targets);
        var deleted = 0;
        if (targets.Count == 0)
        {
            var untouchedQueueDir = queueItem is null || string.IsNullOrWhiteSpace(queueItem.ProjectDir)
                ? null
                : Path.GetFullPath(queueItem.ProjectDir);
            return new UnimportedLocalDuplicateRemoval(0, untouchedQueueDir);
        }

        foreach (var target in targets.OrderByDescending(path => path.Length))
        {
            if (!Directory.Exists(target))
                continue;
            Directory.Delete(target, recursive: true);
            deleted++;
        }

        var queueProjectDir = queueItem is null || string.IsNullOrWhiteSpace(queueItem.ProjectDir)
            ? null
            : Path.GetFullPath(queueItem.ProjectDir);
        var removedQueueDir = queueProjectDir is not null &&
                              (deleted > 0 || !Directory.Exists(queueProjectDir))
            ? queueProjectDir
            : null;
        return new UnimportedLocalDuplicateRemoval(deleted, removedQueueDir);
    }

    private static void AddUnimportedDeleteTarget(
        string? path,
        string workspaceRoot,
        IReadOnlyList<string> protectedDirs,
        string archiveRoot,
        ISet<string> targets)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        var target = Path.GetFullPath(path);
        if (LocalManualDramaImportConflictFinder.IsProtectedImportPath(target, protectedDirs) ||
            LocalManualDramaImportConflictFinder.IsUnderDirectory(target, archiveRoot))
            return;

        var workspace = Path.GetFullPath(workspaceRoot);
        if (string.Equals(target, workspace, StringComparison.OrdinalIgnoreCase) ||
            !IsWithinWorkspace(target, workspace))
            return;

        targets.Add(target);
    }

    private void EnsureDisplayedImportedProjectsChecked(
        string workspaceRoot,
        IReadOnlyList<string> projectDirs,
        TikTokAccountProfile? account)
    {
        if (projectDirs.Count == 0)
            return;
        if (!string.Equals(
                NormalizeWorkspacePath(WorkspacePath),
                NormalizeWorkspacePath(workspaceRoot),
                StringComparison.OrdinalIgnoreCase))
            return;

        var pending = projectDirs
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var item in _queueItems)
        {
            if (string.IsNullOrWhiteSpace(item.ProjectDir))
                continue;
            var key = Path.GetFullPath(item.ProjectDir);
            if (!pending.Remove(key))
                continue;
            if (!item.Enabled)
            {
                item.Enabled = true;
                changed = true;
            }
            if (account is not null && string.IsNullOrWhiteSpace(item.AccountProfileId))
            {
                item.AccountProfileId = account.Id;
                item.AccountProfileName = account.DisplayName;
                changed = true;
            }
        }

        if (pending.Count > 0)
        {
            var scanned = WorkspaceQueueService.ScanProjects(workspaceRoot)
                .ToDictionary(item => Path.GetFullPath(item.ProjectDir), StringComparer.OrdinalIgnoreCase);
            foreach (var key in pending)
            {
                if (!scanned.TryGetValue(key, out var item))
                    continue;
                item.Enabled = true;
                ResetQueueItemToPending(item);
                if (account is not null)
                {
                    item.AccountProfileId = account.Id;
                    item.AccountProfileName = account.DisplayName;
                }
                _queueItems.Add(item);
                changed = true;
            }
        }

        if (changed)
            PersistQueueItems();
        else
            RefreshQueueRowViewModels();
    }

    private void ApplyImportedProjectsToCurrentAccount(
        string workspaceRoot,
        IReadOnlySet<string> importedKeys,
        TikTokAccountProfile? account)
    {
        if (importedKeys.Count == 0)
            return;

        var items = WorkspaceQueueService.ScanProjects(workspaceRoot).ToList();
        var changed = false;
        foreach (var item in items)
        {
            if (!importedKeys.Contains(Path.GetFullPath(item.ProjectDir)))
                continue;

            item.Enabled = true;
            ResetQueueItemToPending(item);
            if (account is not null)
            {
                item.AccountProfileId = account.Id;
                item.AccountProfileName = account.DisplayName;
            }

            changed = true;
        }

        foreach (var item in _queueItems)
        {
            if (!importedKeys.Contains(Path.GetFullPath(item.ProjectDir)))
                continue;

            item.Enabled = true;
            ResetQueueItemToPending(item);
            if (account is not null)
            {
                item.AccountProfileId = account.Id;
                item.AccountProfileName = account.DisplayName;
            }
        }

        if (changed)
            WorkspaceQueueService.SaveRunOptions(workspaceRoot, items, WorkspaceQueueService.LoadRunOptions(workspaceRoot));
    }

    public async Task ArchiveSelectedQueueProjectsAsync(IEnumerable<QueueProjectRowViewModel> selectedRows)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root))
        {
            StatusMessage = "请先在「账号管理 → 发布配置」设置上传工作目录";
            return;
        }

        var rows = selectedRows.ToArray();
        if (rows.Length == 0)
        {
            StatusMessage = "请先选中要归档的项目";
            return;
        }

        if (IsCurrentWorkspaceQueueRunning())
        {
            StatusMessage = "队列运行中，请先等待当前 TikTok 队列停止后再归档项目。";
            AppendLog(StatusMessage);
            return;
        }

        var successCount = 0;
        var failures = new List<string>();
        var archivedProjectDirs = new List<string>();
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var name = string.IsNullOrWhiteSpace(row.Item.Title) ? row.Item.DisplayName : row.Item.Title;
            StatusMessage = $"正在归档（{index + 1}/{rows.Length}）：{name}…";
            try
            {
                // 归档包含递归删视频与跨盘目录移动等重 IO，必须放到后台线程，避免 UI 卡顿。
                var projectDir = row.Item.ProjectDir;
                var account = ResolveAccountForQueueItem(row.Item);
                var deleteVideosOnArchive = account?.TiktokDeleteVideosOnArchive ?? true;
                await Task.Run(() => TikTokArchivedProjectService.ArchiveQueueProjectAsync(
                        root,
                        projectDir,
                        archiveRootDir: account?.ResolveArchiveRootPath(root),
                        deleteSourceVideos: deleteVideosOnArchive,
                        deleteWorkflowVideos: deleteVideosOnArchive,
                        deleteMaterialVideos: deleteVideosOnArchive,
                        account: account,
                        queuedAt: row.Item.QueuedAt,
                        uploadCompletedAt: row.Item.UploadCompletedAt))
                    .ConfigureAwait(true);
                row.Item.Archived = true;
                archivedProjectDirs.Add(projectDir);
                successCount++;
                var archivedItem = CloneQueueItem(row.Item);
                _ = Task.Run(() => TikTokExecutionHistoryService.AppendEvent(
                    "project_archived",
                    archivedItem.StatusText,
                    root,
                    archivedItem,
                    message: deleteVideosOnArchive ? "项目已归档，已删除视频文件" : "项目已归档，已保留视频文件",
                    account: account));
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
                AppendLog($"归档失败 [{name}]：{ex.Message}");
            }
        }

        if (successCount > 0)
        {
            ApplyArchivedQueueProjects(archivedProjectDirs);
            ArchivedProjects.SetWorkspace(root, refresh: !_queueOrchestrator.AnyRunning);
            if (_queueOrchestrator.AnyRunning)
                AppendLog("其它队列运行中，已跳过即时归档列表刷新，避免与运行队列争抢磁盘扫描。");
        }

        StatusMessage = failures.Count == 0
            ? $"已归档 {successCount} 个项目"
            : successCount > 0
                ? $"已归档 {successCount} 个项目，失败 {failures.Count} 个：{string.Join("；", failures.Take(3))}"
                : $"归档失败：{string.Join("；", failures.Take(3))}";
        AppendLog(StatusMessage);
    }

    public void RemoveSelectedQueueProjects(IEnumerable<QueueProjectRowViewModel> selectedRows)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root))
        {
            StatusMessage = "请先在「账号管理 → 发布配置」设置上传工作目录";
            return;
        }

        var dirs = selectedRows.Select(r => r.Item.ProjectDir).ToArray();
        if (dirs.Length == 0)
        {
            StatusMessage = "请先选中要移除的项目";
            return;
        }

        WorkspaceQueueService.RemoveProjectsFromQueue(root, dirs);
        RefreshWorkspaceProjects(root);
        StatusMessage = $"已从队列移除 {dirs.Length} 个项目";
        AppendLog(StatusMessage);
    }

    private async Task<UploadTitleImportOutcome> ImportRemoteUploadTitlesAsync(
        string workspaceRoot,
        TikTokAccountProfile? account,
        string rawText,
        int episodeMin,
        int episodeMax,
        string matchMode,
        FinalAction? finalActionOverride,
        bool allowAppendToRunningQueue,
        CancellationToken ct,
        Func<UnknownAuthorConfirmationRequest, CancellationToken, Task<bool>>? confirmUnknownAuthor = null,
        RemoteAutoPublishContext? autoPublish = null,
        bool keepExistingSelection = false,
        string downloadSource = "",
        string forcedSeriesId = "",
        bool useXingeProcessedHistory = false,
        ManualUploadTitleImportTarget? manualTarget = null)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var market = string.Equals(downloadSource, "dramabox", StringComparison.OrdinalIgnoreCase)
            ? TikTokAccountProfile.DramaMarketOverseas
            : account?.TiktokDramaMarket;
        var loaded = manualTarget?.Settings ?? ClientSettingsStore.Load();
        var settings = manualTarget?.Settings ?? VisibleDramaSource.SettingsFor(loaded, market);
        if (autoPublish is not null && forcedSeriesId.StartsWith("dramabox:", StringComparison.OrdinalIgnoreCase))
        {
            settings = settings.Clone();
            settings.DramaSourceChain = "dramabox";
        }
        if (useXingeProcessedHistory && settings.ManagementDedupEnabled)
        {
            settings = settings.Clone();
            settings.ManagementDedupEnabled = false;
        }
        using var chainHold = ShortDramaDramaServices.HoldSettings(settings);
        AppendLog(manualTarget is null
            ? $"使用{VisibleDramaSource.Describe(loaded, market)}导入。"
            : $"搜索链路 · {manualTarget.RouteLabel}");
        var episodeBounds = OverLimitEpisodePolicy.ResolveEpisodeBounds(settings);
        var result = await UploadTitleImportService.ImportAsync(
            root,
            rawText,
            settings,
            account,
            episodeBounds.Minimum,
            episodeBounds.Maximum,
            matchMode,
            AppendLog,
            ct,
            addProjectsToQueue: false,
            confirmUnknownAuthor: confirmUnknownAuthor,
            seriesId: forcedSeriesId,
            uploadTitleRoute: manualTarget?.Route);

        // 标记必须在项目进入运行队列之前写好，队列观察者才能从第一条进度起识别派单。
        if (autoPublish is not null)
        {
            var importedDirs = BuildOrderedDistinctProjectDirs(result.ProjectDirs);
            foreach (var projectDir in importedDirs)
            {
                XingeAutoPublishMarkerStore.Write(projectDir, autoPublish.Assignment);
                autoPublish.ProjectDirs.Add(projectDir);
            }
        }

        // 用户正在执行勾选队列时不改勾选、不追加。命令按队列忙交还，等这轮结束后再派发。
        if (IsLocalCheckedQueueRunning(root))
        {
            var ordered = BuildOrderedDistinctProjectDirs(result.ProjectDirs);
            return new UploadTitleImportOutcome(
                result,
                new WorkspaceQueueTarget(
                    root,
                    $"{account?.DisplayName ?? "当前账号"} · {root}",
                    account?.Id,
                    ordered,
                    finalActionOverride),
                new UploadTitleImportApplyOutcome(ordered, QueueWasRunning: true, [], 0));
        }

        // 自动派单的勾选由今天的派单标记决定，这里只把新项目放进队列，不改步骤勾选。
        var applyOutcome = ApplyRemoteUploadTitleImportResult(
            result,
            root,
            account,
            allowAppendToRunningQueue,
            clearOtherSelection: !keepExistingSelection && autoPublish is not null && result.ProjectDirs.Count > 0,
            retryFailedBeforeNew: !keepExistingSelection && autoPublish is not null,
            keepExistingSelection: keepExistingSelection);
        if (!keepExistingSelection)
            EnsureDisplayedImportedProjectsChecked(root, applyOutcome.OrderedProjectDirs, account);

        var authorExcludedCount = result.Failures.Count(UploadTitleImportService.IsAuthorExcludedFailure);
        StatusMessage =
            $"上传短剧导入完成：加入 {result.QueuedCount} 个，失败 {result.FailedCount} 个，重复 {result.Duplicates.Count} 个"
            + (authorExcludedCount > 0 ? $"，作者排除 {authorExcludedCount} 个" : "");
        AppendLog(StatusMessage);
        var runTarget = new WorkspaceQueueTarget(
            root,
            $"{account?.DisplayName ?? "当前账号"} · {root}",
            account?.Id,
            applyOutcome.OrderedProjectDirs,
            finalActionOverride);
        return new UploadTitleImportOutcome(result, runTarget, applyOutcome);
    }

    private UploadTitleImportApplyOutcome ApplyRemoteUploadTitleImportResult(
        UploadTitleImportResult result,
        string workspaceRoot,
        TikTokAccountProfile? account,
        bool allowAppendToRunningQueue,
        bool clearOtherSelection = false,
        bool retryFailedBeforeNew = false,
        bool keepExistingSelection = false)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var newProjectDirs = BuildOrderedDistinctProjectDirs(result.ProjectDirs);
        var failedDirs = retryFailedBeforeNew
            ? ListFailedRetryProjectDirs(root, account, newProjectDirs)
            : [];
        if (failedDirs.Count > 0)
            AppendLog($"分发新剧前先重试 {failedDirs.Count} 个失败项目。");
        var orderedProjectDirs = BuildOrderedDistinctProjectDirs(failedDirs.Concat(newProjectDirs));

        // orchestrator 的运行快照会早于 UI finally 的最终刷新/落盘结束；这段收尾期间
        // 也必须走 running 分支，避免旧 terminalItems 随后覆盖刚导入的新项目。
        var queueWasRunning =
            IsWorkspaceQueueRunning(root) ||
            IsWorkspaceQueueRunLifecycleActive(root);
        if (queueWasRunning)
        {
            // 运行中绝不能 Scan + Save 恢复快照，否则会覆盖 runner 的 Running/Waiting 状态。
            // auto_run=false 时只保留 Bootstrap 生成的项目目录，队列结束后的正常刷新会发现它。
            var appendCandidates = allowAppendToRunningQueue
                ? BuildFailedRetryAppendCandidates(root, failedDirs, account)
                    .Concat(BuildRemoteAppendCandidates(newProjectDirs, account))
                    .ToList()
                : [];
            if (clearOtherSelection && allowAppendToRunningQueue)
                ClearManualSelectionBeforeAutoPublish(root, clearOtherSelection, orderedProjectDirs);
            var appendedCount = allowAppendToRunningQueue
                ? _queueOrchestrator.TryAppendItemsToRunningWorkspace(root, appendCandidates)
                : 0;
            return new UploadTitleImportApplyOutcome(
                orderedProjectDirs,
                QueueWasRunning: true,
                appendCandidates,
                appendedCount);
        }

        var preparation = PrepareRemoteUploadProjectsWhenIdle(
            root,
            orderedProjectDirs,
            account,
            clearOtherSelection,
            preserveExistingFailures: failedDirs.Count > 0);
        if (preparation.ClearedSelectionCount > 0)
            AppendLog($"分发前已取消勾选 {preparation.ClearedSelectionCount} 个手动选择的项目。");
        if (!keepExistingSelection)
            ApplyRemoteUploadIdlePreparationToDisplayedWorkspace(root, preparation);
        return new UploadTitleImportApplyOutcome(
            orderedProjectDirs,
            QueueWasRunning: false,
            preparation.AppendCandidates,
            AppendedCount: 0);
    }

    private static List<QueueProjectItem> BuildFailedRetryAppendCandidates(
        string workspaceRoot,
        IReadOnlyList<string> failedDirs,
        TikTokAccountProfile? account)
    {
        if (failedDirs.Count == 0)
            return [];

        Dictionary<string, QueueProjectItem> byDir;
        try
        {
            byDir = WorkspaceQueueDatabase.Load(workspaceRoot).Items
                .Where(item => !string.IsNullOrWhiteSpace(item.ProjectDir))
                .GroupBy(item => Path.GetFullPath(item.ProjectDir), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var candidates = new List<QueueProjectItem>();
        foreach (var projectDir in failedDirs)
        {
            if (!byDir.TryGetValue(projectDir, out var item) &&
                !byDir.TryGetValue(Path.GetFullPath(projectDir), out item))
                continue;
            item.Enabled = true;
            if (account is not null && string.IsNullOrWhiteSpace(item.AccountProfileId))
            {
                item.AccountProfileId = account.Id;
                item.AccountProfileName = account.DisplayName;
            }
            candidates.Add(item);
        }

        return candidates;
    }

    private static IReadOnlyList<string> ListFailedRetryProjectDirs(
        string workspaceRoot,
        TikTokAccountProfile? account,
        IReadOnlyCollection<string> newProjectDirs)
    {
        try
        {
            return QueueWorkerRunner.FailedRetryProjectDirsBefore(
                WorkspaceQueueDatabase.Load(workspaceRoot).Items,
                account?.Id,
                newProjectDirs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<QueueProjectItem> BuildRemoteAppendCandidates(
        IReadOnlyList<string> orderedProjectDirs,
        TikTokAccountProfile? account)
    {
        var candidates = new List<QueueProjectItem>();
        var queuedAt = DateTimeOffset.Now;
        for (var index = 0; index < orderedProjectDirs.Count; index++)
        {
            var scanned = WorkspaceProjectScanner.BuildProject(orderedProjectDirs[index]);
            var item = new QueueProjectItem
            {
                ProjectDir = scanned.ProjectDir,
                DisplayName = scanned.DisplayName,
                OriginalTitle = scanned.OriginalTitle,
                NewTitle = scanned.NewTitle,
                Description = scanned.Description,
                GenreCategory = scanned.GenreCategory,
                EpisodeCount = scanned.EpisodeCount,
                VideoVertical = scanned.VideoVertical,
                PrimaryVideoPath = scanned.PrimaryVideoPath,
                CoverPath = scanned.CoverPath,
                Enabled = true,
                QueuedAt = queuedAt.AddMilliseconds(index).ToString("o"),
            };
            ResetQueueItemToPending(item);
            if (account is not null)
            {
                item.AccountProfileId = account.Id;
                item.AccountProfileName = account.DisplayName;
            }
            candidates.Add(item);
        }

        return candidates;
    }

    private void ClearManualSelectionBeforeAutoPublish(
        string workspaceRoot,
        RemoteAutoPublishContext? autoPublish,
        IReadOnlyCollection<string> keepProjectDirs)
    {
        if (autoPublish is null || keepProjectDirs.Count == 0)
            return;

        var cleared = _queueOrchestrator.UncheckRunningProjectsExcept(workspaceRoot, keepProjectDirs);
        if (cleared > 0)
            AppendLog($"分发前已取消勾选 {cleared} 个手动选择的项目。");
    }

    private void ClearManualSelectionBeforeAutoPublish(
        string workspaceRoot,
        bool clearOtherSelection,
        IReadOnlyCollection<string> keepProjectDirs)
    {
        if (!clearOtherSelection || keepProjectDirs.Count == 0)
            return;

        var cleared = _queueOrchestrator.UncheckRunningProjectsExcept(workspaceRoot, keepProjectDirs);
        if (cleared > 0)
            AppendLog($"分发前已取消勾选 {cleared} 个手动选择的项目。");
    }

    private static RemoteUploadIdlePreparation PrepareRemoteUploadProjectsWhenIdle(
        string workspaceRoot,
        IReadOnlyList<string> orderedProjectDirs,
        TikTokAccountProfile? account,
        bool clearOtherSelection = false,
        bool preserveExistingFailures = false)
    {
        WorkspaceQueueService.AddProjectsToQueue(workspaceRoot, orderedProjectDirs);
        var items = WorkspaceQueueService.ScanProjects(workspaceRoot).ToList();
        var runOptions = WorkspaceQueueService.LoadRunOptions(workspaceRoot);
        var byProjectDir = items
            .Where(item => !string.IsNullOrWhiteSpace(item.ProjectDir))
            .GroupBy(item => Path.GetFullPath(item.ProjectDir), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var appendCandidates = new List<QueueProjectItem>();
        var clearedSelection = clearOtherSelection && orderedProjectDirs.Count > 0
            ? QueueWorkerRunner.DisableEnabledProjectsExcept(items, orderedProjectDirs)
            : 0;

        foreach (var projectDir in orderedProjectDirs)
        {
            if (!byProjectDir.TryGetValue(projectDir, out var item))
                continue;

            item.Enabled = true;
            if (!(preserveExistingFailures && QueueWorkerRunner.HasRetryableFailure(item)))
            {
                ResetQueueItemToPending(item);
                if (account is not null)
                {
                    item.AccountProfileId = account.Id;
                    item.AccountProfileName = account.DisplayName;
                }
            }
            appendCandidates.Add(item);
        }

        if (appendCandidates.Count > 0 || clearedSelection > 0)
            WorkspaceQueueService.SaveRunOptions(workspaceRoot, items, runOptions);

        return new RemoteUploadIdlePreparation(items, appendCandidates, runOptions, clearedSelection);
    }

    private void ApplyRemoteUploadIdlePreparationToDisplayedWorkspace(
        string workspaceRoot,
        RemoteUploadIdlePreparation preparation)
    {
        if (!string.Equals(
                NormalizeWorkspacePath(WorkspacePath),
                NormalizeWorkspacePath(workspaceRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ForceRerunCompletedSteps = false;
        OnPropertyChanged(nameof(ForceRerunCompletedSteps));
        ApplyWorkspaceScanResult(workspaceRoot, preparation.AllItems, preparation.RunOptions);
    }

    private static IReadOnlyList<string> BuildOrderedDistinctProjectDirs(IEnumerable<string> projectDirs)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectDir in projectDirs)
        {
            if (string.IsNullOrWhiteSpace(projectDir))
                continue;

            var normalized = Path.GetFullPath(projectDir);
            if (seen.Add(normalized))
                ordered.Add(normalized);
        }

        return ordered;
    }

    public async Task<UploadTitleImportOutcome?> ImportUploadTitlesAsync(
        WorkspaceQueueTarget target,
        string rawText,
        int episodeMin,
        int episodeMax,
        string matchMode,
        CancellationToken ct,
        Func<UnknownAuthorConfirmationRequest, CancellationToken, Task<bool>>? confirmUnknownAuthor = null)
        => await ImportUploadTitlesAsync(CaptureManualUploadTitleImportTarget(target), rawText, episodeMin, episodeMax,
            matchMode, ct, confirmUnknownAuthor).ConfigureAwait(true);

    internal async Task<UploadTitleImportOutcome?> ImportUploadTitlesAsync(
        ManualUploadTitleImportTarget target,
        string rawText,
        int episodeMin,
        int episodeMax,
        string matchMode,
        CancellationToken ct,
        Func<UnknownAuthorConfirmationRequest, CancellationToken, Task<bool>>? confirmUnknownAuthor = null)
    {
        var root = target.QueueTarget.WorkspaceRoot.Trim();
        if (string.IsNullOrWhiteSpace(root))
        {
            StatusMessage = "请先在「账号管理 → 发布配置」设置上传工作目录";
            return null;
        }

        if (string.IsNullOrWhiteSpace(rawText))
        {
            StatusMessage = "请先输入短剧名称";
            return null;
        }

        var account = FindAccountById(target.Route.AccountProfileId)?.Model
            ?? throw new InvalidOperationException("本次导入的账号已不存在，请重新打开「上传短剧」选择账号。");
        return await ImportRemoteUploadTitlesAsync(
            Path.GetFullPath(root),
            account,
            rawText,
            episodeMin,
            episodeMax,
            matchMode,
            target.QueueTarget.FinalActionOverride,
            allowAppendToRunningQueue: true,
            ct: ct,
            confirmUnknownAuthor: confirmUnknownAuthor,
            manualTarget: target).ConfigureAwait(true);
    }

    public async Task<UploadTitleImportResult?> ImportUploadTitlesAsync(
        string rawText,
        int episodeMin,
        int episodeMax,
        string matchMode,
        CancellationToken ct)
    {
        var target = CaptureCurrentWorkspaceQueueTarget();
        if (target is null)
        {
            StatusMessage = "请先在「账号管理 → 发布配置」设置上传工作目录";
            return null;
        }

        var outcome = await ImportUploadTitlesAsync(
            target,
            rawText,
            episodeMin,
            episodeMax,
            matchMode,
            ct).ConfigureAwait(true);
        return outcome?.ImportResult;
    }

    public async Task<UploadTitleAutoRunPreparation> PrepareUploadTitleImportAutoRunAsync(
        UploadTitleImportOutcome outcome)
    {
        var root = Path.GetFullPath(outcome.RunTarget.WorkspaceRoot);
        var applyOutcome = outcome.ApplyOutcome;
        if (applyOutcome.OrderedProjectDirs.Count == 0)
            return new UploadTitleAutoRunPreparation(null, 0);

        if (applyOutcome.AppendedCount > 0)
            return new UploadTitleAutoRunPreparation(null, applyOutcome.AppendedCount);

        void PrepareAfterQueueClosed()
        {
            var account = FindAccountById(outcome.RunTarget.AccountProfileId)?.Model;
            var preparation = PrepareRemoteUploadProjectsWhenIdle(
                root,
                applyOutcome.OrderedProjectDirs,
                account);
            ApplyRemoteUploadIdlePreparationToDisplayedWorkspace(root, preparation);
            applyOutcome = applyOutcome with
            {
                QueueWasRunning = false,
                AppendCandidates = preparation.AppendCandidates,
            };
        }

        // 导入完成与应用结果之间，目标队列可能刚好启动或进入终态落盘；先追加，
        // 无法追加时必须等整个 lifecycle 结束再落盘，不能与旧 terminalItems 竞争。
        if (!applyOutcome.QueueWasRunning && IsWorkspaceQueueBusy(root))
        {
            if (applyOutcome.AppendCandidateCount > 0 && IsWorkspaceQueueRunning(root))
            {
                var appended = _queueOrchestrator.TryAppendItemsToRunningWorkspace(
                    root,
                    applyOutcome.AppendCandidates);
                if (appended > 0)
                    return new UploadTitleAutoRunPreparation(null, appended);
            }

            applyOutcome = applyOutcome with { QueueWasRunning = true };
        }

        if (applyOutcome.QueueWasRunning)
        {
            var closed = await WaitForWorkspaceQueueToFinishClosingAsync(
                root,
                TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            if (!closed || IsWorkspaceQueueBusy(root))
                throw new InvalidOperationException("目标工作目录队列仍在执行持久化收尾，尚不能安全接纳本次导入项目，请稍后重试。");

            PrepareAfterQueueClosed();
        }

        if (applyOutcome.AppendCandidateCount == 0)
            throw new InvalidOperationException("本次导入项目未能写入目标工作目录队列，已取消自动执行。");

        if (IsWorkspaceQueueBusy(root))
        {
            if (IsWorkspaceQueueRunning(root))
            {
                var appended = _queueOrchestrator.TryAppendItemsToRunningWorkspace(
                    root,
                    applyOutcome.AppendCandidates);
                if (appended > 0)
                    return new UploadTitleAutoRunPreparation(null, appended);
            }

            var closed = await WaitForWorkspaceQueueToFinishClosingAsync(
                root,
                TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            if (!closed || IsWorkspaceQueueBusy(root))
                throw new InvalidOperationException("目标工作目录队列被其它操作抢先启动且仍未结束，请稍后重试。");

            // 刚结束的 runner 终态可能覆盖之前的 idle 准备，关闭后重新合并本次项目。
            PrepareAfterQueueClosed();
            if (applyOutcome.AppendCandidateCount == 0)
                throw new InvalidOperationException("本次导入项目未能写入目标工作目录队列，已取消自动执行。");
        }

        return new UploadTitleAutoRunPreparation(
            outcome.RunTarget with { ProjectDirFilter = applyOutcome.OrderedProjectDirs },
            0);
    }

    public int TryAppendUploadTitleImportToRunningQueue(UploadTitleImportOutcome outcome) =>
        _queueOrchestrator.TryAppendItemsToRunningWorkspace(
            outcome.RunTarget.WorkspaceRoot,
            outcome.ApplyOutcome.AppendCandidates);

    public async Task ApplyUploadTitleImportResultAsync(UploadTitleImportResult result)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root))
            return;

        var importedKeys = result.ProjectDirs
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queueRunning = IsCurrentWorkspaceQueueRunning();

        ForceRerunCompletedSteps = false;
        OnPropertyChanged(nameof(ForceRerunCompletedSteps));

        // 导入短剧是明确的“用当前账号处理这批短剧”操作；
        // 账号写入队列项目本身，工作目录不再绑定账号。
        var importAccount = SelectedAccount?.Model;

        if (queueRunning)
        {
            var appended = AppendImportedProjectsWhileRunning(importedKeys);
            if (appended.Count > 0)
            {
                var added = _queueOrchestrator.TryAppendItemsToRunningWorkspace(root, appended);
                StatusMessage = added > 0
                    ? $"已追加 {added} 个项目到运行中的队列末尾，将自动继续执行。"
                    : $"已更新 {appended.Count} 个导入项目，等待当前队列刷新。";
                AppendLog(added > 0
                    ? $"已请求追加 {added} 个项目到运行中的队列末尾。"
                    : $"已更新 {appended.Count} 个导入项目，未找到运行中的追加入口。");
            }

            return;
        }

        await RefreshWorkspaceProjectsAsync(root).ConfigureAwait(false);
        foreach (var item in _queueItems)
        {
            var isImported = importedKeys.Contains(Path.GetFullPath(item.ProjectDir));
            item.Enabled = isImported;
            if (isImported)
            {
                ResetQueueItemToPending(item);
                if (importAccount is not null)
                {
                    item.AccountProfileId = importAccount.Id;
                    item.AccountProfileName = importAccount.DisplayName;
                }
            }
        }

        PersistQueueItems();
        RefreshQueueRowViewModels();
        UpdateWorkspaceBindingSummary(root);
        UpdateQueueSummaryText();
    }

    public void ApplyUploadTitleImportResult(UploadTitleImportResult result) =>
        ApplyUploadTitleImportResultAsync(result).GetAwaiter().GetResult();

    public bool ShouldAutoStartQueueAfterUploadTitleImport(UploadTitleImportResult result) =>
        result.QueuedCount > 0 && !IsCurrentWorkspaceQueueRunning();

    public int TryAppendToRunningQueue(IReadOnlyList<QueueProjectItem> items) =>
        _queueOrchestrator.TryAppendItemsToRunningWorkspace(WorkspacePath, items);

    private List<QueueProjectItem> AppendImportedProjectsWhileRunning(IReadOnlySet<string> importedKeys)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root) || importedKeys.Count == 0)
            return [];

        var existingByKey = _queueItems
            .GroupBy(item => Path.GetFullPath(item.ProjectDir), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var scanned = WorkspaceQueueService.ScanProjects(root)
            .ToDictionary(item => Path.GetFullPath(item.ProjectDir), StringComparer.OrdinalIgnoreCase);
        var appended = new List<QueueProjectItem>();
        var importAccount = SelectedAccount?.Model;

        foreach (var key in importedKeys)
        {
            if (existingByKey.TryGetValue(key, out var existing))
            {
                if (IsQueueItemInActiveUpload(existing))
                    continue;

                existing.Enabled = true;
                ResetQueueItemToPending(existing);
                if (importAccount is not null)
                {
                    existing.AccountProfileId = importAccount.Id;
                    existing.AccountProfileName = importAccount.DisplayName;
                }
                appended.Add(existing);
                continue;
            }

            if (!scanned.TryGetValue(key, out var item))
                continue;

            item.Enabled = true;
            ResetQueueItemToPending(item);
            if (importAccount is not null)
            {
                item.AccountProfileId = importAccount.Id;
                item.AccountProfileName = importAccount.DisplayName;
            }
            _queueItems.Add(item);
            existingByKey[key] = item;
            appended.Add(item);
        }

        if (appended.Count > 0)
        {
            PersistQueueItems();
            RefreshQueueRowViewModels();
        }

        return appended;
    }

    private static bool IsQueueItemInActiveUpload(QueueProjectItem item) =>
        string.Equals(item.StatusText, QueueStepStatus.Running, StringComparison.Ordinal) ||
        string.Equals(item.StatusText, QueueStepStatus.WaitingUploadSlot, StringComparison.Ordinal);

    private static void ResetQueueItemToPending(QueueProjectItem item)
    {
        item.StatusText = QueueStepStatus.Pending;
        item.CurrentStep = "";
        item.LastError = "";
        foreach (var step in QueueStepRegistry.All)
            item.StepStates[step.Key] = QueueStepStatus.Pending;
        item.NormalizeStepStates();
    }

    public async Task DeleteSelectedQueueProjectsAsync(IEnumerable<QueueProjectRowViewModel> selectedRows,
        string confirmedWorkspace, string? confirmedAccountId)
    {
        var account = SelectedAccount?.Model;
        if (!IsActiveWorkspace(confirmedWorkspace) ||
            !string.Equals(account?.Id, confirmedAccountId, StringComparison.Ordinal))
        {
            StatusMessage = "当前工作目录或账号已切换，请重新选择要删除的项目";
            return;
        }
        if (IsWorkspaceQueueBusy(confirmedWorkspace))
        {
            StatusMessage = "该工作目录正在执行队列，请先停止后再删除项目";
            return;
        }

        var request = new QueueProjectDeletionRequest(
            confirmedWorkspace, account, selectedRows.Select(row => row.Item.ProjectDir).ToArray(), _queueItems.ToArray());
        var useCase = new QueueProjectDeletionUseCase(_queueExecution.TryReserveWorkspaceForDeletion);
        StatusMessage = "正在删除选中的项目…";
        QueueProjectDeletionResult result;
        try
        {
            result = await useCase.ExecuteAsync(request);
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除项目失败：{ex.Message}";
            AppendLog(StatusMessage);
            return;
        }
        DropDeletedProjectsFromVisibleQueue(confirmedWorkspace, result.RemovedProjects);
        if (result.Cancelled)
            StatusMessage = $"已取消删除，已移出队列 {result.RemovedProjects.Count} 个项目";
        else if (result.RemovedProjects.Count == 0 && result.Errors.Count == 1)
            StatusMessage = result.Errors[0];
        else
        {
            StatusMessage = $"已移出队列 {result.RemovedProjects.Count} 个项目，已清理 {result.DeletedDirectoryCount} 个目录";
            if (result.Errors.Count > 0)
                StatusMessage += $"，{result.Errors.Count} 项异常";
        }
        AppendLog(StatusMessage);
        foreach (var error in result.Errors.Take(5))
            AppendLog($"删除异常：{error}");
    }

    private void DropDeletedProjectsFromVisibleQueue(string workspaceRoot, IReadOnlyCollection<string> projectDirs)
    {
        if (projectDirs.Count == 0)
            return;

        var remove = projectDirs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var active = IsActiveWorkspace(workspaceRoot);
        var current = _queueItems;
        var options = _queueRunOptions;
        if (!active && !TryGetWorkspaceQueueSnapshot(workspaceRoot, out current, out options))
            return;

        var kept = current.Where(item => !remove.Contains(Path.TrimEndingDirectorySeparator(NormalizeProjectDir(item.ProjectDir))))
            .ToList();
        CacheWorkspaceQueueSnapshot(workspaceRoot, kept, options);
        if (!active)
            return;

        _queueItems = kept;
        ReconcileQueueProjectRows(_queueItems);
        ApplyQueueProjectFilter();
        UpdateQueueSummaryText();
        RefreshLogSnapshot(force: true);
    }

    private void ApplyArchivedQueueProjects(IReadOnlyCollection<string> archivedDirs)
    {
        if (archivedDirs.Count == 0)
            return;

        var archivedKeys = archivedDirs
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Select(NormalizeProjectDir)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (archivedKeys.Count == 0)
            return;

        _queueItems = _queueItems
            .Where(item => !archivedKeys.Contains(NormalizeProjectDir(item.ProjectDir)))
            .ToList();
        PersistQueueItems();
        UpdateQueueSummaryText();
        RefreshTodayUploadCount();
        CacheWorkspaceQueueSnapshot(WorkspacePath, _queueItems, _queueRunOptions);
    }

    public async Task SyncSelectedManagementAsync(IEnumerable<QueueProjectRowViewModel> selectedRows, CancellationToken ct)
    {
        var rows = selectedRows.ToArray();
        if (rows.Length == 0)
        {
            StatusMessage = "请先选中要同步的任务";
            return;
        }

        var ok = 0;
        var failed = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var account = ResolveAccountForQueueItem(row.Item);
            var result = await TikTokManagementUploadRecordSyncService
                .SyncUploadRecordAsync(row.Item, account, ct);
            if (result.Ok) ok++; else failed++;
            AppendLog($"同步管理系统：{row.Title} - {result.Message}");
        }

        StatusMessage = $"管理系统同步完成：成功 {ok}，失败 {failed}";
        AppendLog(StatusMessage);
    }

    private TikTokAccountProfile? ResolveAccountForQueueItem(QueueProjectItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.AccountProfileId))
        {
            var bound = _store.FindByNameOrId(item.AccountProfileId);
            if (bound is not null) return bound;
        }

        return SelectedAccount?.Model;
    }

    private static void AddDeleteTarget(
        string? path,
        string workspaceRoot,
        ISet<string> targets,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var target = Path.GetFullPath(path);
        var workspace = Path.GetFullPath(workspaceRoot);
        if (string.Equals(target, workspace, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"拒绝删除工作目录根：{target}");
            return;
        }

        if (!IsWithinWorkspace(target, workspace))
        {
            errors.Add($"跳过工作目录外路径：{target}");
            return;
        }

        targets.Add(target);
    }

    private static bool IsWithinWorkspace(string path, string workspaceRoot)
        => WorkspaceQueueService.IsProjectInsideWorkspace(workspaceRoot, path);

    public string ExportQueueExcel()
    {
        return ExportQueueExcelCore(CaptureExcelExportSnapshotContext(WorkspacePath.Trim()));
    }

    public Task<string> ExportQueueExcelAsync(CancellationToken ct = default)
    {
        var context = CaptureExcelExportSnapshotContext(WorkspacePath.Trim());
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return ExportQueueExcelCore(context);
        }, ct);
    }

    private string ExportQueueExcelCore(ExcelExportSnapshotContext context)
    {
        if (string.IsNullOrEmpty(context.ActiveWorkspace))
            throw new InvalidOperationException("请先在「账号管理 → 发布配置」设置上传工作目录");
        var settings = ClientSettingsStore.Load();
        var snapshot = BuildExcelExportSnapshot(context);
        if (snapshot.Items.Count == 0)
            throw new InvalidOperationException("没有可导出的队列项目");

        return TikTokExcelExportService.Export(
            context.ActiveWorkspace,
            snapshot.Items,
            account: null,
            settings,
            snapshot.WorkspaceByProject,
            context.Accounts);
    }

    private void OnQueueStatePersisted(string workspaceRoot)
    {
        MarkQueueExcelExportPending(workspaceRoot);
        if (ShouldDeferQueueExcelExport())
            return;

        ScheduleDebouncedQueueExcelExport();
    }

    private void ScheduleDebouncedQueueExcelExport()
    {
        lock (_queueExcelExportLock)
        {
            if (_queueExcelExportDebounceScheduled || _queueFinalExcelExportScheduled)
                return;

            _queueExcelExportDebounceScheduled = true;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900)).ConfigureAwait(false);
            lock (_queueExcelExportLock)
            {
                _queueExcelExportDebounceScheduled = false;
                if (_queueRunActive || _queueFinalExcelExportScheduled)
                    return;
            }

            ScheduleFinalQueueExcelExport();
        });
    }

    private bool ShouldDeferQueueExcelExport()
    {
        lock (_queueExcelExportLock)
            return _queueRunActive || _queueFinalExcelExportScheduled;
    }

    private void MarkQueueExcelExportPending(string workspaceRoot)
    {
        var root = NormalizeWorkspaceRootKey(workspaceRoot);
        if (string.IsNullOrWhiteSpace(root)) return;

        lock (_queueExcelExportLock)
            _pendingQueueExcelExportWorkspaces.Add(root);
    }

    private void ScheduleFinalQueueExcelExport()
    {
        string[] workspaces;
        lock (_queueExcelExportLock)
        {
            if (_pendingQueueExcelExportWorkspaces.Count == 0)
            {
                _queueRunActive = false;
                return;
            }

            _queueFinalExcelExportScheduled = true;
            _queueRunActive = false;
            workspaces = _pendingQueueExcelExportWorkspaces.ToArray();
        }

        _ = Task.Run(() =>
        {
            try
            {
                foreach (var workspace in workspaces)
                    _queueStatePersist.Flush(workspace, TimeSpan.FromSeconds(3));

                var exportWorkspace = ResolveFinalQueueExcelExportWorkspace(workspaces);
                if (!string.IsNullOrWhiteSpace(exportWorkspace))
                    AutoExportQueueExcelForWorkspaceNow(exportWorkspace);
            }
            finally
            {
                var scheduleAgain = false;
                lock (_queueExcelExportLock)
                {
                    foreach (var workspace in workspaces)
                        _pendingQueueExcelExportWorkspaces.Remove(workspace);

                    _queueRunActive = false;
                    _queueFinalExcelExportScheduled = false;
                    scheduleAgain = _pendingQueueExcelExportWorkspaces.Count > 0;
                }

                Avalonia.Threading.Dispatcher.UIThread.Post(RefreshRunningWorkspacesSummary);
                if (scheduleAgain)
                    ScheduleFinalQueueExcelExport();
            }
        });
    }

    private string ResolveFinalQueueExcelExportWorkspace(IReadOnlyList<string> workspaces)
    {
        if (workspaces.Count == 0) return "";

        var current = NormalizeWorkspaceRootKey(WorkspacePath);
        if (!string.IsNullOrWhiteSpace(current) &&
            workspaces.Any(workspace => string.Equals(workspace, current, StringComparison.OrdinalIgnoreCase)))
        {
            return current;
        }

        return workspaces.FirstOrDefault(Directory.Exists) ?? workspaces[0];
    }

    private void AutoExportQueueExcelForWorkspace(string workspaceRoot)
    {
        if (ShouldDeferQueueExcelExport())
        {
            MarkQueueExcelExportPending(workspaceRoot);
            return;
        }

        _ = Task.Run(() => AutoExportQueueExcelForWorkspaceNow(workspaceRoot));
    }

    private void AutoExportQueueExcelForWorkspaceNow(string workspaceRoot)
    {
        try
        {
            var settings = ClientSettingsStore.Load();
            if (!settings.TiktokExcelAutoExportEnabled) return;
            if (string.IsNullOrWhiteSpace(workspaceRoot)) return;

            var context = CaptureExcelExportSnapshotContext(workspaceRoot);
            var snapshot = BuildExcelExportSnapshot(context);
            if (snapshot.Items.Count == 0) return;

            TikTokExcelExportService.Export(
                context.ActiveWorkspace,
                snapshot.Items,
                account: null,
                settings,
                snapshot.WorkspaceByProject,
                context.Accounts);
        }
        catch (Exception ex)
        {
            AppendLog($"Excel 自动导出失败：{ex.Message}");
        }
    }

    private ExcelExportSnapshotContext CaptureExcelExportSnapshotContext(string activeWorkspace)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            return Avalonia.Threading.Dispatcher.UIThread
                .InvokeAsync(() => CaptureExcelExportSnapshotContext(activeWorkspace))
                .GetTask()
                .GetAwaiter()
                .GetResult();
        }

        var accounts = _store.Accounts.Select(CloneAccountProfileForExport).ToList();
        var selectedAccount = SelectedAccount?.Model is { } account
            ? CloneAccountProfileForExport(account)
            : null;
        var targets = BuildAccountWorkspaceTargets(accounts, skipMissingWorkspace: true, out _);

        return new ExcelExportSnapshotContext(
            activeWorkspace.Trim(),
            WorkspacePath,
            CloneQueueItems(_queueItems),
            accounts,
            targets,
            selectedAccount);
    }

    private (IReadOnlyList<QueueProjectItem> Items, IReadOnlyDictionary<string, string> WorkspaceByProject) BuildExcelExportSnapshot(ExcelExportSnapshotContext context)
    {
        var activeRoot = SafeFullPath(context.ActiveWorkspace);
        var displayedRoot = string.IsNullOrWhiteSpace(context.DisplayedWorkspace) ? "" : SafeFullPath(context.DisplayedWorkspace);
        var workspaces = new Dictionary<string, TikTokAccountProfile?>(StringComparer.OrdinalIgnoreCase);

        void AddWorkspace(string? workspace, TikTokAccountProfile? account)
        {
            if (string.IsNullOrWhiteSpace(workspace)) return;
            var normalized = SafeFullPath(Environment.ExpandEnvironmentVariables(workspace.Trim()));
            if (!Directory.Exists(normalized)) return;

            if (!workspaces.TryGetValue(normalized, out var existing) || (existing is null && account is not null))
                workspaces[normalized] = account;
        }

        foreach (var target in context.WorkspaceTargets)
            AddWorkspace(target.WorkspaceRoot, FindExportAccount(context.Accounts, target.AccountProfileId));

        AddWorkspace(activeRoot, context.SelectedAccount);

        var items = new List<QueueProjectItem>();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var keyByAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var workspaceByProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var knownAccountKeys = BuildKnownAccountKeys(context.Accounts);

        void AddOrMergeItem(QueueProjectItem source, string workspaceRoot, TikTokAccountProfile? account, bool preferSource)
        {
            if (string.IsNullOrWhiteSpace(source.ProjectDir) &&
                string.IsNullOrWhiteSpace(source.OriginalTitle) &&
                string.IsNullOrWhiteSpace(source.NewTitle) &&
                string.IsNullOrWhiteSpace(source.DisplayName))
            {
                return;
            }

            var item = CloneQueueItem(source);
            FillExportAccount(item, account);
            var key = BuildExcelExportItemKey(item);

            if (indexByKey.TryGetValue(key, out var index))
            {
                MergeExcelExportItem(items[index], item, preferSource);
                RegisterExcelExportAliases(items[index], key, keyByAlias);
            }
            else
            {
                indexByKey[key] = items.Count;
                items.Add(item);
                RegisterExcelExportAliases(item, key, keyByAlias);
            }

            if (!string.IsNullOrWhiteSpace(item.ProjectDir))
                workspaceByProject[ExcelProjectKey(item.ProjectDir)] = workspaceRoot;
        }

        void MergeArchiveItem(ArchivedProjectItem archive, string workspaceRoot, TikTokAccountProfile? account)
        {
            var archiveItem = TikTokArchivedProjectService.ToQueueItemForSync(archive);
            archiveItem.Archived = true;
            if (string.IsNullOrWhiteSpace(archiveItem.UploadCompletedAt))
                archiveItem.UploadCompletedAt = archive.ArchivedAt;
            FillExportAccount(archiveItem, account);

            var matchKey = FindArchiveExportMatchKey(archive, archiveItem, workspaceRoot, keyByAlias);
            if (!string.IsNullOrWhiteSpace(matchKey) && indexByKey.TryGetValue(matchKey, out var index))
            {
                var existing = items[index];
                existing.Archived = true;
                MergeExcelExportItem(existing, archiveItem, preferSource: false);
                RegisterExcelExportAliases(existing, matchKey, keyByAlias);
                if (!string.IsNullOrWhiteSpace(existing.ProjectDir))
                    workspaceByProject[ExcelProjectKey(existing.ProjectDir)] = workspaceRoot;
                return;
            }

            AddOrMergeItem(archiveItem, workspaceRoot, account, preferSource: false);
        }

        foreach (var snapshot in TikTokExecutionHistoryService.LoadProjectSnapshots())
        {
            var item = snapshot.Item;
            var account = ResolveAccountForExportItem(item, context.Accounts);
            var snapshotWorkspace = ResolveHistorySnapshotWorkspace(snapshot, account, workspaces);
            if (!ShouldExportHistorySnapshot(snapshotWorkspace, item, workspaces.Keys, knownAccountKeys))
                continue;

            AddOrMergeItem(item, snapshotWorkspace, account, preferSource: true);
        }

        foreach (var (workspaceRoot, account) in workspaces)
        {
            var sourceItems = !string.IsNullOrWhiteSpace(displayedRoot) &&
                              string.Equals(workspaceRoot, displayedRoot, StringComparison.OrdinalIgnoreCase)
                ? context.DisplayedItems
                : WorkspaceQueueService.ScanProjects(workspaceRoot);

            foreach (var source in sourceItems)
                AddOrMergeItem(source, workspaceRoot, account, preferSource: false);

            foreach (var archive in TikTokArchivedProjectService.List(
                         workspaceRoot,
                         account?.ResolveArchiveRootPath(workspaceRoot)))
                MergeArchiveItem(archive, workspaceRoot, account);
        }

        return (items, workspaceByProject);
    }

    private static HashSet<string> BuildKnownAccountKeys(IReadOnlyList<TikTokAccountProfile> accounts)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts)
        {
            AddAccountKey(keys, account.Id);
            AddAccountKey(keys, account.Name);
            AddAccountKey(keys, account.DisplayName);
            AddAccountKey(keys, account.ResolveTikTokAccountName());
            AddAccountKey(keys, account.TiktokLoginEmail);
            AddAccountKey(keys, account.TiktokLastLoginEmail);
        }

        return keys;
    }

    private static TikTokAccountProfile? ResolveAccountForExportItem(
        QueueProjectItem item,
        IReadOnlyList<TikTokAccountProfile> accounts)
    {
        if (!string.IsNullOrWhiteSpace(item.AccountProfileId))
        {
            var account = FindExportAccount(accounts, item.AccountProfileId);
            if (account is not null) return account;
        }

        if (!string.IsNullOrWhiteSpace(item.AccountProfileName))
        {
            var account = FindExportAccount(accounts, item.AccountProfileName);
            if (account is not null) return account;
        }

        return null;
    }

    private static TikTokAccountProfile? FindExportAccount(
        IReadOnlyList<TikTokAccountProfile> accounts,
        string? nameOrId)
    {
        var text = (nameOrId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;

        return accounts.FirstOrDefault(account =>
            string.Equals(account.Id, text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(account.Name, text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(account.DisplayName, text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(account.ResolveTikTokAccountName(), text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(account.TiktokLoginEmail, text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(account.TiktokLastLoginEmail, text, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveHistorySnapshotWorkspace(
        TikTokExecutionProjectSnapshot snapshot,
        TikTokAccountProfile? account,
        IReadOnlyDictionary<string, TikTokAccountProfile?> workspaces)
    {
        var workspace = NormalizeWorkspaceRootKey(snapshot.Workspace);
        if (!string.IsNullOrWhiteSpace(workspace))
            return workspace;

        var accountWorkspace = account?.ResolveWorkspacePath() ?? "";
        if (!string.IsNullOrWhiteSpace(accountWorkspace))
            return NormalizeWorkspaceRootKey(accountWorkspace);

        return workspaces.Keys.FirstOrDefault() ?? "";
    }

    private static bool ShouldExportHistorySnapshot(
        string workspaceRoot,
        QueueProjectItem item,
        IEnumerable<string> knownWorkspaces,
        ISet<string> knownAccountKeys)
    {
        if (!string.IsNullOrWhiteSpace(workspaceRoot) &&
            knownWorkspaces.Any(workspace => string.Equals(workspace, workspaceRoot, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return AccountMatchesKnownKeys(item, knownAccountKeys);
    }

    private static bool AccountMatchesKnownKeys(QueueProjectItem item, ISet<string> knownAccountKeys) =>
        IsKnownAccountKey(knownAccountKeys, item.AccountProfileId) ||
        IsKnownAccountKey(knownAccountKeys, item.AccountProfileName);

    private static bool IsKnownAccountKey(ISet<string> knownAccountKeys, string? value) =>
        !string.IsNullOrWhiteSpace(value) && knownAccountKeys.Contains(value.Trim());

    private static void AddAccountKey(ISet<string> keys, string? value)
    {
        var text = (value ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(text))
            keys.Add(text);
    }

    private static void FillExportAccount(QueueProjectItem item, TikTokAccountProfile? account)
    {
        if (account is null) return;
        if (string.IsNullOrWhiteSpace(item.AccountProfileId))
            item.AccountProfileId = account.Id;
        if (string.IsNullOrWhiteSpace(item.AccountProfileName))
            item.AccountProfileName = account.DisplayName;
    }

    private static void MergeExcelExportItem(QueueProjectItem target, QueueProjectItem source, bool preferSource)
    {
        if (preferSource)
        {
            var archived = target.Archived || source.Archived;
            var merged = CloneQueueItem(source);
            target.ProjectDir = merged.ProjectDir;
            target.DisplayName = merged.DisplayName;
            target.OriginalTitle = merged.OriginalTitle;
            target.NewTitle = merged.NewTitle;
            target.EpisodeCount = merged.EpisodeCount;
            target.SourceEpisodeCount = merged.SourceEpisodeCount;
            target.GenreCategory = merged.GenreCategory;
            target.Description = merged.Description;
            target.QueueEntryDramaType = merged.QueueEntryDramaType;
            target.AccountProfileId = merged.AccountProfileId;
            target.AccountProfileName = merged.AccountProfileName;
            target.QueuedAt = merged.QueuedAt;
            target.UploadCompletedAt = merged.UploadCompletedAt;
            target.Enabled = merged.Enabled;
            target.CurrentStep = merged.CurrentStep;
            target.StatusText = merged.StatusText;
            target.LastError = merged.LastError;
            target.Remark = merged.Remark;
            target.ManualUploadStatus = merged.ManualUploadStatus;
            target.StepStates = new Dictionary<string, string>(merged.StepStates);
            target.Archived = archived;
            return;
        }

        target.Archived |= source.Archived;
        if (string.IsNullOrWhiteSpace(target.ProjectDir)) target.ProjectDir = source.ProjectDir;
        if (string.IsNullOrWhiteSpace(target.DisplayName)) target.DisplayName = source.DisplayName;
        if (string.IsNullOrWhiteSpace(target.OriginalTitle)) target.OriginalTitle = source.OriginalTitle;
        if (string.IsNullOrWhiteSpace(target.NewTitle)) target.NewTitle = source.NewTitle;
        if (target.EpisodeCount <= 0) target.EpisodeCount = source.EpisodeCount;
        if (target.SourceEpisodeCount <= target.EpisodeCount && source.SourceEpisodeCount > source.EpisodeCount)
            target.SourceEpisodeCount = source.SourceEpisodeCount;
        if (string.IsNullOrWhiteSpace(target.GenreCategory)) target.GenreCategory = source.GenreCategory;
        if (string.IsNullOrWhiteSpace(target.Description)) target.Description = source.Description;
        if (string.IsNullOrWhiteSpace(target.AccountProfileId)) target.AccountProfileId = source.AccountProfileId;
        if (string.IsNullOrWhiteSpace(target.AccountProfileName)) target.AccountProfileName = source.AccountProfileName;
        if (string.IsNullOrWhiteSpace(target.QueuedAt)) target.QueuedAt = source.QueuedAt;
        if (string.IsNullOrWhiteSpace(target.UploadCompletedAt)) target.UploadCompletedAt = source.UploadCompletedAt;
        if (string.IsNullOrWhiteSpace(target.StatusText)) target.StatusText = source.StatusText;
        if (string.IsNullOrWhiteSpace(target.LastError)) target.LastError = source.LastError;
        foreach (var (key, value) in source.StepStates)
        {
            if (!target.StepStates.TryGetValue(key, out var existing) ||
                string.Equals(existing, QueueStepStatus.Pending, StringComparison.Ordinal))
            {
                target.StepStates[key] = value;
            }
        }
    }

    private static string FindArchiveExportMatchKey(
        ArchivedProjectItem archive,
        QueueProjectItem archiveItem,
        string workspaceRoot,
        IReadOnlyDictionary<string, string> keyByAlias)
    {
        foreach (var alias in BuildArchiveExportAliases(archive, archiveItem, workspaceRoot))
        {
            if (keyByAlias.TryGetValue(alias, out var key))
                return key;
        }

        return "";
    }

    private static IEnumerable<string> BuildArchiveExportAliases(
        ArchivedProjectItem archive,
        QueueProjectItem archiveItem,
        string workspaceRoot)
    {
        foreach (var alias in BuildExcelExportAliases(archiveItem))
            yield return alias;

        foreach (var candidate in new[]
                 {
                     archive.ArchivedSourceDir,
                     archive.ArchivedWorkflowDir,
                     string.IsNullOrWhiteSpace(archive.ProjectKey) ? "" : Path.Combine(workspaceRoot, archive.ProjectKey),
                     string.IsNullOrWhiteSpace(archive.ProjectKey) ? "" : Path.Combine(workspaceRoot, "workflow", archive.ProjectKey),
                     string.IsNullOrWhiteSpace(archive.ProjectKey) ? "" : Path.Combine(workspaceRoot, "workflow", "_" + archive.ProjectKey),
                 })
        {
            var alias = BuildExcelPathAlias(archiveItem, candidate);
            if (!string.IsNullOrWhiteSpace(alias))
                yield return alias;
        }
    }

    private static void RegisterExcelExportAliases(
        QueueProjectItem item,
        string key,
        IDictionary<string, string> keyByAlias)
    {
        foreach (var alias in BuildExcelExportAliases(item))
            keyByAlias[alias] = key;
    }

    private static IEnumerable<string> BuildExcelExportAliases(QueueProjectItem item)
    {
        foreach (var alias in new[]
                 {
                     BuildExcelPathAlias(item, item.ProjectDir),
                     BuildExcelTitleAlias(item),
                 })
        {
            if (!string.IsNullOrWhiteSpace(alias))
                yield return alias;
        }
    }

    private static string BuildExcelExportItemKey(QueueProjectItem item) =>
        FirstNonEmpty(BuildExcelPathAlias(item, item.ProjectDir), BuildExcelTitleAlias(item));

    private static string BuildExcelPathAlias(QueueProjectItem item, string? path)
    {
        var normalized = NormalizeExcelProjectPath(path);
        return string.IsNullOrWhiteSpace(normalized) ? "" : $"{ExcelAccountKey(item)}|path|{normalized}";
    }

    private static string BuildExcelTitleAlias(QueueProjectItem item)
    {
        var title = FirstNonEmpty(item.OriginalTitle, item.NewTitle, item.DisplayName);
        return string.IsNullOrWhiteSpace(title) ? "" : $"{ExcelAccountKey(item)}|title|{title.Trim()}";
    }

    private static string ExcelAccountKey(QueueProjectItem item) =>
        FirstNonEmpty(item.AccountProfileId, item.AccountProfileName, "未绑定");

    private static string NormalizeExcelProjectPath(string? path)
    {
        var text = (path ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) return "";
        try { return Path.GetFullPath(text).Replace('\\', '/').ToLowerInvariant(); }
        catch { return text.Replace('\\', '/').ToLowerInvariant(); }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return "";
    }

    private static string ExcelProjectKey(string? projectDir) =>
        (projectDir ?? "").Trim().Replace('\\', '/').ToLowerInvariant();

    private void AutoExportQueueExcel() => AutoExportQueueExcelForWorkspace(WorkspacePath);

    private sealed class ExcelExportSnapshotContext
    {
        public ExcelExportSnapshotContext(
            string activeWorkspace,
            string displayedWorkspace,
            IReadOnlyList<QueueProjectItem> displayedItems,
            IReadOnlyList<TikTokAccountProfile> accounts,
            IReadOnlyList<WorkspaceQueueTarget> workspaceTargets,
            TikTokAccountProfile? selectedAccount)
        {
            ActiveWorkspace = activeWorkspace;
            DisplayedWorkspace = displayedWorkspace;
            DisplayedItems = displayedItems;
            Accounts = accounts;
            WorkspaceTargets = workspaceTargets;
            SelectedAccount = selectedAccount;
        }

        public string ActiveWorkspace { get; }
        public string DisplayedWorkspace { get; }
        public IReadOnlyList<QueueProjectItem> DisplayedItems { get; }
        public IReadOnlyList<TikTokAccountProfile> Accounts { get; }
        public IReadOnlyList<WorkspaceQueueTarget> WorkspaceTargets { get; }
        public TikTokAccountProfile? SelectedAccount { get; }
    }

    private sealed class WorkspaceQueueSnapshot
    {
        public List<QueueProjectItem> Items { get; init; } = new();
        public QueueRunOptions? Options { get; set; }
    }

}
