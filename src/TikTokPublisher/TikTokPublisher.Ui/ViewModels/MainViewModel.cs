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

public sealed partial class MainViewModel : ViewModelBase
{
    public const string TikTokLoginUrl = TikTokUrls.DefaultLoginUrl;

    private readonly AccountStore _store;
    private readonly AccountContextService _context;
    private readonly WorkspaceQueueOrchestrator _queueOrchestrator = new();
    private readonly QueueStatePersistService _queueStatePersist = new();
    private CancellationTokenSource? _queueCts;
    private string? _manualInterventionWorkspaceRoot;
    private bool _applyingQueueStepToggles;
    private bool _queueRunActive;
    private int _activeQueueRunCount;
    private string _displayedWorkspaceRoot = "";

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = new();
    public ObservableCollection<AccountItemViewModel> FilteredAccounts { get; } = new();
    public ObservableCollection<PublishTaskItemViewModel> Tasks { get; } = new();
    public ObservableCollection<QueueProjectRowViewModel> QueueProjectRows { get; } = new();
    public RangeObservableCollection<QueueProjectRowViewModel> FilteredQueueProjectRows { get; } = new();

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
    [ObservableProperty] private bool _queueDeleteSourceVideosEnabled;
    [ObservableProperty] private bool _queueSmallVideoRepairEnabled;
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
    private string _currentQueueBatchId = "";
    private int _workspaceRefreshGeneration;
    private DateTime _lastLogSnapshotUtc = DateTime.MinValue;
    private readonly Dictionary<string, QueueProjectRowViewModel> _queueRowByDir =
        new(StringComparer.OrdinalIgnoreCase);
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
    public event Action<AccountItemViewModel, bool>? EmbeddedLoginRequested;
    public event Func<QueueRunOptions?, IReadOnlyCollection<string>?, Task>? RemoteQueueRunRequested;
    public event Func<QueueRunOptions?, IReadOnlyList<WorkspaceQueueTarget>, Task>? RemoteAllQueueRunRequested;

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
        ArchivedProjects.StatusRequested += message => StatusMessage = message;
        ArchivedProjects.AccountProvider = () => SelectedAccount?.Model;
        ArchivedProjects.AccountResolver = ResolveAccountForQueueItem;
        ArchivedProjects.Restored += () => RefreshWorkspaceProjects(WorkspacePath, force: true);
        DramaDownload.ImportToQueueRequested += ImportDramaProjectsToQueue;
        DramaDownload.UploadWorkspaceRequested += () => WorkspacePath;
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
        ArchivedProjects.StatusRequested += message => StatusMessage = message;
        ArchivedProjects.AccountProvider = () => SelectedAccount?.Model;
        ArchivedProjects.AccountResolver = ResolveAccountForQueueItem;
        ArchivedProjects.Restored += () => RefreshWorkspaceProjects(WorkspacePath, force: true);
        DramaDownload.ImportToQueueRequested += ImportDramaProjectsToQueue;
        DramaDownload.UploadWorkspaceRequested += () => WorkspacePath;
        WireQueueOrchestrator();
    }

    private void WireQueueOrchestrator()
    {
        _queueOrchestrator.ManualInterventionPending += OnOrchestratorManualInterventionPending;
        _queueStatePersist.SetOnPersisted(AutoExportQueueExcelForWorkspace);
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
    }

    partial void OnPreferUploadWhenReadyChanged(bool value)
    {
        _queueRunOptions.PreferUploadWhenReady = value;
        PersistQueueRunOptions();
    }

    partial void OnSyncManagementAfterUploadChanged(bool value)
    {
        _queueRunOptions.SyncManagementAfterUpload = value;
        PersistQueueRunOptions();
    }

    public AccountItemViewModel? FindAccount(string nameOrId)
    {
        var profile = _store.FindByNameOrId(nameOrId);
        return profile is null ? null : Accounts.FirstOrDefault(a => a.Id == profile.Id);
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
        StatusMessage = $"已切换账号「{value.DisplayName}」";
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
    partial void OnQueueDeleteSourceVideosEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueUploadEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueSmallVideoRepairEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueSilenceDetectEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueSilenceRepairEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();
    partial void OnQueueMaterialValidateEnabledChanged(bool value) => UpdateQueueRunOptionsFromUi();

    private void UpdateQueueRunOptionsFromUi()
    {
        if (_applyingQueueStepToggles) return;
        SyncEnabledStepsFromUi();
        PersistQueueRunOptions();
        PersistAccountQueueEnabledSteps();
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

        SelectedAccount.Status = AccountStatus.LoggingIn;
        SelectedAccount.Model.TiktokLoginBrowserMode = "embedded";
        BrowserAuthStatus = forceRelogin
            ? "请在内置浏览器中重新完成 TikTok 登录"
            : "请在内置浏览器中完成 TikTok 登录";
        StatusMessage = forceRelogin
            ? $"[{SelectedAccount.DisplayName}] 已打开内置浏览器，请重新完成 TikTok 登录"
            : $"[{SelectedAccount.DisplayName}] 已打开内置浏览器，请完成 TikTok 登录";

        EmbeddedLoginRequested?.Invoke(SelectedAccount, forceRelogin);
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
            QueueDeleteSourceVideosEnabled = true;
            QueueSmallVideoRepairEnabled = true;
            QueueSilenceDetectEnabled = true;
            QueueSilenceRepairEnabled = true;
            QueueMaterialValidateEnabled = true;
            QueueUploadEnabled = true;
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
            QueueDeleteSourceVideosEnabled = false;
            QueueSmallVideoRepairEnabled = false;
            QueueSilenceDetectEnabled = false;
            QueueSilenceRepairEnabled = false;
            QueueMaterialValidateEnabled = false;
            QueueUploadEnabled = false;
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

        var account = FindAccount(boundId);
        WorkspaceBindingSummary = account is null
            ? $"账号绑定：{boundId}"
            : $"账号绑定：{account.DisplayName}（{boundId}）";
    }

    private void UpdateQueueSummaryText()
    {
        var checkedCount = _queueItems.Count(i => i.Enabled);
        var pending = WorkspaceQueueService.FilterPendingUpload(_queueItems).Count();
        QueueSummaryText =
            $"已加载 { _queueItems.Count} 个项目，勾选 {checkedCount} 个，待上传 {pending} 个" +
            (string.IsNullOrWhiteSpace(WorkspacePath) ? "" : $" · 工作目录 {WorkspacePath}");
        RefreshTodayUploadCount();
    }

    /// <summary>今日上传完成数：按当前账号隔离统计（对齐 Python <c>_count_today_uploaded_projects</c>）。</summary>
    public void RefreshTodayUploadCount()
    {
        var accountId = (SelectedAccount?.Id ?? "").Trim();
        var today = DateTime.Now.Date;
        TodayUploadCount = _queueItems.Count(item =>
        {
            if (accountId.Length > 0 &&
                !string.Equals((item.AccountProfileId ?? "").Trim(), accountId, StringComparison.Ordinal))
                return false;

            var completedAt = (item.UploadCompletedAt ?? "").Trim();
            return completedAt.Length > 0 &&
                   DateTimeOffset.TryParse(completedAt, out var timestamp) &&
                   timestamp.ToLocalTime().Date == today;
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
                string.Equals(_displayedWorkspaceRoot, SafeFullPath(root), StringComparison.OrdinalIgnoreCase))
                return;
            ApplyWorkspaceScanResult(root, scanResult.Items, scanResult.Options);
        });
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path.Trim(); }
    }

    private bool IsWorkspaceQueueRunning(string workspaceRoot)
    {
        var normalized = Path.GetFullPath(workspaceRoot);
        return _queueOrchestrator.Snapshot().Any(item =>
            item.IsRunning &&
            string.Equals(Path.GetFullPath(item.WorkspaceRoot), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private void ClearWorkspaceProjectCollections()
    {
        QueueProjectRows.Clear();
        FilteredQueueProjectRows.Clear();
    }

    private void ApplyWorkspaceScanResult(string root, List<QueueProjectItem> items, QueueRunOptions options)
    {
        if (IsWorkspaceQueueRunning(root))
            items = PreserveDisplayedRuntimeState(items);

        ClearWorkspaceProjectCollections();
        _queueRowByDir.Clear();
        _displayedWorkspaceRoot = SafeFullPath(root);
        _queueItems = items;
        _queueRunOptions = options;
        // 传入后台线程已加载的 options，避免在 UI 线程重复读工作目录运行配置。
        ApplyAccountQueueEnabledSteps(root, options);
        ForceRerunCompletedSteps = _queueRunOptions.ForceRerunCompletedSteps;
        AutoArchiveAfterUpload = _queueRunOptions.AutoArchiveAfterUpload;
        PreferUploadWhenReady = _queueRunOptions.PreferUploadWhenReady;
        SyncManagementAfterUpload = _queueRunOptions.SyncManagementAfterUpload;
        ApplyQueueStepTogglesFromOptions();
        UpdateWorkspaceBindingSummary(root);

        // WorkspaceProjects/FilteredWorkspaceProjects 无 UI 绑定，切账号时不再构建（省去大量 VM 分配）。
        var rowIndex = 1;
        foreach (var project in _queueItems)
        {
            var row = new QueueProjectRowViewModel(project) { RowIndex = rowIndex++ };
            row.EnabledChangedByUser += OnQueueRowEnabledChangedByUser;
            QueueProjectRows.Add(row);
            _queueRowByDir[NormalizeProjectDir(project.ProjectDir)] = row;
        }

        ApplyQueueProjectFilter();
        UpdateQueueSummaryText();
        RefreshLogSnapshot(force: true);

        var pending = WorkspaceQueueService.FilterPendingUpload(_queueItems).Count();
        StatusMessage = $"已扫描工作目录：{_queueItems.Count} 个项目，{pending} 个待上传";
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

    private void ApplyQueueProjectFilter()
    {
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
        // 一次性 Reset：切账号整表刷新时 ListBox 只重建一次，而非逐项 Clear+Add。
        FilteredQueueProjectRows.ReplaceAll(target);
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

        UpdateQueueSummaryText();
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

        UpdateQueueSummaryText();
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

        UpdateQueueSummaryText();
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

    public async Task<QueueWorkerSummary?> RunQueueWorkerAsync(
        IQueuePublishHost host,
        Action<QueueWorkerProgress> onProgress,
        Action<IReadOnlyList<QueueProjectItem>> onPersist,
        CancellationToken ct,
        QueueRunOptions? optionsOverride = null,
        IReadOnlyCollection<string>? projectDirFilter = null)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root) || _queueItems.Count == 0)
            return null;

        // 并行多账号时 SelectedAccount 会随 UI 切换，故填充账号优先用「工作目录绑定的账号」，
        // 仅在工作目录未绑定时回退到当前选中账号，避免把项目绑到错误账号。
        var boundId = WorkspaceBindingService.ResolveAccountProfileId(root);
        var boundAccount = string.IsNullOrWhiteSpace(boundId) ? null : FindAccount(boundId);
        var effectiveAccount = boundAccount ?? SelectedAccount;
        if (effectiveAccount is not null)
        {
            if (string.IsNullOrWhiteSpace(boundId))
            {
                WorkspaceBindingService.Bind(root, effectiveAccount.Id, effectiveAccount.DisplayName);
                UpdateWorkspaceBindingSummary(root);
            }

            foreach (var item in _queueItems.Where(i => i.Enabled && string.IsNullOrWhiteSpace(i.AccountProfileId)))
            {
                item.AccountProfileId = effectiveAccount.Id;
                item.AccountProfileName = effectiveAccount.DisplayName;
            }

            PersistQueueItems();
        }

        var runOptions = optionsOverride ?? BuildQueueRunOptionsFromUi();
        if (optionsOverride is null)
            _queueStatePersist.Enqueue(root, _queueItems, _queueRunOptions);
        RefreshRunningWorkspacesSummary();
        _currentQueueBatchId = TikTokExecutionHistoryService.NewBatchId();
        var batchId = _currentQueueBatchId;
        var totalCount = _queueItems.Count(i =>
            i.Enabled &&
            !i.Archived &&
            (projectDirFilter is null || projectDirFilter.Contains(Path.GetFullPath(i.ProjectDir))));
        var enabledSteps = runOptions.OrderedEnabledSteps();
        var projectConcurrency = runOptions.ProjectConcurrency;
        var uploadEntryMode = runOptions.UploadEntryMode;
        var account = SelectedAccount?.Model;
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
            var finalAction = SelectedFinalAction?.Value ?? FinalAction.None;
            var label = $"{SelectedAccount?.DisplayName ?? "当前账号"} · {root}";
            var summary = await _queueOrchestrator.RunWorkspaceAsync(
                root,
                _queueItems,
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
                        onPersist(items);
                },
                ct,
                projectDirFilter);
            TikTokExecutionHistoryService.AppendEvent(
                "run_finished",
                summary?.Stopped == true ? "stopped" : "completed",
                root,
                batchId: _currentQueueBatchId,
                message: "队列执行结束",
                metadata: new Dictionary<string, object?>
                {
                    ["total_count"] = summary?.TotalCount ?? 0,
                    ["success_count"] = summary?.SuccessCount ?? 0,
                    ["failed_count"] = summary?.FailedCount ?? 0,
                    ["stopped"] = summary?.Stopped ?? false,
                },
                account: SelectedAccount?.Model);
            if (string.Equals(Path.GetFullPath(root), Path.GetFullPath(WorkspacePath), StringComparison.OrdinalIgnoreCase))
                RefreshWorkspaceProjects(root);
            return summary;
        }
        finally
        {
            RefreshRunningWorkspacesSummary();
            RefreshLogSnapshot();
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
        var targets = targetsOverride is { Count: > 0 }
            ? targetsOverride
            : BuildAccountWorkspaceTargets();
        if (targets.Count == 0)
            return Array.Empty<QueueWorkerSummary?>();

        SyncEnabledStepsFromUi();
        _queueStatePersist.Enqueue(WorkspacePath, _queueItems, _queueRunOptions);
        PersistAccountQueueEnabledSteps();
        RefreshRunningWorkspacesSummary();
        _currentQueueBatchId = TikTokExecutionHistoryService.NewBatchId();
        foreach (var target in targets)
        {
            TikTokExecutionHistoryService.AppendEvent(
                "run_started",
                "running",
                target.WorkspaceRoot,
                batchId: _currentQueueBatchId,
                message: "多工作目录队列开始执行",
                metadata: new Dictionary<string, object?> { ["display_label"] = target.DisplayLabel },
                account: FindAccount(target.AccountProfileId ?? "")?.Model);
        }

        try
        {
            var finalAction = SelectedFinalAction?.Value ?? FinalAction.None;
            return await _queueOrchestrator.RunWorkspacesAsync(
                targets,
                host,
                _store,
                finalAction,
                target =>
                {
                    var account = FindAccount(target.AccountProfileId ?? "")?.Model;
                    if (optionsOverride is not null)
                    {
                        var cloned = optionsOverride.Clone();
                        cloned.ProjectConcurrency = Math.Clamp(account?.TiktokProjectConcurrency ?? cloned.ProjectConcurrency, 1, 20);
                        return cloned;
                    }

                    if (account?.Id == SelectedAccount?.Id &&
                        string.Equals(Path.GetFullPath(target.WorkspaceRoot), Path.GetFullPath(WorkspacePath), StringComparison.OrdinalIgnoreCase))
                        return BuildQueueRunOptionsFromUi();
                    return LoadQueueRunOptionsForAccountWorkspace(target.WorkspaceRoot, account);
                },
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
            RefreshRunningWorkspacesSummary();
            RefreshWorkspaceProjects(WorkspacePath);
            RefreshLogSnapshot();
        }
    }

    public IReadOnlyList<WorkspaceQueueTarget> BuildAccountWorkspaceTargets()
        => BuildAccountWorkspaceTargets(_store.Accounts, skipMissingWorkspace: true, out _);

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
                account.Id);
        }

        missingWorkspaceAccounts = missing;
        return targets.Values.OrderBy(target => target.DisplayLabel, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private QueueRunOptions BuildQueueRunOptionsFromUi()
    {
        SyncEnabledStepsFromUi();
        var concurrency = SelectedAccount?.Model.TiktokProjectConcurrency ?? _queueRunOptions.ProjectConcurrency;
        _queueRunOptions.ProjectConcurrency = Math.Clamp(concurrency < 1 ? 4 : concurrency, 1, 20);
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
        StatusMessage = text;
        AppendLog(text);
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

        if (RemoteQueueRunRequested is null)
            return TikTokRemoteCommandResult.Failed(command.Command, "队列视图尚未初始化，无法执行远程队列。");

        await RemoteQueueRunRequested.Invoke(options, null);
        return TikTokRemoteCommandResult.Accepted(command.Command, $"TikTok 队列已启动，工作目录：{workspace}");
    }

    private async Task<TikTokRemoteCommandResult> ExecuteRemoteUploadSeriesAsync(TikTokRemoteCommand command)
    {
        var titles = command.Titles?.Where(title => !string.IsNullOrWhiteSpace(title)).ToList() ?? [];
        if (titles.Count == 0)
            return TikTokRemoteCommandResult.Failed(command.Command, "未提供可上传的 TikTok 剧名。");
        if (IsQueueRunning)
            return TikTokRemoteCommandResult.Failed(command.Command, "当前已有 TikTok 队列在执行，请等待完成后再发起新任务。");
        if (command.HasMultiAccountSelection)
            return await ExecuteRemoteUploadSeriesMultiAccountAsync(command, titles);

        var hasExplicitAccount = command.HasExplicitAccountSelection;
        if (hasExplicitAccount && !TryApplyRemoteAccountSelection(command, "", out var accountError))
            return TikTokRemoteCommandResult.Failed(command.Command, accountError);
        if (!TryResolveRemoteWorkspace(command, out var workspace, out var error))
            return TikTokRemoteCommandResult.Failed(command.Command, error);
        if (!hasExplicitAccount && !TryApplyRemoteAccountSelection(command, workspace, out error))
            return TikTokRemoteCommandResult.Failed(command.Command, error);
        ActivateRemoteWorkspace(workspace);

        var options = SystemServices.BuildRemoteUploadRunOptions(command);
        options.ProjectConcurrency = Math.Clamp(SelectedAccount?.Model.TiktokProjectConcurrency ?? options.ProjectConcurrency, 1, 20);

        var result = await ImportUploadTitlesAsync(
            string.Join(Environment.NewLine, titles),
            UploadTitleImportService.DefaultEpisodeMin,
            UploadTitleImportService.DefaultEpisodeMax,
            UploadTitleImportService.MatchModeTitle,
            CancellationToken.None);

        if (result is null)
            return TikTokRemoteCommandResult.Failed(command.Command, "上传剧名导入失败。");
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

        if (RemoteQueueRunRequested is null)
            return TikTokRemoteCommandResult.Failed(command.Command, "剧集已导入，但队列视图尚未初始化，无法启动 TikTok 队列。");

        await RemoteQueueRunRequested.Invoke(options, null);
        return TikTokRemoteCommandResult.Accepted(
            command.Command,
            $"飞书上传任务已导入并启动队列：已加入执行 {result.ProjectDirs.Count} 个，未导入 {result.FailedCount} 个。"
            + (string.IsNullOrWhiteSpace(authorExcludeNotice) ? "" : $" {authorExcludeNotice}"));
    }

    private async Task<TikTokRemoteCommandResult> ExecuteRemoteStartMultiAccountQueueAsync(TikTokRemoteCommand command)
    {
        if (!TryResolveRemoteAccountQueueTargets(command, out var targets, out var error))
            return TikTokRemoteCommandResult.Failed(command.Command, error);
        if (RemoteAllQueueRunRequested is null)
            return TikTokRemoteCommandResult.Failed(command.Command, "队列视图尚未初始化，无法执行远程多账号队列。");

        var options = BuildRemoteEnabledStepOptions(command);
        await RemoteAllQueueRunRequested.Invoke(options, targets);
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

        var rawTitles = string.Join(Environment.NewLine, titles);
        var totalQueued = 0;
        var totalFailed = 0;
        var totalDuplicates = 0;
        var runTargets = new List<WorkspaceQueueTarget>();
        var failures = new List<UploadTitleImportFailure>();

        foreach (var target in targets)
        {
            var account = FindAccount(target.AccountProfileId ?? "");
            if (account is null)
            {
                failures.Add(new UploadTitleImportFailure(target.DisplayLabel, "未找到账号"));
                continue;
            }

            SelectedAccount = account;
            ActivateRemoteWorkspace(target.WorkspaceRoot);

            var result = await ImportUploadTitlesAsync(
                rawTitles,
                UploadTitleImportService.DefaultEpisodeMin,
                UploadTitleImportService.DefaultEpisodeMax,
                UploadTitleImportService.MatchModeTitle,
                CancellationToken.None);

            if (result is null)
            {
                failures.Add(new UploadTitleImportFailure(account.DisplayName, "导入失败"));
                continue;
            }

            totalQueued += result.QueuedCount;
            totalFailed += result.FailedCount;
            totalDuplicates += result.Duplicates.Count;
            if (result.Failures.Count > 0)
                failures.AddRange(result.Failures.Select(item => item with { Title = $"{account.DisplayName}/{item.Title}" }));
            if (result.ProjectDirs.Count > 0)
                runTargets.Add(target);
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

        if (RemoteAllQueueRunRequested is null)
            return TikTokRemoteCommandResult.Failed(command.Command, "剧集已导入，但队列视图尚未初始化，无法启动 TikTok 多账号队列。");

        var options = SystemServices.BuildRemoteUploadRunOptions(command);
        await RemoteAllQueueRunRequested.Invoke(options, runTargets);
        return TikTokRemoteCommandResult.Accepted(
            command.Command,
            $"飞书多账号上传任务已导入并启动队列：{runTargets.Count} 个工作目录，加入执行 {totalQueued} 个，未导入 {totalFailed} 个。"
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
        _activeQueueRunCount = Math.Max(0, _activeQueueRunCount - 1);
        if (_activeQueueRunCount == 0)
        {
            _queueCts?.Dispose();
            _queueCts = null;
            _queueRunActive = false;
        }

        RefreshRunningWorkspacesSummary();
    }

    public void HandleQueueWorkerProgress(QueueWorkerProgress progress)
    {
        NotifyDailyLimitIfPresent(progress.Message);
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

                var batchId = _currentQueueBatchId;
                _ = Task.Run(() => TikTokExecutionHistoryService.AppendEvent(
                    "queue_progress",
                    progress.Item?.StatusText ?? "info",
                    progress.WorkspaceRoot,
                    progress.Item,
                    progress.StepKey ?? "",
                    progress.Message,
                    progress.Item?.LastError ?? "",
                    batchId,
                    account: null));
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

        var activeBatchId = _currentQueueBatchId;
        var account = SelectedAccount?.Model;
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
        if (message.Contains("失败", StringComparison.Ordinal)
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

    public void ApplyPersistedQueueItems(IReadOnlyList<QueueProjectItem> items)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root)) return;

        // 切账号后，旧工作目录队列的持久化回调仍会到达；项目不属于当前工作目录时忽略，防止跨目录覆盖状态。
        if (items.Count > 0)
        {
            var rootPrefix = SafeFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var firstDir = SafeFullPath(items[0].ProjectDir);
            if (!firstDir.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                return;
        }

        _queueItems = items.ToList();
        _queueStatePersist.Enqueue(root, _queueItems, _queueRunOptions);
        ScheduleQueueRowRefresh();
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

    private void PersistQueueItems(IReadOnlyList<QueueProjectItem> items)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root)) return;
        _queueItems = items.ToList();
        _queueStatePersist.Enqueue(root, _queueItems, _queueRunOptions);
        RefreshQueueRowViewModels();
    }

    private void PersistQueueItems() => PersistQueueItems(_queueItems);

    private void OnQueueRowEnabledChangedByUser(QueueProjectRowViewModel row)
    {
        PersistQueueItems();
        UpdateQueueSummaryText();
        StatusMessage = row.IsEnabled
            ? $"已勾选「{row.NewTitle}」"
            : $"已取消勾选「{row.NewTitle}」";
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
        // 按归一化路径建索引，避免 O(行数×项目数) 的重复 Path.GetFullPath 比较拖慢 UI 线程。
        var itemsByDir = new Dictionary<string, QueueProjectItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _queueItems)
            itemsByDir[NormalizeProjectDir(item.ProjectDir)] = item;

        foreach (var row in QueueProjectRows)
        {
            if (itemsByDir.TryGetValue(NormalizeProjectDir(row.Item.ProjectDir), out var item))
                row.RefreshFrom(item);
        }
        ApplyQueueProjectFilter();
        RefreshTodayUploadCount();
    }

    private void PersistQueueRunOptions()
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root)) return;
        _queueStatePersist.Enqueue(root, _queueItems, _queueRunOptions);
    }

    private void ApplyQueueStepTogglesFromOptions()
    {
        _applyingQueueStepToggles = true;
        try
        {
            QueueDownloadEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.Download);
            QueueRewriteEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.RewriteInfo);
            QueueGeneratePosterEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.GeneratePoster);
            QueueDeleteSourceVideosEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.DeleteSourceVideos);
            QueueSmallVideoRepairEnabled = _queueRunOptions.IsStepEnabled(QueueStepRegistry.SmallVideoRepair);
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
        if (QueueDeleteSourceVideosEnabled) steps.Add(QueueStepRegistry.DeleteSourceVideos);
        if (QueueSmallVideoRepairEnabled) steps.Add(QueueStepRegistry.SmallVideoRepair);
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
        if (!hasAccountSteps)
        {
            enabledSteps = NormalizeQueueEnabledSteps(options.EnabledSteps);
            if (account is not null)
            {
                account.TiktokQueueEnabledSteps = enabledSteps.ToList();
                _context.NotifyProfileUpdated(account);
            }
        }

        options.EnabledSteps = enabledSteps.ToList();
        var concurrency = account?.TiktokProjectConcurrency ?? options.ProjectConcurrency;
        options.ProjectConcurrency = Math.Clamp(concurrency < 1 ? 4 : concurrency, 1, 20);
        return options;
    }

    private void PersistAccountQueueEnabledSteps()
    {
        var account = SelectedAccount?.Model;
        if (account is null) return;

        var enabledSteps = NormalizeQueueEnabledSteps(_queueRunOptions.EnabledSteps);

        if (NormalizeQueueEnabledSteps(account.TiktokQueueEnabledSteps).SequenceEqual(enabledSteps))
            return;

        account.TiktokQueueEnabledSteps = enabledSteps.ToList();
        _context.NotifyProfileUpdated(account);
    }

    private static List<string> NormalizeQueueEnabledSteps(IEnumerable<string>? steps)
    {
        if (steps is null) return new List<string>();

        var known = QueueStepRegistry.All.Select(step => step.Key).ToHashSet(StringComparer.Ordinal);
        return QueueStepRegistry.OrderEnabledSteps(
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

    private void BindWorkspaceToSelectedAccountIfMissing(string workspace)
    {
        var account = SelectedAccount?.Model;
        if (account is null || string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
            return;

        if (!string.IsNullOrWhiteSpace(WorkspaceBindingService.ResolveAccountProfileId(workspace)))
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
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root))
        {
            StatusMessage = "请先选择工作目录";
            return;
        }

        var added = WorkspaceQueueService.AddProjectsToQueue(root, projectDirs);
        RefreshWorkspaceProjects(root);
        StatusMessage = added.Count > 0 ? $"已导入 {added.Count} 个项目到上传队列" : "没有可导入的项目";
        AppendLog(StatusMessage);
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
                await Task.Run(() => TikTokArchivedProjectService.ArchiveQueueProjectAsync(
                        root,
                        projectDir,
                        account: account,
                        queuedAt: row.Item.QueuedAt))
                    .ConfigureAwait(true);
                row.Item.Archived = true;
                successCount++;
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
                AppendLog($"归档失败 [{name}]：{ex.Message}");
            }
        }

        if (successCount > 0)
        {
            PersistQueueItems();
            RefreshWorkspaceProjects(root);
            ArchivedProjects.SetWorkspace(root);
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

    public async Task<UploadTitleImportResult?> ImportUploadTitlesAsync(
        string rawText,
        int episodeMin,
        int episodeMax,
        string matchMode,
        CancellationToken ct)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root))
        {
            StatusMessage = "请先选择工作目录";
            return null;
        }

        if (string.IsNullOrWhiteSpace(rawText))
        {
            StatusMessage = "请先输入短剧名称";
            return null;
        }

        var settings = ClientSettingsStore.Load();
        var result = await UploadTitleImportService.ImportAsync(
            root,
            rawText,
            settings,
            SelectedAccount?.Model,
            episodeMin,
            episodeMax,
            matchMode,
            AppendLog,
            ct);

        await ApplyUploadTitleImportResultAsync(result).ConfigureAwait(false);
        var authorExcludedCount = result.Failures.Count(UploadTitleImportService.IsAuthorExcludedFailure);
        StatusMessage =
            $"上传短剧导入完成：加入 {result.QueuedCount} 个，失败 {result.FailedCount} 个，重复 {result.Duplicates.Count} 个"
            + (authorExcludedCount > 0 ? $"，作者排除 {authorExcludedCount} 个" : "");
        AppendLog(StatusMessage);
        return result;
    }

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
        PreferUploadWhenReady = false;
        OnPropertyChanged(nameof(ForceRerunCompletedSteps));
        OnPropertyChanged(nameof(PreferUploadWhenReady));

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
                AppendLog(added > 0
                    ? $"已请求追加 {added} 个项目到运行中的队列末尾。"
                    : $"已追加 {appended.Count} 个项目到队列列表。");
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

        var existingKeys = _queueItems
            .Select(item => Path.GetFullPath(item.ProjectDir))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scanned = WorkspaceQueueService.ScanProjects(root)
            .ToDictionary(item => Path.GetFullPath(item.ProjectDir), StringComparer.OrdinalIgnoreCase);
        var appended = new List<QueueProjectItem>();
        var importAccount = SelectedAccount?.Model;

        foreach (var key in importedKeys)
        {
            if (existingKeys.Contains(key))
                continue;
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
            existingKeys.Add(key);
            appended.Add(item);
        }

        if (appended.Count > 0)
        {
            PersistQueueItems();
            RefreshQueueRowViewModels();
        }

        return appended;
    }

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
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root))
            throw new InvalidOperationException("请先选择工作目录");
        var settings = ClientSettingsStore.Load();
        return TikTokExcelExportService.Export(root, _queueItems, SelectedAccount?.Model, settings);
    }

    private void AutoExportQueueExcelForWorkspace(string workspaceRoot)
    {
        try
        {
            var settings = ClientSettingsStore.Load();
            if (!settings.TiktokExcelAutoExportEnabled) return;
            if (string.IsNullOrWhiteSpace(workspaceRoot) || _queueItems.Count == 0) return;
            if (!string.Equals(
                    Path.GetFullPath(workspaceRoot),
                    Path.GetFullPath(WorkspacePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            TikTokExcelExportService.Export(workspaceRoot, _queueItems, SelectedAccount?.Model, settings);
        }
        catch (Exception ex)
        {
            AppendLog($"Excel 自动导出失败：{ex.Message}");
        }
    }

    private void AutoExportQueueExcel() => AutoExportQueueExcelForWorkspace(WorkspacePath);
}
