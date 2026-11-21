using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ChannelsPublisher.Core.Config;
using ChannelsPublisher.Core.Models;
using ChannelsPublisher.Core.Publishing;
using ChannelsPublisher.Core.Services;
using ChannelsPublisher.Desktop.Controls;
using ChannelsPublisher.Desktop.Services;
using ChannelsPublisher.Desktop.ViewModels;

namespace ChannelsPublisher.Desktop.Views;

/// <summary>素材发布视图（可复用 UserControl）：左账号列表 + 右内嵌浏览器 + 底部任务队列。
/// 可独立放进窗口（MainWindow），也可被 ConvertTools 壳作为一个 Tab 承载。</summary>
public partial class MaterialPublishView : UserControl
{
    // 内嵌浏览器只在登录或发布时创建；切换账号时关闭空闲的，每个约占 0.6 GB。
    private readonly Dictionary<string, WebView2Host> _hosts = new();
    private readonly HashSet<string> _busyAccountIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly PlaywrightPublishAutomation _automation = new();
    private PublishScheduler? _scheduler;
    private MainViewModel? _vm;
    private bool _ready;
    private string? _workspaceQueuePath;
    private bool _workspaceQueueAvailable;
    private bool _workspaceVisible;
    private Func<bool>? _canStartPublish;
    private Func<int, Task<bool>>? _confirmPublish;
    private Func<string, Task<bool?>>? _confirmPendingOutcome;
    private Func<string, int>? _allocatePort;
    private Action<string>? _releasePort;

    public event Action? RuntimeChanged;

    public string RuntimeStatus => _vm?.StatusMessage ?? string.Empty;
    // 发表配置：作为发布的全局默认，供任务队列/发布测试套用；配置对话框保存后重载。
    private PublishConfig _publishConfig = PublishConfig.Load();
    // 停止：本轮发布的取消源；运行中非空。
    private CancellationTokenSource? _publishCts;
    // 断点续传：已成功发布素材的持久化记录，resume 策略下跳过。
    private readonly PublishRunStateStore _runState = new();

    public MaterialPublishView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DataContext = new MainViewModel(new AccountStore());
        // UserControl 无 OnOpened；Loaded 时命名控件与原生窗口句柄已就绪，WebView2 才能挂载
        Loaded += OnLoaded;
    }

    public PublishAccount? SelectedAccountProfile => _vm?.SelectedAccount?.Model;
    public IReadOnlyList<PublishAccount> AccountProfiles => _vm?.Accounts.Select(item => item.Model).ToArray() ?? [];
    public bool HasActivePublish => _publishCts is not null;

    public void SetSidebarVisible(bool visible)
    {
        AccountSidebar.IsVisible = visible;
        RootLayout.ColumnDefinitions[0].Width = new GridLength(visible ? 198 : 0);
        RootLayout.ColumnSpacing = visible ? 12 : 0;
    }

    public void UseUploadWorkspace(Panel sidebarHost, AccountStore store, string queuePath,
        Func<bool> canStartPublish, Func<int, Task<bool>> confirmPublish,
        Func<string, Task<bool?>> confirmPendingOutcome,
        Func<string, int> allocatePort, Action<string> releasePort)
    {
        _workspaceQueuePath = queuePath;
        _canStartPublish = canStartPublish;
        _confirmPublish = confirmPublish;
        _confirmPendingOutcome = confirmPendingOutcome;
        _allocatePort = allocatePort;
        _releasePort = releasePort;

        SetSidebarVisible(false);
        RootLayout.Children.Remove(AccountSidebar);
        AccountSidebar.UseShellChrome();
        AccountSidebar.HideAccountRemoval();
        sidebarHost.Children.Add(AccountSidebar);
        LegacySessionImportButton.IsVisible = LegacySessionImportRequested is not null;
        foreach (var tab in WorkspaceTabs.Items.OfType<TabItem>())
            tab.IsVisible = tab == MaterialQueueTab || tab == AccountConfigTab ||
                tab == SeriesUploadTab || tab == RuntimeLogTab;
        MaterialQueueTab.Header = "素材发布";
        AddMaterialDirectoryButton.IsVisible = true;
        ClearResumeButton.IsVisible = false;
        ResolvePendingButton.IsVisible = true;
        MaterialQueue.IsVisible = true;
        MaterialQueue.RowDefinitions[1].Height = new GridLength(0);
        MaterialQueue.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
        MaterialQueue.RowDefinitions[3].Height = new GridLength(0);
        MaterialBrowserFrame.Child = null;
        MaterialBrowserFrame.IsVisible = false;
        WorkspaceBrowserPanel.Children.Add(BrowserArea);
        CloseWorkspaceBrowserButton.IsVisible = false;
        WorkspaceBrowserFrame.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        WorkspaceBrowserFrame.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        WorkspaceBrowserFrame.Width = 1;
        WorkspaceBrowserFrame.Height = 1;
        WorkspaceBrowserFrame.Margin = new Avalonia.Thickness(0);
        WorkspaceBrowserFrame.IsVisible = true;
        WorkspaceBrowserFrame.IsHitTestVisible = false;
        DataContext = new MainViewModel(store, loadAccounts: false, excludeNewAccountsFromKuaishou: true);
        AccountSidebar.DataContext = DataContext;
        var vm = _vm!;
        RuntimeLogContentHost.Content = new ListBox { ItemsSource = vm.Logs };
        WorkspaceTabs.SelectionChanged += (_, args) =>
        {
            if (ReferenceEquals(args.Source, WorkspaceTabs))
                CloseWorkspaceBrowser();
        };
        ApplyConfigToVm();
        RestoreWorkspaceQueue(vm);
    }

    public void SetWorkspaceVisible(bool visible)
    {
        _workspaceVisible = visible;
        if (!visible)
            CloseWorkspaceBrowser();
    }

    public void SetWorkspaceAccountPanelVisible(bool visible) => AccountSidebar.IsVisible = visible;

    public void RefreshWorkspaceAccounts() => _vm?.RefreshAccountList();

    public async Task DisposeWorkspaceAsync()
    {
        StopWorkspace();
        await _automation.DisposeAsync();
    }

    public void StopWorkspace()
    {
        _publishCts?.Cancel();
        CloseWorkspaceBrowser();
        if (!HasActivePublish)
            ReleaseIdleHosts(null);
    }

    private void SetWorkspaceBrowserExpanded(bool expanded)
    {
        WorkspaceBrowserFrame.Width = expanded ? double.NaN : 1;
        WorkspaceBrowserFrame.Height = expanded ? double.NaN : 1;
        WorkspaceBrowserFrame.HorizontalAlignment = expanded ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Left;
        WorkspaceBrowserFrame.VerticalAlignment = expanded ? Avalonia.Layout.VerticalAlignment.Stretch : Avalonia.Layout.VerticalAlignment.Top;
        WorkspaceBrowserFrame.Margin = expanded ? new Avalonia.Thickness(0, 48, 0, 0) : default;
        WorkspaceBrowserFrame.IsHitTestVisible = expanded;
        CloseWorkspaceBrowserButton.IsVisible = expanded;
        ShowAccount(_vm?.SelectedAccount);
    }

    private void OnCloseWorkspaceBrowserClick(object? sender, RoutedEventArgs e) => CloseWorkspaceBrowser();

    private void CloseWorkspaceBrowser()
    {
        if (_workspaceQueuePath is not null)
            SetWorkspaceBrowserExpanded(false);
    }

    public async Task PrepareSeriesSessionAsync(string accountId, CancellationToken cancellationToken)
    {
        var account = _vm?.Accounts.FirstOrDefault(item => item.Id == accountId)?.Model;
        if (account is null || string.IsNullOrWhiteSpace(account.ProfileDir))
            return;
        Directory.CreateDirectory(account.ProfileDir);
        var destination = Path.Combine(account.ProfileDir, "weixin-series-auth.json");
        if (_hosts.TryGetValue(account.Id, out var host) && host.CdpEndpoint is { } endpoint)
        {
            try
            {
                if (await _automation.TrySaveVideoChannelSessionAsync(endpoint, destination, cancellationToken))
                    return;
            }
            catch (Exception ex) when (ex is Microsoft.Playwright.PlaywrightException or IOException)
            {
                if (_vm is not null)
                    _vm.StatusMessage = "无法转存当前网页登录态，将使用原登录记录或在剧集上传窗口扫码。";
            }
        }

        var previous = account.WeixinAuthStatePath;
        if (!File.Exists(destination) && File.Exists(previous))
            File.Copy(previous, destination, overwrite: false);
    }

    public Task RunPublishTestAsync() => PublishTestAsync();

    public async Task<string> EnsureAccountCdpEndpointAsync(string accountId, CancellationToken cancellationToken)
    {
        if (_vm?.Accounts.FirstOrDefault(item => item.Id == accountId) is not { } account)
            throw new InvalidOperationException("未找到视频号账号。");
        var host = GetOrCreateHost(account);
        if (host.CdpEndpoint is { } ready) return ready;
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady()
        {
            if (host.CdpEndpoint is { } endpoint) completion.TrySetResult(endpoint);
        }
        host.Ready += OnReady;
        try
        {
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        finally { host.Ready -= OnReady; }
    }

    public event Action<PublishAccount?>? SelectedAccountChanged;
    public event EventHandler? LegacySessionImportRequested;

    public void UseAccountStore(AccountStore store) => DataContext = new MainViewModel(store);

    public void SetSeriesPublishContent(Control content)
    {
        SeriesPublishContentHost.Content = content;
    }

    public void SetUnifiedPublishContent(Control content)
    {
        UnifiedPublishContentHost.Content = content;
    }

    public void ShowUnifiedPublish()
    {
        WorkspaceTabs.SelectedIndex = 0;
    }

    public void ShowAccountSettings()
    {
        WorkspaceTabs.SelectedIndex = 1;
    }

    public void SetAccountSettingsOnly(bool settingsOnly)
    {
        for (var index = 0; index < WorkspaceTabs.ItemCount; index++)
        {
            if (WorkspaceTabs.Items[index] is TabItem tab)
                tab.IsVisible = !settingsOnly || index == 1;
        }
        if (settingsOnly)
            ShowAccountSettings();
    }

    public void SelectAccount(string accountId)
    {
        var account = _vm?.Accounts.FirstOrDefault(item =>
            string.Equals(item.Id, accountId, StringComparison.OrdinalIgnoreCase));
        if (account is not null && _vm is not null)
            _vm.SelectedAccount = account;
    }

    public void SetWorkflowContent(Control content)
    {
        WorkflowContentHost.Content = content;
    }

    public void SetMaterialWorkflowContent(Control content)
    {
        MaterialWorkflowContentHost.Content = content;
    }

    public void SetWorkspaceRunHistory(Control history)
    {
        if (_vm is not { } vm)
            return;
        RuntimeLogContentHost.Content = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem { Header = "素材运行日志", Content = new ListBox { ItemsSource = vm.Logs } },
                new TabItem { Header = "原项目队列", Content = history },
            },
        };
    }

    public void SetRuntimeLogContent(Control content)
    {
        RuntimeLogContentHost.Content = content;
    }

    public void SetArchivedProjectsContent(Control content)
    {
        ArchivedProjectsContentHost.Content = content;
    }

    public void SetDramaDownloadContent(Control content)
    {
        DramaDownloadContentHost.Content = content;
    }

    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _ready = true;
        ApplyConfigToVm();
        ShowAccount(_vm?.SelectedAccount);
    }

    // 把发表配置的运行默认（结束动作）灌进 VM，让队列/发布用配置里的选择起步。
    private void ApplyConfigToVm()
    {
        var vm = _vm;
        if (vm is null) return;
        var fa = (_workspaceQueuePath is not null ? "draft" : _publishConfig.FinalAction) switch
        {
            "publish" => FinalAction.Publish,
            "draft" => FinalAction.Draft,
            _ => FinalAction.None,
        };
        var choice = vm.FinalActionChoices.FirstOrDefault(c => c.Value == fa);
        if (choice != null) vm.SelectedFinalAction = choice;
    }

    // 用发表配置填充一条素材的默认表单项（描述/剧集/封面/原创）。
    private void ApplyConfigDefaults(PublishItem item)
    {
        var c = _publishConfig;
        if (c.FillDescription && string.IsNullOrEmpty(item.Description) && !string.IsNullOrWhiteSpace(c.DescriptionTemplate))
        {
            var text = c.DescriptionTemplate.Trim();
            item.Description = c.PrependHash && !text.StartsWith("#") ? "#" + text : text;
        }
        if (string.IsNullOrEmpty(item.DramaName))
        {
            var drama = !string.IsNullOrWhiteSpace(c.NewDramaMountTitle) ? c.NewDramaMountTitle : c.DramaName;
            if (!string.IsNullOrWhiteSpace(drama)) item.DramaName = drama.Trim();
        }
        if (c.ReplaceCover && item.CoverPath is null && !string.IsNullOrWhiteSpace(c.CoverImagePath))
            item.CoverPath = c.CoverImagePath.Trim();
        if (c.DeclareOriginal) item.DeclareOriginal = true;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.NavigateRequested -= OnNavigateRequested;
        }
        _vm = DataContext as MainViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.NavigateRequested += OnNavigateRequested;
            if (_ready) ShowAccount(_vm.SelectedAccount);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.StatusMessage))
            RuntimeChanged?.Invoke();
        if (_ready && e.PropertyName == nameof(MainViewModel.SelectedAccount))
        {
            ShowAccount(_vm?.SelectedAccount);
            SelectedAccountChanged?.Invoke(_vm?.SelectedAccount?.Model);
        }
    }

    private void OnNavigateRequested(AccountItemViewModel account, string url)
    {
        if (!_ready) return;
        if (_workspaceQueuePath is not null && _busyAccountIds.Contains(account.Id))
        {
            if (_vm is not null)
                _vm.StatusMessage = $"「{account.Name}」正在发布素材，任务结束后可重新登录。";
            return;
        }
        if (_workspaceQueuePath is not null)
            SetWorkspaceBrowserExpanded(_workspaceVisible);
        var host = GetOrCreateHost(account);
        ShowAccount(account);
        host.Navigate(url);
    }

    private void ShowAccount(AccountItemViewModel? account)
    {
        if (!_ready || BrowserArea is null || EmptyHint is null) return;

        ReleaseIdleHosts(account?.Id);
        foreach (var host in _hosts.Values)
            host.IsVisible = false;

        if (account is not null && _hosts.TryGetValue(account.Id, out var target))
        {
            target.IsVisible = true;
            EmptyHint.IsVisible = false;
            return;
        }

        EmptyHint.IsVisible = true;
    }

    private WebView2Host GetOrCreateHost(AccountItemViewModel account)
    {
        if (_hosts.TryGetValue(account.Id, out var existing))
            return existing;

        var host = new WebView2Host
        {
            UserDataFolder = account.Model.ProfileDir,
            StorageStatePath = account.Model.WeixinAuthStatePath,
            RemoteDebuggingPort = _allocatePort?.Invoke(account.Id) ?? NextFreeDebuggingPort(),
            IsVisible = _workspaceQueuePath is not null || _vm?.SelectedAccount?.Id == account.Id,
        };
        if (_workspaceQueuePath is null)
            host.Ready += () => _vm?.RecordAccountLogin(account);
        else
            host.LocationChanged += location =>
            {
                if (PlaywrightPublishAutomation.IsVideoChannelWorkspace(location) &&
                    account.Status != AccountStatus.Online && _hosts.GetValueOrDefault(account.Id) == host)
                    _vm?.RecordAccountLogin(account);
                else if (location.Host == "channels.weixin.qq.com" &&
                    location.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase))
                    account.Status = AccountStatus.LoggingIn;
            };
        _hosts[account.Id] = host;
        BrowserArea.Children.Add(host);
        if (host.IsVisible) EmptyHint.IsVisible = false;

        host.Navigate(MainViewModel.ChannelsLoginUrl);
        return host;
    }

    private int NextFreeDebuggingPort()
    {
        var used = _hosts.Values.Select(host => host.RemoteDebuggingPort).ToHashSet();
        var port = 9222;
        while (used.Contains(port)) port++;
        return port;
    }

    private void ReleaseIdleHosts(string? keepAccountId)
    {
        foreach (var id in _hosts.Keys.ToArray())
        {
            if (string.Equals(id, keepAccountId, StringComparison.OrdinalIgnoreCase) || _busyAccountIds.Contains(id))
                continue;
            RemoveHost(id);
        }
    }

    private void RemoveHost(string accountId)
    {
        if (!_hosts.Remove(accountId, out var host)) return;
        BrowserArea.Children.Remove(host);
        _releasePort?.Invoke(accountId);
        if (_vm?.Accounts.FirstOrDefault(item => item.Id == accountId) is { Status: AccountStatus.Online } account)
            account.Status = AccountStatus.Offline;
    }

    public void ReloadAccountSession(string accountId)
    {
        var wasOpen = _hosts.ContainsKey(accountId);
        RemoveHost(accountId);
        if (_vm?.Accounts.FirstOrDefault(item => item.Id == accountId) is { } account)
        {
            if (wasOpen) GetOrCreateHost(account);
            ShowAccount(account);
        }
    }

    public void RefreshImportedAccount(PublishAccount account)
    {
        var item = _vm?.Accounts.FirstOrDefault(value => value.Id == account.Id);
        if (item is null) return;
        item.RefreshLegacySessionState();
        ReloadAccountSession(account.Id);
        SelectedAccountChanged?.Invoke(account);
    }

    public void RefreshImportedAccounts(IEnumerable<PublishAccount> importedAccounts)
    {
        var ids = importedAccounts.Select(account => account.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preferred = importedAccounts.LastOrDefault()?.Id ?? _vm?.SelectedAccount?.Id;
        _vm?.ReloadAccounts(preferred);
        foreach (var id in ids)
            RemoveHost(id);
        ShowAccount(_vm?.SelectedAccount);
        SelectedAccountChanged?.Invoke(_vm?.SelectedAccount?.Model);
    }

    // 发布测试：选视频 → 经当前账号 WebView2 的 CDP 端点跑发布流程（只填不发，安全）
    private void OnAccountSettingsRequested(object? sender, EventArgs e) => ShowAccountSettings();
    private void OnLegacySessionImportClick(object? sender, RoutedEventArgs e) =>
        LegacySessionImportRequested?.Invoke(this, EventArgs.Empty);

    private async Task PublishTestAsync()
    {
        var vm = _vm;
        var account = vm?.SelectedAccount;
        if (vm is null || account is null) { if (vm != null) vm.StatusMessage = "请先选择账号"; return; }
        if (Storage is null) return;

        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要发布的视频",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("视频") { Patterns = new[] { "*.mp4", "*.mov", "*.m4v" } } },
        });
        var file = files.FirstOrDefault();
        if (file is null) return;

        var item = new PublishItem { VideoPath = file.Path.LocalPath, Description = "【多账号发布测试】" };
        ApplyConfigDefaults(item); // 描述已填则保留测试标记，仅补剧集/封面/原创等配置默认
        _scheduler ??= new PublishScheduler(_automation, _runState);

        _busyAccountIds.Add(account.Id);
        try
        {
            string endpoint;
            try { endpoint = await EnsureAccountCdpEndpointAsync(account.Id, CancellationToken.None); }
            catch (Exception)
            {
                vm.StatusMessage = "该账号内嵌浏览器未就绪（先点「登录」或等待加载完成）";
                return;
            }

            var job = new AccountPublishJob(account.Model, endpoint, new[] { item });
            vm.StatusMessage = $"[{account.Name}] 发布中：{item.DisplayName}…";
            await _scheduler.RunAsync(new[] { job }, FinalAction.None, maxParallelAccounts: 1,
                p => Dispatcher.UIThread.Post(() => vm.StatusMessage = $"[{p.AccountName}] {p.ItemName}：{p.Message}"),
                CancellationToken.None);
        }
        catch (Exception ex) { vm.StatusMessage = $"发布失败：{ex.Message}"; }
        finally { _busyAccountIds.Remove(account.Id); }
    }

    // ────────────── 全局配置对话框 ──────────────

    private async void OnPublishConfigClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var ok = await new PublishConfigDialog().ShowDialog<bool>(owner);
        if (ok)
        {
            _publishConfig = PublishConfig.Load(); // 重载并套用新默认
            ApplyConfigToVm();
            if (_vm != null) _vm.StatusMessage = "发表配置已保存（新素材将套用新默认）";
        }
    }

    private async void OnClipConfigClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var ok = await new ClipConfigDialog().ShowDialog<bool>(owner);
        if (ok && _vm != null) _vm.StatusMessage = "全局剪辑配置已保存";
    }

    // ────────────── 发布任务队列 ──────────────

    private void RestoreWorkspaceQueue(MainViewModel vm)
    {
        try
        {
            if (File.Exists(_workspaceQueuePath))
            {
                foreach (var dto in PublishTaskFile.Load(_workspaceQueuePath!).Tasks)
                {
                    if (vm.FindAccount(dto.Account) is { } account)
                        vm.Tasks.Add(new PublishTaskItemViewModel(dto.ToItem(), account));
                    else
                        throw new InvalidOperationException($"找不到任务所属账号 {dto.Account}。");
                }
            }
            _workspaceQueueAvailable = true;
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"读取素材队列失败：{ex.Message}";
        }
    }

    private bool SaveWorkspaceQueue(MainViewModel vm)
    {
        if (_workspaceQueuePath is not { } path)
            return true;
        if (!_workspaceQueueAvailable)
        {
            vm.StatusMessage = "原素材队列无法读取，请核对保存的任务文件后重新打开客户端。";
            return false;
        }

        var tempPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            new PublishTaskFile
            {
                Tasks = vm.Tasks.Select(task => PublishTaskDto.FromItem(task.Account.Id, task.Item)).ToList(),
            }.Save(tempPath);
            File.Move(tempPath, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            vm.StatusMessage = $"保存素材队列失败：{ex.Message}";
            return false;
        }
    }

    public string? QueueWorkspaceMaterials(string accountId, IEnumerable<string> paths)
    {
        if (_vm is not { } vm || vm.FindAccount(accountId) is not { } account)
            return "请先在视频号工作区选择发布账号，再发送剪辑成片。";
        if (!_workspaceQueueAvailable)
            return vm.StatusMessage;
        if (!AddWorkspaceMaterials(vm, account, paths.Where(File.Exists)))
            return vm.StatusMessage;
        ShowUnifiedPublish();
        return null;
    }

    private bool AddWorkspaceMaterials(MainViewModel vm, AccountItemViewModel account, IEnumerable<string> paths)
    {
        var added = new List<PublishTaskItemViewModel>();
        var existing = vm.Tasks.Where(task => task.Account.Id == account.Id)
            .Select(task => task.Item.VideoPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(existing.Add))
        {
            var item = new PublishItem { VideoPath = path };
            ApplyConfigDefaults(item);
            var task = new PublishTaskItemViewModel(item, account);
            vm.Tasks.Add(task);
            added.Add(task);
        }
        if (added.Count == 0)
        {
            vm.StatusMessage = $"没有新素材可加入「{account.Name}」";
            return true;
        }
        if (SaveWorkspaceQueue(vm))
        {
            vm.StatusMessage = $"已添加 {added.Count} 条素材到「{account.Name}」";
            return true;
        }

        foreach (var task in added)
            vm.Tasks.Remove(task);
        return false;
    }

    private async void OnAddMaterialDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { SelectedAccount: { } account } vm || Storage is not { } storage)
            return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择包含成片的目录",
        });
        if (folders.FirstOrDefault() is not { } folder)
            return;
        try
        {
            AddWorkspaceMaterials(vm, account, Directory.EnumerateFiles(folder.Path.LocalPath)
                .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".m4v")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            vm.StatusMessage = $"读取素材目录失败：{ex.Message}";
        }
    }

    private async void OnAddMaterialsClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        var account = vm?.SelectedAccount;
        if (vm is null || account is null) { if (vm != null) vm.StatusMessage = "请先选择账号（素材将分配给它）"; return; }
        if (Storage is null) return;

        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择素材视频（可多选）",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("视频") { Patterns = new[] { "*.mp4", "*.mov", "*.m4v" } } },
        });
        if (_workspaceQueuePath is not null)
        {
            AddWorkspaceMaterials(vm, account, files.Select(file => file.Path.LocalPath));
            return;
        }
        int n = 0;
        foreach (var f in files)
        {
            var item = new PublishItem { VideoPath = f.Path.LocalPath };
            ApplyConfigDefaults(item);
            vm.Tasks.Add(new PublishTaskItemViewModel(item, account));
            n++;
        }
        if (n > 0) vm.StatusMessage = $"已添加 {n} 条素材到「{account.Name}」";
    }

    private async void OnImportTasksClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null || Storage is null) return;
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入发布任务清单(JSON) — prep 产出",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
        });
        var file = files.FirstOrDefault();
        if (file is null) return;

        PublishTaskFile taskFile;
        try { taskFile = PublishTaskFile.Load(file.Path.LocalPath); }
        catch (Exception ex) { vm.StatusMessage = $"导入失败：{ex.Message}"; return; }

        var imported = new List<PublishTaskItemViewModel>();
        int added = 0, skipped = 0;
        foreach (var dto in taskFile.Tasks)
        {
            var acct = vm.FindAccount(dto.Account);
            if (acct is null) { skipped++; continue; }
            var task = new PublishTaskItemViewModel(dto.ToItem(), acct);
            vm.Tasks.Add(task);
            imported.Add(task);
            added++;
        }
        if (!SaveWorkspaceQueue(vm))
        {
            foreach (var task in imported) vm.Tasks.Remove(task);
            return;
        }
        if (_workspaceQueuePath is null)
        {
            var choice = vm.FinalActionChoices.FirstOrDefault(c => c.Value == taskFile.ResolveFinalAction());
            if (choice is not null) vm.SelectedFinalAction = choice;
        }
        vm.StatusMessage = $"已导入 {added} 条任务" + (skipped > 0 ? $"，跳过 {skipped} 条（账号未匹配）" : "");
    }

    private void OnClearDoneClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        var done = vm.Tasks.Where(t => t.Status == PublishTaskStatus.Done).ToList();
        foreach (var t in done) vm.Tasks.Remove(t);
        if (!SaveWorkspaceQueue(vm))
        {
            foreach (var task in done) vm.Tasks.Add(task);
            return;
        }
        vm.StatusMessage = $"已清空 {done.Count} 条完成任务";
    }

    private async void OnStartPublishClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        if (_publishCts is not null) { vm.StatusMessage = "发布进行中…（可点「停止」）"; return; }

        if (_canStartPublish?.Invoke() == false)
        {
            vm.StatusMessage = "视频号剧集上传正在运行，请等任务结束后再开始素材发布。";
            return;
        }
        if (!SaveWorkspaceQueue(vm))
            return;

        // 运行策略来自发表配置：all=全部待发；resume=断点续传（跳过已发布记录）；retry_failed=只重试失败。
        var strategy = _workspaceQueuePath is not null
            ? "resume"
            : (_publishConfig.RunStrategy ?? "all").Trim().ToLowerInvariant();
        var candidates = (strategy == "retry_failed"
                ? vm.Tasks.Where(t => t.Status == PublishTaskStatus.Failed)
                : vm.Tasks.Where(t => t.Status is PublishTaskStatus.Pending or PublishTaskStatus.Failed
                    or PublishTaskStatus.PendingConfirmation ||
                    (_workspaceQueuePath is not null && t.Status == PublishTaskStatus.Done)))
            .ToList();
        if (candidates.Count == 0) { vm.StatusMessage = "没有待发布任务"; return; }
        var finalAction = vm.SelectedFinalAction?.Value ?? FinalAction.None;
        if (finalAction == FinalAction.Publish && _confirmPublish is { } confirm)
        {
            if (!await confirm(candidates.Count) || _publishCts is not null || _canStartPublish?.Invoke() == false)
                return;
        }

        _publishCts = new CancellationTokenSource();
        SetPublishing(true);
        var runAccountIds = candidates.Select(t => t.Account.Id).Distinct().ToList();
        _busyAccountIds.UnionWith(runAccountIds);
        var jobs = new List<AccountPublishJob>();
        foreach (var group in candidates.GroupBy(t => t.Account.Id))
        {
            var account = group.First().Account;
            foreach (var t in group) { t.Status = PublishTaskStatus.Pending; t.Message = "排队中"; }
            jobs.Add(new AccountPublishJob(account.Model, "", group.Select(t => t.Item).ToList())
            {
                ResolveCdpEndpoint = ct => EnsureAccountCdpEndpointAsync(account.Id, ct),
            });
        }

        _scheduler ??= new PublishScheduler(_automation, _runState, scopeSubmissionByAction: _workspaceQueuePath is not null);
        var strategyLabel = strategy switch { "resume" => "断点续传", "retry_failed" => "仅重试失败", _ => "全部" };
        vm.StatusMessage = $"开始发布：{jobs.Count} 账号 / {candidates.Count} 素材（{strategyLabel}，{finalAction.ToLabel()}，并发 {vm.MaxParallel}）";
        try
        {
            await _scheduler.RunAsync(jobs, finalAction, vm.MaxParallel,
                p => Dispatcher.UIThread.Post(() => UpdateTaskProgress(p)), _publishCts.Token, resume: strategy == "resume");
            vm.StatusMessage = "发布结束";
        }
        catch (OperationCanceledException)
        {
            // 停止：未发出去的（排队/发布中）退回待发布，便于再次「开始」续传。
            foreach (var t in vm.Tasks.Where(t => t.Status is PublishTaskStatus.Running or PublishTaskStatus.Pending))
            {
                t.Status = PublishTaskStatus.Pending;
                t.Message = "已停止（已尝试提交的素材需先核对平台记录）";
            }
            vm.StatusMessage = "已停止。已完成的记录已保留，待确认素材会在下次运行时提示。";
        }
        catch (Exception ex) { vm.StatusMessage = $"发布出错：{ex.Message}"; }
        finally { EndPublishRun(runAccountIds); }
    }

    private void EndPublishRun(IEnumerable<string> accountIds)
    {
        _publishCts?.Dispose();
        _publishCts = null;
        SetPublishing(false);
        _busyAccountIds.ExceptWith(accountIds);
        ShowAccount(_vm?.SelectedAccount);
        RuntimeChanged?.Invoke();
    }

    private void OnStopPublishClick(object? sender, RoutedEventArgs e)
    {
        if (_publishCts is null) return;
        _publishCts.Cancel();
        if (StopPublishButton is not null) StopPublishButton.IsEnabled = false;
        if (_vm != null) _vm.StatusMessage = "正在停止…（当前素材发完即停）";
    }

    private async void OnResolvePendingClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || _confirmPendingOutcome is null || HasActivePublish)
            return;
        if (MaterialTaskList.SelectedItem is not PublishTaskItemViewModel task)
        {
            vm.StatusMessage = "请先选中一条待确认的素材，并在视频号后台核对草稿或作品。";
            return;
        }

        var submitted = await _confirmPendingOutcome(task.VideoName);
        if (submitted is null || HasActivePublish)
            return;
        try
        {
            if (!_runState.ResolvePending(task.Account.Id, task.Item, submitted.Value))
            {
                vm.StatusMessage = "这条素材没有待确认的提交记录。";
                return;
            }
            task.Status = submitted.Value ? PublishTaskStatus.Done : PublishTaskStatus.Pending;
            task.Message = submitted.Value ? "已核对平台记录" : "平台未找到记录，可重试";
            vm.StatusMessage = $"{task.VideoName}：{task.Message}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            vm.StatusMessage = $"保存核对结果失败：{ex.Message}";
        }
    }

    private void OnClearResumeClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        if (_publishCts is not null) { vm.StatusMessage = "请结束本轮发布后再清除续传记录"; return; }
        try
        {
            _runState.Reset();
            vm.StatusMessage = "已清除续传记录。重新提交前请先核对平台作品和草稿。";
        }
        catch (Exception ex) { vm.StatusMessage = $"无法清除续传记录：{ex.Message}"; }
    }

    // 开始/停止按钮互斥。
    private void SetPublishing(bool running)
    {
        if (StartPublishButton is not null) StartPublishButton.IsEnabled = !running;
        if (StopPublishButton is not null) StopPublishButton.IsEnabled = running;
    }

    private void UpdateTaskProgress(PublishProgress p)
    {
        var vm = _vm;
        if (vm is null) return;
        var task = vm.Tasks.FirstOrDefault(t =>
            t.Account.Id == p.AccountId && ReferenceEquals(t.Item, p.Item) && t.Status != PublishTaskStatus.Done);
        if (task is null) return;
        task.Message = p.Message;
        task.Status = p.SubmissionPending ? PublishTaskStatus.PendingConfirmation
            : p.Done ? (p.Ok ? PublishTaskStatus.Done : PublishTaskStatus.Failed) : PublishTaskStatus.Running;
        if (_workspaceQueuePath is not null)
            vm.StatusMessage = $"[{p.AccountName}] {p.ItemName}：{p.Message}";
    }
}
