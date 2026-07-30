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

public sealed record UploadTitleAutoRunPreparation(
    WorkspaceQueueTarget? RunTarget,
    int AppendedCount);

public sealed partial class MainViewModel : ViewModelBase
{
    public const string TikTokLoginUrl = TikTokUrls.DefaultLoginUrl;

    private sealed record RemoteUploadIdlePreparation(
        List<QueueProjectItem> AllItems,
        List<QueueProjectItem> AppendCandidates,
        QueueRunOptions RunOptions);

    private sealed record WorkspaceQueueExecutionContext(
        string BatchId,
        TikTokAccountProfile? Account);

    private readonly AccountStore _store;
    private readonly AccountContextService _context;
    private readonly WorkspaceQueueOrchestrator _queueOrchestrator = new();
    private readonly QueueStatePersistService _queueStatePersist = new();
    private readonly XingeRemoteCommandService _xingeRemoteCommandService = new();
    private CancellationTokenSource? _queueCts;
    private string? _manualInterventionWorkspaceRoot;
    private bool _applyingQueueStepToggles;
    private bool _queueRunActive;
    private int _activeQueueRunCount;
    private string _displayedWorkspaceRoot = "";
    private readonly object _workspaceQueueSnapshotsLock = new();
    private readonly Dictionary<string, WorkspaceQueueSnapshot> _workspaceQueueSnapshots =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _workspaceQueueRunLifecyclesLock = new();
    private readonly Dictionary<string, HashSet<WorkspaceQueueRunLifecycle>> _workspaceQueueRunLifecycles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _queueExcelExportLock = new();
    private readonly HashSet<string> _pendingQueueExcelExportWorkspaces =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _queueFinalExcelExportScheduled;
    private bool _queueExcelExportDebounceScheduled;

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = new();
    public ObservableCollection<AccountItemViewModel> FilteredAccounts { get; } = new();
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

    public IReadOnlyList<FinalActionChoice> FinalActionChoices { get; } = new[]
    {
        new FinalActionChoice("只填不发（安全）", FinalAction.None),
        new FinalActionChoice("保存草稿", FinalAction.Draft),
        new FinalActionChoice("直接发表", FinalAction.Publish),
    };

    [ObservableProperty] private AccountItemViewModel? _selectedAccount;
    [ObservableProperty] private string _statusMessage = "就绪";
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
    [ObservableProperty] private bool _queueRewriteEnabled;
    [ObservableProperty] private bool _queueGeneratePosterEnabled;
    [ObservableProperty] private bool _queueGenerateProofMaterialEnabled;
    [ObservableProperty] private bool _queueDeleteSourceVideosEnabled;
    [ObservableProperty] private bool _queueSmallVideoRepairEnabled;
    [ObservableProperty] private bool _queueVideoTranslateEnabled;
    [ObservableProperty] private bool _queueSilenceDetectEnabled;
    [ObservableProperty] private bool _queueSilenceRepairEnabled;
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
    public SystemSettingsViewModel SystemSettings { get; } = new();
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
    private bool _todayUploadRefreshScheduled;
    private int _todayUploadRefreshGeneration;
    private int _pendingTodayUploadGeneration;
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

    public MainViewModel(AccountStore store, AccountContextService context)
    {
        _store = store;
        _context = context;
        _store.Load();
        foreach (var account in _store.Accounts)
            Accounts.Add(new AccountItemViewModel(account));
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == _store.ActiveAccountId) ?? Accounts.FirstOrDefault();
        _selectedFinalAction = FinalActionChoices[0];
        RefreshFilteredAccounts();
        RefreshWorkspaceFromActiveAccount();
        DramaDownload.LogRequested += AppendLog;
        WireSystemSettings();
        SystemServices.StatusRequested += message => StatusMessage = message;
        SystemServices.RemoteCommandRequested += ExecuteRemoteCommandAsync;
        WireXingeRemoteCommandService();
        ArchivedProjects.StatusRequested += message => StatusMessage = message;
        ArchivedProjects.AccountProvider = () => SelectedAccount?.Model;
        ArchivedProjects.AccountResolver = ResolveAccountForQueueItem;
        ArchivedProjects.Restored += () => RefreshWorkspaceProjects(WorkspacePath, force: true);
        DramaDownload.ImportToQueueRequested += ImportDramaProjectsToQueue;
        DramaDownload.UploadWorkspaceRequested += ResolveSelectedAccountWorkspacePath;
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
        SystemSettings.Load(WorkspacePath);
        DramaDownload.ApplyClientSettings(ClientSettingsStore.Load(), preferSavedWorkspace: true);
    }

    public MainViewModel()
    {
        var store = new AccountStore();
        _store = store;
        _context = new AccountContextService(store);
        _store.Load();
        foreach (var account in _store.Accounts)
            Accounts.Add(new AccountItemViewModel(account));
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == _store.ActiveAccountId) ?? Accounts.FirstOrDefault();
        _selectedFinalAction = FinalActionChoices[0];
        RefreshFilteredAccounts();
        RefreshWorkspaceFromActiveAccount();
        DramaDownload.LogRequested += AppendLog;
        WireSystemSettings();
        SystemServices.StatusRequested += message => StatusMessage = message;
        SystemServices.RemoteCommandRequested += ExecuteRemoteCommandAsync;
        WireXingeRemoteCommandService();
        ArchivedProjects.StatusRequested += message => StatusMessage = message;
        ArchivedProjects.AccountProvider = () => SelectedAccount?.Model;
        ArchivedProjects.AccountResolver = ResolveAccountForQueueItem;
        ArchivedProjects.Restored += () => RefreshWorkspaceProjects(WorkspacePath, force: true);
        DramaDownload.ImportToQueueRequested += ImportDramaProjectsToQueue;
        DramaDownload.UploadWorkspaceRequested += ResolveSelectedAccountWorkspacePath;
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
        _xingeRemoteCommandService.StatusChanged += OnXingeRemoteStatusChanged;
    }

    public void StartXingeRemoteCommandService() => RestartXingeRemoteCommandService();

    public void StopXingeRemoteCommandService() => _xingeRemoteCommandService.Stop();

    private void RestartXingeRemoteCommandService()
    {
        _xingeRemoteCommandService.Restart(
            ExecuteXingeRemoteCommandAsync,
            BuildXingeRemoteRegistrationSnapshotThreadSafe,
            AppendLog);
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

    private Task<TikTokRemoteCommandResult> ExecuteXingeRemoteCommandAsync(TikTokRemoteCommand command)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            return ExecuteRemoteCommandAsync(command);

        return Avalonia.Threading.Dispatcher.UIThread
            .InvokeAsync(() => ExecuteRemoteCommandAsync(command));
    }

    private XingeRemoteRegistrationSnapshot BuildXingeRemoteRegistrationSnapshotThreadSafe()
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            return BuildXingeRemoteRegistrationSnapshot();

        return Avalonia.Threading.Dispatcher.UIThread
            .InvokeAsync(BuildXingeRemoteRegistrationSnapshot)
            .GetTask()
            .GetAwaiter()
            .GetResult();
    }

    private XingeRemoteRegistrationSnapshot BuildXingeRemoteRegistrationSnapshot()
    {
        var activeId = SelectedAccount?.Id ?? "";
        var accounts = Accounts
            .Select(account => new XingeRemoteAccountSnapshot(
                account.Id,
                account.DisplayName,
                HasAccountAuthFile(account.Model),
                string.Equals(account.Id, activeId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return new XingeRemoteRegistrationSnapshot(
            WorkspacePath ?? "",
            activeId,
            accounts);
    }

    private static bool HasAccountAuthFile(TikTokAccountProfile profile)
    {
        try
        {
            var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(profile);
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

    public void AppendLog(string text)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AppendLog(text));
            return;
        }

        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var level = LogService.InferLevel(text);
        Logs.Append($"[{timestamp}] {LogService.FormatLevel(level)} {text}");
    }

    public void RefreshLogSnapshot(bool force = false)
    {
        if (!force && (DateTime.UtcNow - _lastLogSnapshotUtc).TotalMilliseconds < 1500)
            return;
        _lastLogSnapshotUtc = DateTime.UtcNow;
        Logs.UpdateSnapshot(QueueProjectRows, WorkspacePath, IsQueueRunning);
    }

    public void RequestNavigate(string pageTag) => NavigatePageRequested?.Invoke(pageTag);

    partial void OnAccountSearchTextChanged(string value) => RefreshFilteredAccounts();

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

        _context.SwitchTo(value.Id);
        RestoreQueueSearchTextForSelectedAccount();
        RefreshWorkspaceFromActiveAccount();
        AccountSwitchRequested?.Invoke(value);
        RefreshTodayUploadCount();
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
    partial void OnQueueRewriteEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueGeneratePosterEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueGenerateProofMaterialEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueDeleteSourceVideosEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueUploadEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueSmallVideoRepairEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueVideoTranslateEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueSilenceDetectEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueSilenceRepairEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
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

    [RelayCommand]
    private void SelectAllQueueSteps()
    {
        _applyingQueueStepToggles = true;
        try
        {
            QueueDownloadEnabled = true;
            QueueRewriteEnabled = true;
            QueueGeneratePosterEnabled = true;
            QueueGenerateProofMaterialEnabled = true;
            QueueDeleteSourceVideosEnabled = true;
            QueueSmallVideoRepairEnabled = true;
            QueueVideoTranslateEnabled = false;
            QueueSilenceDetectEnabled = true;
            QueueSilenceRepairEnabled = true;
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
            QueueRewriteEnabled = false;
            QueueGeneratePosterEnabled = false;
            QueueGenerateProofMaterialEnabled = false;
            QueueDeleteSourceVideosEnabled = false;
            QueueSmallVideoRepairEnabled = false;
            QueueVideoTranslateEnabled = false;
            QueueSilenceDetectEnabled = false;
            QueueSilenceRepairEnabled = false;
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
        var selectedId = SelectedAccount?.Id ?? _store.ActiveAccountId;
        FilteredAccounts.Clear();
        var query = (AccountSearchText ?? "").Trim();
        foreach (var account in Accounts)
        {
            if (string.IsNullOrEmpty(query)
                || account.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || account.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || account.LoginEmail.Contains(query, StringComparison.OrdinalIgnoreCase)
                || account.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase))
                FilteredAccounts.Add(account);
        }

        var restored = FilteredAccounts.FirstOrDefault(a => a.Id == selectedId)
                       ?? FilteredAccounts.FirstOrDefault(a => a.Id == _store.ActiveAccountId)
                       ?? FilteredAccounts.FirstOrDefault();
        if (restored is not null && SelectedAccount?.Id != restored.Id)
            SelectedAccount = restored;
        else if (restored is null && SelectedAccount is not null)
            SelectedAccount = null;
    }

    private void UpdateWorkspaceBindingSummary(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            WorkspaceBindingSummary = "账号绑定：未绑定";
            return;
        }

        var boundId = WorkspaceBindingService.ResolveAccountProfileId(root);
        if (string.IsNullOrWhiteSpace(boundId))
        {
            WorkspaceBindingSummary = "账号绑定：未绑定";
            return;
        }

        var account = FindAccountById(boundId);
        WorkspaceBindingSummary = account is null
            ? $"账号绑定：{boundId}"
            : $"账号绑定：{account.DisplayName}（{boundId}）";
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
        int generation;
        lock (_todayUploadRefreshLock)
        {
            _todayUploadRefreshScheduled = false;
            items = _pendingTodayUploadItems;
            accountId = _pendingTodayUploadAccountId;
            workspace = _pendingTodayUploadWorkspace;
            generation = _pendingTodayUploadGeneration;
        }

        _ = Task.Run(() =>
        {
            try
            {
                return TikTokTodayUploadCountService.CountTodayUploads(items, accountId, workspace);
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
                if (generation == _todayUploadRefreshGeneration)
                    TodayUploadCount = task.Result;
            });
        });
    }

    [RelayCommand]
    private void Login() => BeginAccountLogin(forceRelogin: false);

    [RelayCommand]
    private void Relogin() => BeginAccountLogin(forceRelogin: true);

    public void SetWorkspacePath(string path)
    {
        var workspace = NormalizeWorkspacePath(path);
        var active = SelectedAccount?.Model;
        if (active is null || string.IsNullOrWhiteSpace(workspace))
        {
            WorkspacePath = workspace;
            RefreshWorkspaceProjects(workspace);
            return;
        }

        active.LastWorkspace = workspace;
        active.TiktokUploadProfilePath = workspace;
        WorkspaceBindingService.Bind(workspace, active.Id, active.DisplayName);
        SaveAccountProfile(active);
        StatusMessage = $"工作目录已同步到「{active.DisplayName}」基础设置并自动保存：{workspace}";
    }

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
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
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
            return;
        }

        BindWorkspaceToSelectedAccountIfMissing(root);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _workspaceRefreshGeneration) return;
            if (TryGetWorkspaceQueueSnapshot(root, out var cachedItems, out var cachedOptions))
            {
                ApplyWorkspaceScanResult(root, cachedItems, cachedOptions);
                return;
            }

            if (!string.Equals(_displayedWorkspaceRoot, SafeFullPath(root), StringComparison.OrdinalIgnoreCase))
                ShowWorkspaceLoadingState(root);
        });

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
                CacheWorkspaceQueueSnapshot(root, terminalItems, finalOptions);
                _queueStatePersist.Enqueue(root, terminalItems, finalOptions);
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
                _queueItems = terminalItems.ToList();
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

    public bool IsWorkspaceQueueRunning(string workspaceRoot)
    {
        var normalized = Path.GetFullPath(workspaceRoot);
        return _queueOrchestrator.Snapshot().Any(item =>
            item.IsRunning &&
            string.Equals(Path.GetFullPath(item.WorkspaceRoot), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsWorkspaceQueueBusy(string workspaceRoot) =>
        IsWorkspaceQueueRunning(workspaceRoot) || IsWorkspaceQueueRunLifecycleActive(workspaceRoot);

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

        if (IsWorkspaceQueueRunning(root))
            items = PreserveDisplayedRuntimeState(items);

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
        string.IsNullOrWhiteSpace(workspaceRoot) ? "" : SafeFullPath(workspaceRoot.Trim());

    private void SetWorkspaceQueueExecutionContext(
        string workspaceRoot,
        string batchId,
        TikTokAccountProfile? account)
    {
        var root = NormalizeWorkspaceRootKey(workspaceRoot);
        if (string.IsNullOrWhiteSpace(root)) return;
        lock (_queueExecutionContextsLock)
            _queueExecutionContexts[root] = new WorkspaceQueueExecutionContext(batchId, account);
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
        TiktokProofSealPath = account.TiktokProofSealPath,
        TiktokProofAccountConfigMigrated = account.TiktokProofAccountConfigMigrated,
        TiktokAiRewriteSynopsis = account.TiktokAiRewriteSynopsis,
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
        if (replaceCollection)
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

    public IReadOnlyList<QueueProjectRowViewModel> SetFilteredCompletedQueueRowsEnabled()
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root)) return Array.Empty<QueueProjectRowViewModel>();

        var visibleRows = FilteredQueueProjectRows.ToArray();
        if (visibleRows.Length == 0)
        {
            StatusMessage = "没有可勾选的项目";
            return Array.Empty<QueueProjectRowViewModel>();
        }

        var visibleDirs = visibleRows
            .Select(row => NormalizeProjectDir(row.Item.ProjectDir))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var completedDirs = visibleRows
            .Where(row => row.Item.IsUploadCompleted)
            .Select(row => NormalizeProjectDir(row.Item.ProjectDir))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var changed = 0;
        foreach (var item in _queueItems)
        {
            var normalized = NormalizeProjectDir(item.ProjectDir);
            if (!visibleDirs.Contains(normalized)) continue;

            var enabled = completedDirs.Contains(normalized);
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
        StatusMessage = completedDirs.Count == 0
            ? "当前筛选结果中没有已完成项目"
            : $"已勾选 {completedDirs.Count} 个已完成项目";

        return FilteredQueueProjectRows
            .Where(row => completedDirs.Contains(NormalizeProjectDir(row.Item.ProjectDir)))
            .ToArray();
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

    public async Task<QueueProjectMoveResult> MoveQueueProjectsToAccountAsync(
        IEnumerable<QueueProjectRowViewModel> rows,
        AccountItemViewModel targetAccount)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("请先选择源账号工作目录。");
        if (targetAccount is null)
            throw new InvalidOperationException("请先选择目标账号。");
        if (IsCurrentWorkspaceQueueRunning())
            throw new InvalidOperationException("当前工作目录队列正在运行，请停止后再移动项目。");

        var selectedItems = rows
            .Where(row => row is not null && !string.IsNullOrWhiteSpace(row.Item.ProjectDir))
            .GroupBy(row => NormalizeProjectDir(row.Item.ProjectDir), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().Item)
            .ToArray();
        if (selectedItems.Length == 0)
            throw new InvalidOperationException("请先勾选或选中要移动的项目。");

        _queueStatePersist.Flush(root, TimeSpan.FromMilliseconds(400));
        var result = await Task.Run(() =>
            WorkspaceQueueService.MoveProjectsToAccountWorkspace(
                root,
                selectedItems,
                targetAccount.Model)).ConfigureAwait(true);

        var targetRoot = targetAccount.Model.ResolveWorkspacePath();
        var sameWorkspace = string.Equals(
            NormalizeWorkspaceRootKey(root),
            NormalizeWorkspaceRootKey(targetRoot),
            StringComparison.OrdinalIgnoreCase);

        var sourceScan = await Task.Run(() =>
            (
                Items: WorkspaceQueueService.ScanProjects(root).ToList(),
                Options: WorkspaceQueueService.LoadRunOptions(root))).ConfigureAwait(true);
        ApplyWorkspaceScanResult(root, sourceScan.Items, sourceScan.Options);

        if (!sameWorkspace && !string.IsNullOrWhiteSpace(targetRoot))
        {
            var targetScan = await Task.Run(() =>
                (
                    Items: WorkspaceQueueService.ScanProjects(targetRoot).ToList(),
                    Options: WorkspaceQueueService.LoadRunOptions(targetRoot))).ConfigureAwait(true);
            CacheWorkspaceQueueSnapshot(targetRoot, targetScan.Items, targetScan.Options);
        }

        AutoExportQueueExcelForWorkspace(root);
        if (!sameWorkspace && !string.IsNullOrWhiteSpace(targetRoot))
            AutoExportQueueExcelForWorkspace(targetRoot);

        StatusMessage = $"已移动 {result.Count} 个项目到「{targetAccount.DisplayName}」";
        AppendLog(StatusMessage);
        return result;
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
            throw new InvalidOperationException("请先选择工作目录。");
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
        WorkspaceQueueTarget? targetOverride = null)
    {
        var target = targetOverride ?? CaptureCurrentWorkspaceQueueTarget();
        if (target is null || string.IsNullOrWhiteSpace(target.WorkspaceRoot))
            return null;

        var root = Path.GetFullPath(target.WorkspaceRoot);
        var displayedWorkspace = WorkspacePath.Trim();
        var isDisplayedWorkspace = !string.IsNullOrWhiteSpace(displayedWorkspace) &&
            string.Equals(
                Path.GetFullPath(displayedWorkspace),
                root,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                SafeFullPath(_displayedWorkspaceRoot),
                root,
                StringComparison.OrdinalIgnoreCase);
        projectDirFilter = target.ProjectDirFilter ?? projectDirFilter;
        var displayedSnapshot = isDisplayedWorkspace && _queueItems.Count > 0
            ? _queueItems
            : null;
        var queueItemsForRun = WorkspaceQueueService.ResolveExecutionSnapshot(
            root,
            displayedSnapshot,
            target.PreferPersistedQueueSnapshot).ToList();
        if (queueItemsForRun.Count == 0)
        {
            if (projectDirFilter is not null)
                throw new InvalidOperationException($"目标工作目录队列为空，未匹配到本次导入项目：{root}");
            return null;
        }

        // 并行多账号时 SelectedAccount 会随 UI 切换，故优先使用本次执行目标账号，
        // 其次使用工作目录绑定；只有当前页面直接执行时才回退到当前选中账号。
        var boundId = WorkspaceBindingService.ResolveAccountProfileId(root);
        var boundAccount = FindAccountById(boundId);
        var requestedTargetAccountId = (target.AccountProfileId ?? "").Trim();
        var targetAccount = FindAccountById(requestedTargetAccountId);
        if (!string.IsNullOrWhiteSpace(requestedTargetAccountId) && targetAccount is null)
        {
            throw new InvalidOperationException(
                $"本次队列指定的账号已删除或不存在：{requestedTargetAccountId}。请重新选择账号后执行。");
        }

        var effectiveAccount = targetAccount ?? boundAccount ?? (isDisplayedWorkspace ? SelectedAccount : null);
        if (effectiveAccount is not null)
        {
            if (string.IsNullOrWhiteSpace(boundId) ||
                boundAccount is null ||
                (targetAccount is not null && boundAccount.Id != targetAccount.Id))
            {
                WorkspaceBindingService.Bind(root, effectiveAccount.Id, effectiveAccount.DisplayName);
                if (isDisplayedWorkspace)
                    UpdateWorkspaceBindingSummary(root);
            }
        }

        var runOptions = optionsOverride?.Clone() ?? (isDisplayedWorkspace
            ? BuildQueueRunOptionsFromUi()
            : LoadQueueRunOptionsForAccountWorkspace(root, effectiveAccount?.Model));

        IReadOnlyList<string>? normalizedProjectFilter = null;
        HashSet<string>? normalizedProjectFilterSet = null;
        if (projectDirFilter is not null)
        {
            normalizedProjectFilter = projectDirFilter
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (normalizedProjectFilter.Count == 0)
                throw new InvalidOperationException("执行项目筛选为空，已取消启动队列。");
            normalizedProjectFilterSet = normalizedProjectFilter.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var runItems = queueItemsForRun.Where(item =>
            item.Enabled &&
            !item.Archived &&
            (normalizedProjectFilterSet is null || normalizedProjectFilterSet.Contains(Path.GetFullPath(item.ProjectDir))))
            .ToArray();
        if (runItems.Length == 0)
            throw new InvalidOperationException($"目标工作目录未匹配到可执行项目，已取消启动队列：{root}");

        // 只有拥有明确执行账号的编排层才允许迁移失效绑定；Core Runner 本身保持严格，
        // 不会把找不到的账号静默回退到当前活动账号。并且只修复本次实际执行的项目。
        var repairedAccountBindingCount = 0;
        if (effectiveAccount is not null)
        {
            foreach (var item in runItems)
            {
                if (!QueueAccountBindingResolver.RepairForWorkspaceDefault(
                        _store,
                        item,
                        effectiveAccount.Model))
                {
                    continue;
                }

                repairedAccountBindingCount++;
                if (isDisplayedWorkspace)
                    RefreshQueueRowFor(item);
            }

            if (repairedAccountBindingCount > 0)
            {
                AppendLog($"检测到账号删除、重建或重命名，已校正 {repairedAccountBindingCount} 个队列项目的账号绑定。");
            }
        }

        if (optionsOverride is null && isDisplayedWorkspace)
            _queueStatePersist.Enqueue(root, queueItemsForRun, _queueRunOptions);
        else if (repairedAccountBindingCount > 0)
            _queueStatePersist.Enqueue(root, queueItemsForRun, runOptions);
        if (repairedAccountBindingCount > 0)
            _queueStatePersist.Flush(root, TimeSpan.FromSeconds(1));
        CacheWorkspaceQueueSnapshot(root, queueItemsForRun, runOptions);
        MarkQueueExcelExportPending(root);

        var selectedRunCount = QueueWorkerRunner.ValidateRunSelection(
            queueItemsForRun,
            runOptions,
            _store,
            normalizedProjectFilter);

        Logs.ClearProjectEntries(runItems);
        RefreshRunningWorkspacesSummary();
        var batchId = TikTokExecutionHistoryService.NewBatchId();
        var totalCount = selectedRunCount;
        var enabledSteps = runOptions.OrderedEnabledSteps();
        var projectConcurrency = runOptions.ProjectConcurrency;
        var uploadEntryMode = runOptions.UploadEntryMode;
        var account = effectiveAccount?.Model;
        var finalAction = target.FinalActionOverride ?? SelectedFinalAction?.Value ?? FinalAction.None;
        var label = string.IsNullOrWhiteSpace(target.DisplayLabel)
            ? $"{effectiveAccount?.DisplayName ?? "当前账号"} · {root}"
            : target.DisplayLabel;
        SetWorkspaceQueueExecutionContext(root, batchId, account);
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
        var runLifecycle = RegisterWorkspaceQueueRunLifecycle(root);
        try
        {
            var summary = await _queueOrchestrator.RunWorkspaceAsync(
                root,
                queueItemsForRun,
                runOptions,
                host,
                _store,
                finalAction,
                label,
                progress =>
                {
                    onProgress(progress);
                    RefreshRunningWorkspacesSummary();
                },
                (workspaceRoot, items) =>
                {
                    if (string.Equals(Path.GetFullPath(workspaceRoot), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                        onPersist(workspaceRoot, items);
                },
                ct,
                normalizedProjectFilter,
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
                CompleteWorkspaceQueueRunLifecycle(runLifecycle);
                RemoveWorkspaceQueueExecutionContext(root, batchId);
            }
        }
    }

    public async Task<IReadOnlyList<QueueWorkerSummary?>> RunAllAccountWorkspaceQueuesAsync(
        IQueuePublishHost host,
        Action<QueueWorkerProgress> onProgress,
        Action<string, IReadOnlyList<QueueProjectItem>> onPersist,
        CancellationToken ct,
        IReadOnlyList<WorkspaceQueueTarget>? targetsOverride = null,
        QueueRunOptions? optionsOverride = null)
    {
        var targets = targetsOverride ?? BuildAccountWorkspaceTargets();
        if (targets.Count == 0)
            return Array.Empty<QueueWorkerSummary?>();

        if (!_queueOrchestrator.AnyRunning)
            Logs.ClearAllEntries();
        if (targetsOverride is null)
        {
            SyncEnabledStepsFromUi();
            _queueStatePersist.Enqueue(WorkspacePath, _queueItems, _queueRunOptions);
            PersistAccountQueueSettings();
        }

        var targetOptionsByRoot = new Dictionary<string, QueueRunOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            var account = FindAccountById(target.AccountProfileId)?.Model;
            QueueRunOptions targetOptions;
            if (optionsOverride is not null)
            {
                targetOptions = optionsOverride.Clone();
                targetOptions.ProjectConcurrency = Math.Clamp(
                    account?.TiktokProjectConcurrency ?? targetOptions.ProjectConcurrency,
                    1,
                    20);
            }
            else if (account?.Id == SelectedAccount?.Id &&
                     string.Equals(
                         Path.GetFullPath(target.WorkspaceRoot),
                         Path.GetFullPath(WorkspacePath),
                         StringComparison.OrdinalIgnoreCase))
            {
                targetOptions = BuildQueueRunOptionsFromUi();
            }
            else
            {
                targetOptions = LoadQueueRunOptionsForAccountWorkspace(target.WorkspaceRoot, account);
            }

            var items = WorkspaceQueueService.ScanProjects(target.WorkspaceRoot);
            QueueWorkerRunner.ValidateRunSelection(items, targetOptions, _store, target.ProjectDirFilter);
            CacheWorkspaceQueueOptions(target.WorkspaceRoot, targetOptions);
            targetOptionsByRoot[Path.GetFullPath(target.WorkspaceRoot)] = targetOptions;
        }

        foreach (var target in targets)
            MarkQueueExcelExportPending(target.WorkspaceRoot);
        RefreshRunningWorkspacesSummary();
        var batchId = TikTokExecutionHistoryService.NewBatchId();
        foreach (var target in targets)
        {
            var account = FindAccountById(target.AccountProfileId)?.Model;
            SetWorkspaceQueueExecutionContext(target.WorkspaceRoot, batchId, account);
            TikTokExecutionHistoryService.AppendEvent(
                "run_started",
                "running",
                target.WorkspaceRoot,
                batchId: batchId,
                message: "多工作目录队列开始执行",
                metadata: new Dictionary<string, object?> { ["display_label"] = target.DisplayLabel },
                account: account);
        }

        var runLifecycles = targets
            .Select(target => RegisterWorkspaceQueueRunLifecycle(target.WorkspaceRoot))
            .ToArray();
        try
        {
            var fallbackFinalAction = SelectedFinalAction?.Value ?? FinalAction.None;
            return await _queueOrchestrator.RunWorkspacesAsync(
                targets,
                host,
                _store,
                fallbackFinalAction,
                target => targetOptionsByRoot[Path.GetFullPath(target.WorkspaceRoot)].Clone(),
                progress =>
                {
                    onProgress(progress);
                    RefreshRunningWorkspacesSummary();
                },
                onPersist,
                ct);
        }
        finally
        {
            try
            {
                RefreshRunningWorkspacesSummary();
                await RefreshWorkspaceProjectsAfterQueueRunAsync(WorkspacePath)
                    .ConfigureAwait(true);
                RefreshLogSnapshot();
            }
            finally
            {
                await Task.WhenAll(runLifecycles.Select(lifecycle =>
                        WaitForWorkspaceQueueStatePersistAsync(lifecycle.WorkspaceRoot)))
                    .ConfigureAwait(true);
                foreach (var lifecycle in runLifecycles)
                {
                    CompleteWorkspaceQueueRunLifecycle(lifecycle);
                    RemoveWorkspaceQueueExecutionContext(lifecycle.WorkspaceRoot, batchId);
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

        var root = Path.GetFullPath(workspace);
        var account = SelectedAccount;
        return new WorkspaceQueueTarget(
            root,
            $"{account?.DisplayName ?? "当前账号"} · {root}",
            account?.Id,
            FinalActionOverride: SelectedFinalAction?.Value ?? FinalAction.None);
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
        if (string.IsNullOrEmpty(root))
            return false;

        var normalized = Path.GetFullPath(root);
        return _queueOrchestrator.Snapshot().Any(item =>
            item.IsRunning &&
            string.Equals(Path.GetFullPath(item.WorkspaceRoot), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public void RequestStopQueue(string? workspaceRoot = null)
    {
        var root = (workspaceRoot ?? WorkspacePath).Trim();
        if (!string.IsNullOrEmpty(root))
        {
            _queueOrchestrator.StopWorkspace(root);
        }
        else
        {
            _queueOrchestrator.StopAll();
            _queueCts?.Cancel();
        }

        // 各次运行的 finally 会调用 EndQueueRun 做引用计数清理，这里只需刷新状态展示。
        RefreshRunningWorkspacesSummary();
    }

    public async Task<TikTokRemoteCommandResult> ExecuteRemoteCommandAsync(TikTokRemoteCommand command)
    {
        try
        {
            return command.Command switch
            {
                TikTokRemoteCommandNames.ShowHelpText =>
                    TikTokRemoteCommandResult.Success(command.Command, ClientSettingsStore.Load().FeishuCommandHelpText),
                TikTokRemoteCommandNames.ShowHelpCard =>
                    TikTokRemoteCommandResult.SuccessCard(
                        command.Command,
                        "飞书 TikTok 上传命令卡片教程",
                        TikTokRemoteHelpCardBuilder.BuildCommandTutorialCardJson()),
                TikTokRemoteCommandNames.QueryStatus =>
                    TikTokRemoteCommandResult.Success(command.Command, BuildRemoteRuntimeStatusText()),
                TikTokRemoteCommandNames.StopQueue => ExecuteRemoteStopQueue(command),
                TikTokRemoteCommandNames.SwitchAccountProfile => ExecuteRemoteSwitchAccount(command),
                TikTokRemoteCommandNames.StartQueue => await ExecuteRemoteStartQueueAsync(command),
                TikTokRemoteCommandNames.UploadSeries => await ExecuteRemoteUploadSeriesAsync(command),
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
        if (!IsQueueRunning)
            return TikTokRemoteCommandResult.Success(command.Command, "当前没有运行中的 TikTok 队列。");

        _queueOrchestrator.StopAll();
        _queueCts?.Cancel();
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
            return TikTokRemoteCommandResult.Failed(command.Command, "当前已有 TikTok 队列在执行，请等待完成后再发起新任务。");
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
        var titles = command.Titles?.Where(title => !string.IsNullOrWhiteSpace(title)).ToList() ?? [];
        if (titles.Count == 0)
            return TikTokRemoteCommandResult.Failed(command.Command, "未提供可上传的 TikTok 剧名。");
        if (command.HasMultiAccountSelection)
        {
            // 多账号远程运行仍沿用原有的全局互斥语义；单账号命令则可以追加到目标工作目录的运行队列。
            if (IsQueueRunning)
                return TikTokRemoteCommandResult.Failed(command.Command, "当前已有 TikTok 队列在执行，请等待完成后再发起新任务。");
            return await ExecuteRemoteUploadSeriesMultiAccountAsync(command, titles);
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
        var targetAccount = SelectedAccount?.Model;
        var finalAction = SelectedFinalAction?.Value ?? FinalAction.None;
        var runQueueRequested = RemoteQueueRunRequested;
        ActivateRemoteWorkspace(targetWorkspace);
        if (targetAccount is not null)
            WorkspaceBindingService.Bind(targetWorkspace, targetAccount.Id, targetAccount.DisplayName);

        var options = SystemServices.BuildRemoteUploadRunOptions(command);
        options.ProjectConcurrency = Math.Clamp(targetAccount?.TiktokProjectConcurrency ?? options.ProjectConcurrency, 1, 20);

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
            CancellationToken.None);

        var result = importOutcome.ImportResult;
        if (result.ProjectDirs.Count == 0)
        {
            var duplicateSuffix = result.Duplicates.Count > 0 ? $"，重复 {result.Duplicates.Count} 个" : "";
            var failurePreview = result.Failures.Count > 0
                ? UploadTitleImportService.BuildFailurePreview(result.Failures)
                : "没有匹配到可执行项目。";
            return result.Duplicates.Count > 0 && result.Failures.Count == 0
                ? TikTokRemoteCommandResult.Success(command.Command, $"没有需要上传的新剧（全部已存在）{duplicateSuffix}。")
                : TikTokRemoteCommandResult.Failed(command.Command, $"TikTok 剧集导入失败：{failurePreview}{duplicateSuffix}");
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
            return TikTokRemoteCommandResult.Failed(command.Command, failureText);
        }

        if (IsWorkspaceQueueRunning(targetWorkspace))
        {
            const string failureText = "远程上传任务已生成项目目录，但运行中的目标队列未接纳新项目，且队列尚未结束；请稍后重试。";
            StatusMessage = failureText;
            AppendLog(failureText);
            return TikTokRemoteCommandResult.Failed(command.Command, failureText);
        }

        if (applyOutcome.QueueWasRunning)
        {
            // 运行分支刻意没有落盘；只有确认旧 runner 已退出后，才把本次项目加入持久队列并准备启动。
            var idlePreparation = PrepareRemoteUploadProjectsWhenIdle(
                targetWorkspace,
                applyOutcome.OrderedProjectDirs,
                targetAccount);
            ApplyRemoteUploadIdlePreparationToDisplayedWorkspace(targetWorkspace, idlePreparation);
            applyOutcome = applyOutcome with
            {
                QueueWasRunning = false,
                AppendCandidates = idlePreparation.AppendCandidates,
            };
        }

        if (IsWorkspaceQueueRunning(targetWorkspace))
        {
            var lateAppended = _queueOrchestrator.TryAppendItemsToRunningWorkspace(
                targetWorkspace,
                applyOutcome.AppendCandidates);
            if (lateAppended > 0)
                return BuildAppendAcceptedResult(lateAppended);

            const string failureText = "远程上传任务已导入，但目标队列被其它操作抢先启动且未接纳新项目；未重复启动队列。";
            StatusMessage = failureText;
            AppendLog(failureText);
            return TikTokRemoteCommandResult.Failed(command.Command, failureText);
        }

        if (runQueueRequested is null)
            return TikTokRemoteCommandResult.Failed(command.Command, "剧集已导入，但队列视图尚未初始化，无法启动 TikTok 队列。");

        var started = await runQueueRequested.Invoke(importOutcome.RunTarget, options);
        if (!started)
        {
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
                CancellationToken.None);
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
                $"飞书多账号上传任务已导入：{totalAppended} 个项目已追加到运行中的目标队列。"
                + (string.IsNullOrWhiteSpace(multiAuthorExcludeNotice) ? "" : $" {multiAuthorExcludeNotice}"));
        }

        if (RemoteAllQueueRunRequested is null)
            return TikTokRemoteCommandResult.Failed(command.Command, "剧集已导入，但队列视图尚未初始化，无法启动 TikTok 多账号队列。");

        var options = SystemServices.BuildRemoteUploadRunOptions(command);
        var started = await RemoteAllQueueRunRequested.Invoke(options, runTargets);
        if (!started)
            return TikTokRemoteCommandResult.Failed(command.Command, "TikTok 多账号剧集已导入，但目标队列未启动，请查看运行日志。");
        return TikTokRemoteCommandResult.Accepted(
            command.Command,
            $"飞书多账号上传任务已导入并启动队列：{runTargets.Count} 个工作目录，加入执行 {totalQueued} 个，已追加 {totalAppended} 个，未导入 {totalFailed} 个。"
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

    private bool TryResolveRemoteWorkspace(TikTokRemoteCommand command, out string workspace, out string error)
    {
        var settings = ClientSettingsStore.Load();
        var selectedAccountWorkspace = SelectedAccount?.Model.ResolveWorkspacePath() ?? "";
        var candidates = command.HasExplicitAccountSelection
            ? new[]
            {
                command.WorkspacePath,
                selectedAccountWorkspace,
                settings.FeishuCommandDefaultWorkspace,
                WorkspacePath,
            }
            : new[]
            {
                command.WorkspacePath,
                settings.FeishuCommandDefaultWorkspace,
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
        error = "未找到可用工作目录，请先在系统服务中配置飞书默认工作目录，或在命令中指定工作目录。";
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
        else if (!string.IsNullOrWhiteSpace(workspace))
            selected = WorkspaceBindingService.ResolveAccountProfileId(workspace) ?? "";

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

    private WorkspaceQueueRunLifecycle RegisterWorkspaceQueueRunLifecycle(string workspace)
    {
        var lifecycle = new WorkspaceQueueRunLifecycle(NormalizeWorkspacePath(workspace));
        lock (_workspaceQueueRunLifecyclesLock)
        {
            if (!_workspaceQueueRunLifecycles.TryGetValue(lifecycle.WorkspaceRoot, out var active))
            {
                active = [];
                _workspaceQueueRunLifecycles[lifecycle.WorkspaceRoot] = active;
            }

            active.Add(lifecycle);
        }

        return lifecycle;
    }

    private void CompleteWorkspaceQueueRunLifecycle(WorkspaceQueueRunLifecycle lifecycle)
    {
        lock (_workspaceQueueRunLifecyclesLock)
        {
            if (_workspaceQueueRunLifecycles.TryGetValue(lifecycle.WorkspaceRoot, out var active))
            {
                active.Remove(lifecycle);
                if (active.Count == 0)
                    _workspaceQueueRunLifecycles.Remove(lifecycle.WorkspaceRoot);
            }
        }

        lifecycle.Completion.TrySetResult(true);
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

    private Task[] SnapshotWorkspaceQueueRunLifecycleTasks(string workspace)
    {
        var root = NormalizeWorkspacePath(workspace);
        lock (_workspaceQueueRunLifecyclesLock)
        {
            return _workspaceQueueRunLifecycles.TryGetValue(root, out var active)
                ? active.Select(lifecycle => lifecycle.Completion.Task).ToArray()
                : [];
        }
    }

    private bool IsWorkspaceQueueRunLifecycleActive(string workspace) =>
        SnapshotWorkspaceQueueRunLifecycleTasks(workspace).Length > 0;

    private async Task<bool> WaitForWorkspaceQueueToFinishClosingAsync(string workspace, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var lifecycleTasks = SnapshotWorkspaceQueueRunLifecycleTasks(workspace);
            if (!IsWorkspaceQueueRunning(workspace) && lifecycleTasks.Length == 0)
                return true;

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return false;

            var delay = Task.Delay(remaining < TimeSpan.FromMilliseconds(50)
                ? remaining
                : TimeSpan.FromMilliseconds(50));
            if (lifecycleTasks.Length == 0)
            {
                await delay.ConfigureAwait(true);
                continue;
            }

            await Task.WhenAny(Task.WhenAll(lifecycleTasks), delay).ConfigureAwait(true);
        }
    }

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

    public CancellationToken BeginQueueRun()
    {
        // 支持多账号/多工作目录并行：已有运行中的队列时复用同一 CTS，不得 Dispose（会破坏前一个队列的停止）。
        if (_queueCts is null || _queueCts.IsCancellationRequested)
        {
            _queueCts?.Dispose();
            _queueCts = new CancellationTokenSource();
        }

        _activeQueueRunCount++;
        _queueRunActive = true;
        _dailyLimitNotified = false;
        RefreshRunningWorkspacesSummary();
        return _queueCts.Token;
    }

    public void EndQueueRun()
    {
        var finishedAllRuns = false;
        _activeQueueRunCount = Math.Max(0, _activeQueueRunCount - 1);
        if (_activeQueueRunCount == 0)
        {
            _queueCts?.Dispose();
            _queueCts = null;
            finishedAllRuns = true;
        }

        if (finishedAllRuns)
            ScheduleFinalQueueExcelExport();
        RefreshRunningWorkspacesSummary();
    }

    public void HandleQueueWorkerProgress(QueueWorkerProgress progress)
    {
        NotifyDailyLimitIfPresent(progress.Message);
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
                    account: executionContext?.Account));
            }

            return;
        }

        if (progress.Item is not null)
            RefreshQueueRowFor(progress.Item);

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
        if (string.IsNullOrEmpty(root) || string.IsNullOrWhiteSpace(workspaceRoot))
            return true;
        return string.Equals(
            NormalizePathCached(workspaceRoot),
            NormalizePathCached(root),
            StringComparison.OrdinalIgnoreCase);
    }

    private void NotifyDailyLimitIfPresent(string? message)
    {
        if (_dailyLimitNotified)
            return;
        var text = message ?? "";
        if (!text.Contains("单日创建剧集上限", StringComparison.Ordinal) ||
            !text.Contains("已停止", StringComparison.Ordinal))
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
        if (QueueStepLogFilters.RequiresLosslessUiDelivery(progress.StepKey)
            || message.Contains("失败", StringComparison.Ordinal)
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

        CacheWorkspaceQueueSnapshot(root, items);
        _queueStatePersist.Enqueue(root, items.ToList(), ResolveQueueOptionsForPersistedWorkspace(root));

        if (!IsActiveWorkspace(root))
            return;

        _queueItems = items.ToList();
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
        if (string.IsNullOrEmpty(root)) return;
        _queueItems = items.ToList();
        _queueStatePersist.Enqueue(root, _queueItems, _queueRunOptions);
        if (refreshRows)
            RefreshQueueRowViewModels();
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

    public async Task<QueueProjectTitleRenameResult> RenameQueueProjectNewTitleAsync(
        QueueProjectRowViewModel row,
        string newTitle)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("请先选择工作目录。");
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
        if (string.IsNullOrEmpty(root)) return;
        _queueStatePersist.Enqueue(root, _queueItems, ClonePersistentQueueRunOptions(_queueRunOptions));
    }

    private void ApplyQueueStepTogglesFromOptions()
    {
        _applyingQueueStepToggles = true;
        try
        {
            QueueDownloadEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.Download);
            QueueRewriteEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.RewriteInfo);
            QueueGeneratePosterEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.GeneratePoster);
            QueueGenerateProofMaterialEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.GenerateProofMaterial);
            QueueDeleteSourceVideosEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.DeleteSourceVideos);
            QueueSmallVideoRepairEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.SmallVideoRepair);
            QueueVideoTranslateEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.VideoTranslate);
            QueueSilenceDetectEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.SilenceDetect);
            QueueSilenceRepairEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.SilenceRepair);
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
        if (QueueRewriteEnabled) steps.Add(QueueStepRegistry.RewriteInfo);
        if (QueueGeneratePosterEnabled) steps.Add(QueueStepRegistry.GeneratePoster);
        if (QueueGenerateProofMaterialEnabled) steps.Add(QueueStepRegistry.GenerateProofMaterial);
        if (QueueDeleteSourceVideosEnabled) steps.Add(QueueStepRegistry.DeleteSourceVideos);
        if (QueueSmallVideoRepairEnabled) steps.Add(QueueStepRegistry.SmallVideoRepair);
        if (QueueVideoTranslateEnabled) steps.Add(QueueStepRegistry.VideoTranslate);
        if (QueueSilenceDetectEnabled) steps.Add(QueueStepRegistry.SilenceDetect);
        if (QueueSilenceRepairEnabled) steps.Add(QueueStepRegistry.SilenceRepair);
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

    private void BindWorkspaceToSelectedAccountIfMissing(string workspace)
    {
        var account = SelectedAccount?.Model;
        if (account is null || string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
            return;

        var boundId = WorkspaceBindingService.ResolveAccountProfileId(workspace);
        if (!string.IsNullOrWhiteSpace(boundId) && FindAccountById(boundId) is not null)
            return;

        WorkspaceBindingService.Bind(workspace, account.Id, account.DisplayName);
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
            RefreshWorkspaceFromActiveAccount();
        AccountProfileNetworkChanged?.Invoke(profile);
    }

    public void ImportDramaProjectsToQueue(IReadOnlyList<string> projectDirs)
    {
        var root = ResolveSelectedAccountWorkspacePath();
        if (string.IsNullOrEmpty(root))
        {
            StatusMessage = "请先为左侧选择账号配置上传工作目录";
            return;
        }

        var account = SelectedAccount?.Model;
        if (account is not null)
            WorkspaceBindingService.Bind(root, account.Id, account.DisplayName);

        if (!string.Equals(NormalizeWorkspacePath(WorkspacePath), root, StringComparison.OrdinalIgnoreCase))
        {
            WorkspacePath = root;
            SystemSettings.UpdateWorkspacePath(root);
            ArchivedProjects.SetWorkspace(root, refresh: false);
        }

        var added = WorkspaceQueueService.AddProjectsToQueue(root, projectDirs);
        RefreshWorkspaceProjects(root, force: true);
        var accountName = account?.DisplayName ?? "当前账号";
        StatusMessage = added.Count > 0 ? $"已导入 {added.Count} 个项目到「{accountName}」上传队列" : "没有可导入的项目";
        AppendLog(StatusMessage);
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
        if (account is not null)
            WorkspaceBindingService.Bind(root, account.Id, account.DisplayName);

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
            result = await Task.Run(() => LocalManualDramaImportService.Import(root, sourceProjectDir, AppendLog))
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
        if (account is not null)
            WorkspaceBindingService.Bind(root, account.Id, account.DisplayName);

        if (!string.Equals(NormalizeWorkspacePath(WorkspacePath), root, StringComparison.OrdinalIgnoreCase))
        {
            WorkspacePath = root;
            SystemSettings.UpdateWorkspacePath(root);
            ArchivedProjects.SetWorkspace(root, refresh: false);
        }

        var existingBefore = WorkspaceQueueService.ScanProjects(root)
            .Select(item => Path.GetFullPath(item.ProjectDir))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                var result = await Task.Run(() => LocalManualDramaImportService.Import(root, sourceProjectDir, AppendLog))
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
            StatusMessage = "请先选择工作目录";
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
            StatusMessage = "请先选择工作目录";
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
        CancellationToken ct)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var settings = ClientSettingsStore.Load();
        var result = await UploadTitleImportService.ImportAsync(
            root,
            rawText,
            settings,
            account,
            episodeMin,
            episodeMax,
            matchMode,
            AppendLog,
            ct,
            addProjectsToQueue: false);

        var applyOutcome = ApplyRemoteUploadTitleImportResult(
            result,
            root,
            account,
            allowAppendToRunningQueue);

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
        bool allowAppendToRunningQueue)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var orderedProjectDirs = BuildOrderedDistinctProjectDirs(result.ProjectDirs);
        if (account is not null)
            WorkspaceBindingService.Bind(root, account.Id, account.DisplayName);

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
                ? BuildRemoteAppendCandidates(orderedProjectDirs, account)
                : [];
            var appendedCount = allowAppendToRunningQueue
                ? _queueOrchestrator.TryAppendItemsToRunningWorkspace(root, appendCandidates)
                : 0;
            return new UploadTitleImportApplyOutcome(
                orderedProjectDirs,
                QueueWasRunning: true,
                appendCandidates,
                appendedCount);
        }

        var preparation = PrepareRemoteUploadProjectsWhenIdle(root, orderedProjectDirs, account);
        ApplyRemoteUploadIdlePreparationToDisplayedWorkspace(root, preparation);
        return new UploadTitleImportApplyOutcome(
            orderedProjectDirs,
            QueueWasRunning: false,
            preparation.AppendCandidates,
            AppendedCount: 0);
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

    private static RemoteUploadIdlePreparation PrepareRemoteUploadProjectsWhenIdle(
        string workspaceRoot,
        IReadOnlyList<string> orderedProjectDirs,
        TikTokAccountProfile? account)
    {
        WorkspaceQueueService.AddProjectsToQueue(workspaceRoot, orderedProjectDirs);
        var items = WorkspaceQueueService.ScanProjects(workspaceRoot).ToList();
        var runOptions = WorkspaceQueueService.LoadRunOptions(workspaceRoot);
        var byProjectDir = items
            .Where(item => !string.IsNullOrWhiteSpace(item.ProjectDir))
            .GroupBy(item => Path.GetFullPath(item.ProjectDir), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var appendCandidates = new List<QueueProjectItem>();

        foreach (var projectDir in orderedProjectDirs)
        {
            if (!byProjectDir.TryGetValue(projectDir, out var item))
                continue;

            item.Enabled = true;
            ResetQueueItemToPending(item);
            if (account is not null)
            {
                item.AccountProfileId = account.Id;
                item.AccountProfileName = account.DisplayName;
            }
            appendCandidates.Add(item);
        }

        if (appendCandidates.Count > 0)
            WorkspaceQueueService.SaveRunOptions(workspaceRoot, items, runOptions);

        return new RemoteUploadIdlePreparation(items, appendCandidates, runOptions);
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
        CancellationToken ct)
    {
        var root = target.WorkspaceRoot.Trim();
        if (string.IsNullOrWhiteSpace(root))
        {
            StatusMessage = "请先选择工作目录";
            return null;
        }

        if (string.IsNullOrWhiteSpace(rawText))
        {
            StatusMessage = "请先输入短剧名称";
            return null;
        }

        var account = FindAccountById(target.AccountProfileId)?.Model;
        return await ImportRemoteUploadTitlesAsync(
            Path.GetFullPath(root),
            account,
            rawText,
            episodeMin,
            episodeMax,
            matchMode,
            target.FinalActionOverride,
            allowAppendToRunningQueue: true,
            ct).ConfigureAwait(true);
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
            StatusMessage = "请先选择工作目录";
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

        // 导入短剧是明确的“用当前账号处理这批短剧”操作：强制把工作目录与导入项目绑定到当前账号，
        // 覆盖此前用其它账号跑过留下的残留绑定，避免账号槽错乱、上传到错误账号。
        var importAccount = SelectedAccount?.Model;
        if (importAccount is not null)
            WorkspaceBindingService.Bind(root, importAccount.Id, importAccount.DisplayName);

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

    public async Task DeleteSelectedQueueProjectsAsync(IEnumerable<QueueProjectRowViewModel> selectedRows)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root))
        {
            StatusMessage = "请先选择工作目录";
            return;
        }

        var rows = selectedRows.ToArray();
        if (rows.Length == 0)
        {
            StatusMessage = "请先选中要删除的项目";
            return;
        }

        var errors = new List<string>();
        var deleted = 0;
        var queuedDirs = rows.Select(row => row.Item.ProjectDir).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        var deleteAccount = SelectedAccount?.Model;
        foreach (var row in rows)
        {
            // Persist a durable recovery snapshot before the source/workflow directories
            // disappear. The snapshot is intentionally retained when normal history events
            // are pruned, allowing copyright-proof completion after an unarchived deletion.
            TikTokExecutionHistoryService.AppendEvent(
                "project_deleted",
                "deleted",
                root,
                row.Item,
                message: "用户删除未归档项目，已保存版权恢复快照",
                account: deleteAccount);
        }

        await Task.Run(() =>
        {
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                try
                {
                    var context = ProjectWorkspaceService.LoadContext(row.Item.ProjectDir);
                    AddDeleteTarget(context.SourceProjectDir, root, targets, errors);
                    AddDeleteTarget(context.WorkflowProjectDir, root, targets, errors);
                }
                catch (Exception ex)
                {
                    errors.Add($"{row.Item.DisplayName}: {ex.Message}");
                }
            }

            foreach (var target in targets.OrderByDescending(path => path.Length))
            {
                try
                {
                    if (!Directory.Exists(target)) continue;
                    Directory.Delete(target, recursive: true);
                    deleted++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{target}: {ex.Message}");
                }
            }
        });

        if (queuedDirs.Length > 0)
            WorkspaceQueueService.RemoveProjectsFromQueue(root, queuedDirs);
        RefreshWorkspaceProjects(root);
        StatusMessage = errors.Count == 0
            ? $"已删除 {deleted} 个项目目录"
            : $"已删除 {deleted} 个项目目录，{errors.Count} 个失败";
        AppendLog(StatusMessage);
        foreach (var error in errors.Take(5))
            AppendLog($"删除失败：{error}");
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
    {
        var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

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
            throw new InvalidOperationException("请先选择工作目录");
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

            foreach (var archive in TikTokArchivedProjectService.List(workspaceRoot))
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

    private sealed class WorkspaceQueueRunLifecycle(string workspaceRoot)
    {
        public string WorkspaceRoot { get; } = workspaceRoot;
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
