using System.Collections.ObjectModel;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Ui.ViewModels;

public sealed record FinalActionChoice(string Label, FinalAction Value);

public sealed partial class MainViewModel : ViewModelBase
{
    public const string TikTokLoginUrl = TikTokUrls.DefaultLoginUrl;

    private readonly AccountStore _store;
    private readonly AccountContextService _context;
    private readonly QueueWorkerRunner _queueWorker = new();
    private CancellationTokenSource? _queueCts;

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = new();
    public ObservableCollection<AccountItemViewModel> FilteredAccounts { get; } = new();
    public ObservableCollection<PublishTaskItemViewModel> Tasks { get; } = new();
    public ObservableCollection<QueueProjectRowViewModel> QueueProjectRows { get; } = new();
    public ObservableCollection<QueueProjectRowViewModel> FilteredQueueProjectRows { get; } = new();

    public IReadOnlyList<FinalActionChoice> FinalActionChoices { get; } = new[]
    {
        new FinalActionChoice("只填不发（安全）", FinalAction.None),
        new FinalActionChoice("保存草稿", FinalAction.Draft),
        new FinalActionChoice("直接发表", FinalAction.Publish),
    };

    [ObservableProperty] private AccountItemViewModel? _selectedAccount;
    [ObservableProperty] private string _statusMessage = "就绪";
    [ObservableProperty] private string _accountSearchText = "";
    [ObservableProperty] private string _queueSearchText = "";
    [ObservableProperty] private string _workspacePath = "";
    [ObservableProperty] private string _workspaceBindingSummary = "账号绑定：未绑定";
    [ObservableProperty] private string _queueSummaryText = "";
    [ObservableProperty] private FinalActionChoice _selectedFinalAction;
    [ObservableProperty] private int _maxParallel = 2;
    [ObservableProperty] private bool _showOnlyPendingUpload;
    [ObservableProperty] private bool _isQueueRunning;
    [ObservableProperty] private bool _forceRerunCompletedSteps;
    [ObservableProperty] private bool _autoArchiveAfterUpload;
    [ObservableProperty] private bool _preferUploadWhenReady;
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

    public event Action<AccountItemViewModel, string>? NavigateRequested;
    public event Action<AccountItemViewModel>? AccountSwitchRequested;
    public event Action<AccountItemViewModel, bool>? EmbeddedLoginRequested;

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
        ArchivedProjects.StatusRequested += message => StatusMessage = message;
        DramaDownload.ImportToQueueRequested += ImportDramaProjectsToQueue;
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
        ArchivedProjects.StatusRequested += message => StatusMessage = message;
        DramaDownload.ImportToQueueRequested += ImportDramaProjectsToQueue;
        _queueWorker.ManualIntervention.PendingChanged += OnManualInterventionPendingChanged;
    }

    private void OnManualInterventionPendingChanged(QueueProjectItem item, string errorMessage)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var pending = _queueWorker.ManualIntervention.HasPending;
            ManualInterventionPending = pending;
            if (pending)
            {
                var title = string.IsNullOrWhiteSpace(item.Title) ? item.DisplayName : item.Title;
                ManualInterventionHint =
                    $"「{title}」上传失败，浏览器已保持打开。请在浏览器里处理完成后点击「标记成功 / 失败」。错误：{errorMessage}";
            }
            else
            {
                ManualInterventionHint = "";
            }
        });
    }

    public bool ResolveManualIntervention(string action)
    {
        var handled = _queueWorker.ManualIntervention.Resolve(action);
        if (handled)
        {
            ManualInterventionPending = _queueWorker.ManualIntervention.HasPending;
            if (!ManualInterventionPending) ManualInterventionHint = "";
        }
        return handled;
    }

    public void AppendLog(string text)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        Logs.Append($"[{timestamp}] INFO {text}");
        RefreshLogSnapshot();
    }

    public void RefreshLogSnapshot()
    {
        Logs.UpdateSnapshot(QueueProjectRows, WorkspacePath, IsQueueRunning);
    }

    public void RequestNavigate(string pageTag) => NavigatePageRequested?.Invoke(pageTag);

    partial void OnAccountSearchTextChanged(string value) => RefreshFilteredAccounts();

    partial void OnQueueSearchTextChanged(string value) => ApplyQueueProjectFilter();

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

    public AccountItemViewModel? FindAccount(string nameOrId)
    {
        var profile = _store.FindByNameOrId(nameOrId);
        return profile is null ? null : Accounts.FirstOrDefault(a => a.Id == profile.Id);
    }

    partial void OnSelectedAccountChanged(AccountItemViewModel? value)
    {
        if (value is null) return;
        if (_store.ActiveAccountId == value.Id) return;
        _context.SwitchTo(value.Id);
        RefreshWorkspaceFromActiveAccount();
        AccountSwitchRequested?.Invoke(value);
        StatusMessage = $"已切换账号「{value.DisplayName}」";
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
        SyncEnabledStepsFromUi();
        PersistQueueRunOptions();
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
        QueueDownloadEnabled = true;
        QueueRewriteEnabled = true;
        QueueGeneratePosterEnabled = true;
        QueueDeleteSourceVideosEnabled = true;
        QueueSmallVideoRepairEnabled = true;
        QueueSilenceDetectEnabled = true;
        QueueSilenceRepairEnabled = true;
        QueueMaterialValidateEnabled = true;
        QueueUploadEnabled = true;
        UpdateQueueRunOptionsFromUi();
    }

    [RelayCommand]
    private void ClearAllQueueSteps()
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
    }

    [RelayCommand]
    private void Login() => BeginAccountLogin(forceRelogin: false);

    [RelayCommand]
    private void Relogin() => BeginAccountLogin(forceRelogin: true);

    public void SetWorkspacePath(string path)
    {
        WorkspacePath = path;
        var active = SelectedAccount?.Model;
        if (active is null || string.IsNullOrWhiteSpace(path)) return;

        active.LastWorkspace = path;
        active.TiktokUploadProfilePath = path;
        _context.NotifyProfileUpdated(active);
        WorkspaceBindingService.Bind(path, active.Id, active.DisplayName);
        RefreshWorkspaceProjects(path);
        StatusMessage = $"工作目录已绑定到「{active.DisplayName}」：{path}";
    }

    public void RefreshWorkspaceProjects(string? workspaceRoot = null)
    {
        WorkspaceProjects.Clear();
        FilteredWorkspaceProjects.Clear();
        QueueProjectRows.Clear();
        FilteredQueueProjectRows.Clear();
        var root = (workspaceRoot ?? WorkspacePath).Trim();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            _queueItems.Clear();
            return;
        }

        _queueItems = WorkspaceQueueService.ScanProjects(root).ToList();
        _queueRunOptions = WorkspaceQueueService.LoadRunOptions(root);
        ForceRerunCompletedSteps = _queueRunOptions.ForceRerunCompletedSteps;
        AutoArchiveAfterUpload = _queueRunOptions.AutoArchiveAfterUpload;
        PreferUploadWhenReady = _queueRunOptions.PreferUploadWhenReady;
        ApplyQueueStepTogglesFromOptions();
        UpdateWorkspaceBindingSummary(root);

        var rowIndex = 1;
        foreach (var project in _queueItems)
        {
            WorkspaceProjects.Add(new WorkspaceProjectItemViewModel(project));
            QueueProjectRows.Add(new QueueProjectRowViewModel(project) { RowIndex = rowIndex++ });
        }
        ApplyWorkspaceProjectFilter();
        ApplyQueueProjectFilter();
        UpdateQueueSummaryText();
        RefreshLogSnapshot();

        var pending = WorkspaceQueueService.FilterPendingUpload(_queueItems).Count();
        StatusMessage = $"已扫描工作目录：{_queueItems.Count} 个项目，{pending} 个待上传";
    }

    partial void OnShowOnlyPendingUploadChanged(bool value)
    {
        ApplyWorkspaceProjectFilter();
        ApplyQueueProjectFilter();
    }

    private void ApplyWorkspaceProjectFilter()
    {
        FilteredWorkspaceProjects.Clear();
        foreach (var vm in ShowOnlyPendingUpload
                     ? WorkspaceProjects.Where(p => p.IsPendingUpload)
                     : WorkspaceProjects)
            FilteredWorkspaceProjects.Add(vm);
    }

    private void ApplyQueueProjectFilter()
    {
        FilteredQueueProjectRows.Clear();
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

        var index = 1;
        foreach (var vm in rows)
        {
            vm.RowIndex = index++;
            FilteredQueueProjectRows.Add(vm);
        }
    }

    public IReadOnlyList<QueueProjectItem> GetPendingUploadProjects() =>
        WorkspaceQueueService.FilterPendingUpload(_queueItems).ToList();

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
        CancellationToken ct)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root) || _queueItems.Count == 0)
            return null;

        SyncEnabledStepsFromUi();
        IsQueueRunning = true;
        try
        {
            var finalAction = SelectedFinalAction?.Value ?? FinalAction.None;
            return await _queueWorker.RunAsync(
                root,
                _queueItems,
                _queueRunOptions,
                host,
                _store,
                finalAction,
                onProgress,
                onPersist,
                ct);
        }
        finally
        {
            IsQueueRunning = false;
            RefreshLogSnapshot();
        }
    }

    public void RequestStopQueue() => _queueCts?.Cancel();

    public CancellationToken BeginQueueRun()
    {
        _queueCts?.Dispose();
        _queueCts = new CancellationTokenSource();
        return _queueCts.Token;
    }

    public void EndQueueRun()
    {
        _queueCts?.Dispose();
        _queueCts = null;
    }

    public void HandleQueueWorkerProgress(QueueWorkerProgress progress)
    {
        if (progress.Item is not null)
            RefreshQueueRowFor(progress.Item);
        StatusMessage = progress.Message;
        var project = progress.Item?.Title ?? progress.Item?.DisplayName ?? "";
        var prefix = string.IsNullOrWhiteSpace(project) ? "" : $"[{project}] ";
        AppendLog($"{prefix}{progress.Message}");
    }

    public void ApplyPersistedQueueItems(IReadOnlyList<QueueProjectItem> items) =>
        PersistQueueItems(items);

    private void PersistQueueItems(IReadOnlyList<QueueProjectItem> items)
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root)) return;
        _queueItems = items.ToList();
        WorkspaceQueueService.SaveRunOptions(root, _queueItems, _queueRunOptions);
        RefreshQueueRowViewModels();
    }

    private void PersistQueueItems() => PersistQueueItems(_queueItems);

    private void RefreshQueueRowFor(QueueProjectItem item)
    {
        var normalized = Path.GetFullPath(item.ProjectDir);
        var row = QueueProjectRows.FirstOrDefault(r =>
            string.Equals(Path.GetFullPath(r.Item.ProjectDir), normalized, StringComparison.OrdinalIgnoreCase));
        row?.RefreshFrom(item);
    }

    private void RefreshQueueRowViewModels()
    {
        foreach (var row in QueueProjectRows)
        {
            var item = _queueItems.FirstOrDefault(i =>
                string.Equals(Path.GetFullPath(i.ProjectDir), Path.GetFullPath(row.Item.ProjectDir),
                    StringComparison.OrdinalIgnoreCase));
            if (item is not null)
                row.RefreshFrom(item);
        }
        ApplyQueueProjectFilter();
    }

    private void PersistQueueRunOptions()
    {
        var root = WorkspacePath.Trim();
        if (string.IsNullOrEmpty(root)) return;
        WorkspaceQueueService.SaveRunOptions(root, _queueItems, _queueRunOptions);
    }

    private void ApplyQueueStepTogglesFromOptions()
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
        _queueRunOptions.EnabledSteps = steps.Count > 0
            ? QueueStepRegistry.OrderEnabledSteps(steps).ToList()
            : QueueStepRegistry.DefaultEnabledSteps.ToList();
        _queueRunOptions.ForceRerunCompletedSteps = ForceRerunCompletedSteps;
        _queueRunOptions.AutoArchiveAfterUpload = AutoArchiveAfterUpload;
        _queueRunOptions.PreferUploadWhenReady = PreferUploadWhenReady;
        var concurrency = SelectedAccount?.Model.TiktokProjectConcurrency ?? _queueRunOptions.ProjectConcurrency;
        _queueRunOptions.ProjectConcurrency = Math.Clamp(concurrency < 1 ? 4 : concurrency, 1, 20);
    }

    private void RefreshWorkspaceFromActiveAccount()
    {
        var workspace = SelectedAccount?.Model.ResolveWorkspacePath() ?? "";
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            WorkspacePath = workspace;
            RefreshWorkspaceProjects(workspace);
        }

        SystemSettings.UpdateWorkspacePath(WorkspacePath);
        ArchivedProjects.SetWorkspace(WorkspacePath);
        SystemServices.Load();
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
    }

    public AccountStore.PythonImportResult SyncWithPythonClient(bool merge = true)
    {
        var result = _store.SyncWithPythonDatabase(merge: merge);
        ReloadAccounts();
        StatusMessage = result.Message;
        return result;
    }

    public AccountStore.PythonImportResult ImportFromPythonClient(bool merge = true)
    {
        var result = _store.ImportFromPythonDatabase(merge: merge);
        ReloadAccounts();
        StatusMessage = result.Message;
        return result;
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

        foreach (var row in rows)
        {
            await TikTokArchivedProjectService.ArchiveQueueProjectAsync(root, row.Item.ProjectDir);
            row.Item.Archived = true;
        }

        PersistQueueItems();
        RefreshWorkspaceProjects(root);
        StatusMessage = $"已归档 {rows.Length} 个项目";
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
}
