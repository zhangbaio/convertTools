using PublisherShared.Licensing.Ui;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Threading;
using PlatformPublisher.Common.Services;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using PublisherShared.Licensing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Desktop;
using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.Services.TikTok;
using TikTokPublisher.Ui.ViewModels;
using TikTokPublisher.Ui.Views;

namespace TikTokPublisher.Desktop.Views;

public partial class MainWindow : Window
{

    private readonly AccountStore _accountStore = new();
    private readonly BrowserSessionHost _browserHost = new();
    private readonly MainViewModel _viewModel;
    private readonly TikTokAccountInventorySyncCoordinator _accountInventorySync;
    private readonly HashSet<string> _externalLoginsInProgress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _apiLoginChecks = new(StringComparer.Ordinal);
    private string _activeNavTag = "queue";
    private string _tikTokSubPage = "queue";
    private bool _navigationPending;
    private bool _weixinClosePending;
    private bool _licenseRequiresClose;
    private bool _tikTokAnalyticsEnabled;
    private bool _historyKuaishou;
    private const double CollapsedRailWidth = 52;
    private readonly HashSet<string> _collapsedAccountPanels = [];
    private bool _moduleRailCollapsed;
    private bool _compactLayout;
    private bool _startupLayoutApplied;
    private bool _suspendPlacementTracking;
    private MainWindowPlacement? _normalPlacement;
    private DispatcherTimer? _licenseVerifyTimer;
    private bool _licenseVerifyRunning;
    private bool _licenseLoginDialogOpen;
    private MachineResourceMonitor? _resourceMonitor;
    private IBrush? _resourceNormalBrush;
    private IBrush? _resourceWarmBrush;
    private IBrush? _resourceHotBrush;

    public Task CheckClientUpdateOnStartupAsync() =>
        _viewModel.SystemServices.CheckClientUpdateOnStartupAsync();

    public MainWindow()
    {
        var context = new AccountContextService(_accountStore);
        _viewModel = new MainViewModel(_accountStore, context, PlatformPublisherPaths.SettingsDatabasePath);
        _accountInventorySync = new TikTokAccountInventorySyncCoordinator(_accountStore);
        InitializeComponent();
        DataContext = _viewModel;

        _browserHost.Attach(BrowserHostMount);
        _browserHost.PresentationLayoutChanged += () =>
        {
            if (IsTikTokBrowserActive)
                ScheduleBrowserHostMountLayout();
        };
        QueueView.Initialize(_viewModel, _browserHost, EnsureBrowserHostMounted);
        _browserHost.IsAccountInUse = QueueView.IsBrowserInUse;
        BrowserView.Initialize(_browserHost, _viewModel);
        BrowserView.LayoutUpdated += (_, _) =>
        {
            if (IsTikTokBrowserActive)
                SyncBrowserHostMountLayout();
        };
        AccountSidebar.DataContext = _viewModel;
        AccountSidebar.CanChangeAccountAsync = ConfirmTikTokAccountChangeAsync;
        KuaishouWorkspace.MountAccountPanel(AccountPanelHost);
        WeixinWorkspace.Bind(AccountPanelHost, KuaishouWorkspace);
        WeixinWorkspace.RuntimeChanged += UpdateWeixinStatus;
        WeixinWorkspace.SettingsRequested += () => NavigateTo("settings");
        if (WeixinWorkspace.IsMaterialPublishVisible)
            MaterialClipPage.BindWeixinExport(SendClippedMaterialsToWeixinAsync);
        AccountsView.Bind(_viewModel);
        AccountsView.UseWorkspaceNavigation();
        DedupTemplatesView.Bind(_viewModel);
        TikTokBasicSettingsButton.IsVisible = AccountsView.HasWorkspacePage("基础设置");
        KuaishouWorkspace.SettingsRequested += () => NavigateTo("settings");
        KuaishouWorkspace.RuntimeChanged += () =>
        {
            if (HistoryWorkspace.IsVisible)
                ApplyHistoryPage(_activeNavTag);
        };
        LogView.Bind(_viewModel, _viewModel.Logs);
        SettingsView.Bind(_viewModel.SystemSettings);
        SettingsView.BindServices(_viewModel.SystemServices);
        ClientUpdateCoordinator.Bind(() => _viewModel.IsQueueRunning || KuaishouWorkspace.HasActiveUploads ||
            WeixinWorkspace.HasActiveUploads || MaterialClipPage.IsRunning);
        _viewModel.SystemServices.ConfirmClientUpdate = message =>
            ConfirmDialog.ShowAsync(this, "客户端升级", message);
        _viewModel.SystemServices.ConfigImported += () =>
        {
            _viewModel.ReloadAccountsFromDisk();
            _viewModel.SystemSettings.Load();
            _viewModel.SystemServices.Load();
            ApplyAccountAnalytics(_viewModel.SystemSettings.TiktokAccountAnalyticsEnabled);
        };
        ArchivedView.Bind(_viewModel.ArchivedProjects);
        _viewModel.BindKuaishouDownloadQueue(() => KuaishouWorkspace.SelectedDownloadTarget,
            KuaishouWorkspace.GetDownloadTargets, KuaishouWorkspace.ImportDownloadedProjectsAsync);
        DownloadView.Bind(_viewModel.DramaDownload, _viewModel.AppendLog);
        AnalyticsView.Bind(_viewModel.Accounts, _viewModel.AppendLog);
        _viewModel.SystemSettings.SettingsSaved += settings =>
            ApplyAccountAnalytics(settings.TiktokAccountAnalyticsEnabled);
        ApplyAccountAnalytics(_viewModel.SystemSettings.TiktokAccountAnalyticsEnabled);

        QueueView.OpenBrowserRequested += (_, _) => NavigateTo("browser");
        QueueView.PublishBrowserFocusRequested += account =>
        {
            if (_viewModel.SelectedAccount?.Id != account.Id)
                _viewModel.SelectedAccount = account;
            NavigateTo("browser");
        };
        QueueView.OpenLogsRequested += (_, _) => _ = NavigateAsync("queue", "logs");
        AccountsView.LoginRequested += (_, _) => BeginAccountLogin(forceRelogin: false);
        AccountsView.ReloginRequested += (_, _) => BeginAccountLogin(forceRelogin: true);
        AccountsView.LogoutRequested += (_, _) => _ = LogoutSelectedTikTokAccountAsync();
        _viewModel.AccountLoginRequested += OnAccountLoginRequested;
        _browserHost.AuthSaved += args => _viewModel.HandleEmbeddedAuthSaved(args.Account, args.Result);
        _browserHost.AuthSaveFailed += _viewModel.HandleEmbeddedAuthSaveFailed;
        _browserHost.AuthStatusChanged += _viewModel.HandleEmbeddedAuthStatusChanged;
        LogView.ReturnRequested += (_, _) => _ = NavigateAsync("queue", "queue");
        _viewModel.NavigatePageRequested += NavigateTo;
        _viewModel.RemoteQueueRunRequested += QueueView.StartQueueRunFromRemoteAsync;
        _viewModel.RemoteAllQueueRunRequested += QueueView.StartAllQueueRunFromRemoteAsync;
        _viewModel.CheckedQueueRunRequested += QueueView.StartCheckedQueueLikeButtonAsync;
        _viewModel.StartXingeRemoteCommandService();
        _accountInventorySync.StatusChanged += _viewModel.AppendLog;
        _accountInventorySync.Start();
        _viewModel.AccountProfileNetworkChanged += profile => _browserHost.InvalidateHostIfNetworkChanged(profile);
        _viewModel.AccountSwitchRequested += OnAccountSwitchRequested;
        _viewModel.DailyLimitReached += message =>
            _ = ConfirmDialog.ShowAsync(
                this,
                "单日创建剧集已达上限",
                $"{message}\n\n任务队列已自动停止，请明天再继续上传。");

        ApplyAccountPanelLayout();
        NavigateTo("queue");
        Closed += OnWindowClosed;
        Opened += async (_, _) =>
        {
            _featurePermissionTimer.Tick += async (_, _) => await RefreshModulePermissionsAsync();
            _featurePermissionTimer.Start();
            await RefreshModulePermissionsAsync();
        };
        Closed += (_, _) => _featurePermissionTimer.Stop();
        ApplyStartupLayout();
        SizeChanged += (_, _) => RememberNormalPlacement();
        PositionChanged += (_, _) => RememberNormalPlacement();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!_startupLayoutApplied)
            ApplyStartupLayout();
        StartResourceMonitor();
        _ = KuaishouWorkspace.PrepareAsync();
    }

    private void StartResourceMonitor()
    {
        if (_resourceMonitor is not null || !OperatingSystem.IsWindows())
            return;
        _resourceMonitor = new MachineResourceMonitor();
        _resourceMonitor.Updated += OnMachineResourceUpdated;
        _resourceMonitor.Start();
    }

    private void StopResourceMonitor()
    {
        if (!OperatingSystem.IsWindows() || _resourceMonitor is null)
            return;
        _resourceMonitor.Updated -= OnMachineResourceUpdated;
        _resourceMonitor.Dispose();
        _resourceMonitor = null;
    }

    private void OnMachineResourceUpdated(MachineResourceSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ApplyResourceText(CpuUsageText, "CPU", snapshot.CpuPercent);
            ApplyResourceText(GpuUsageText, "GPU", snapshot.GpuPercent);
            ApplyResourceText(MemUsageText, "MEM", snapshot.MemoryPercent);
            ToolTip.SetTip(GpuUsageText, MachineResourceMath.GpuTooltip(snapshot.Adapters));
        });
    }

    private void ApplyResourceText(TextBlock target, string label, int? percent)
    {
        target.Text = MachineResourceMath.FormatUsage(label, percent);
        target.Foreground = BrushFor(percent);
    }

    private IBrush BrushFor(int? percent)
    {
        _resourceNormalBrush ??= BrushOrFallback("PythonMutedBrush", Color.Parse("#64748B"));
        _resourceWarmBrush ??= BrushOrFallback("PythonPendingText", Color.Parse("#B45309"));
        _resourceHotBrush ??= BrushOrFallback("PythonDangerText", Color.Parse("#D92D20"));
        if (percent is not int value || value < 80)
            return _resourceNormalBrush;
        return value >= 90 ? _resourceHotBrush : _resourceWarmBrush;
    }

    private IBrush BrushOrFallback(string key, Color fallback)
    {
        if (this.TryFindResource(key, out var found))
        {
            if (found is IBrush brush)
                return brush;
            if (found is Color color)
                return new SolidColorBrush(color);
        }

        return new SolidColorBrush(fallback);
    }

    private double ExpandedSidebarWidth =>
        _compactLayout ? LaptopLayout.CompactSidebarWidth : LaptopLayout.ExpandedSidebarWidth;

    private void ApplyAccountAnalytics(bool enabled)
    {
        _tikTokAnalyticsEnabled = enabled;
        AnalyticsView.SetEnabled(enabled);
        if (_activeNavTag == "analytics")
            ApplyHistoryPage("analytics");
    }

    private void OnNavItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && !string.IsNullOrWhiteSpace(tag))
            NavigateTo(tag);
    }

    private void OnSidebarToggleClick(object? sender, RoutedEventArgs e)
    {
        if (!_collapsedAccountPanels.Remove(AccountPanelLayoutKey))
            _collapsedAccountPanels.Add(AccountPanelLayoutKey);
        ApplyAccountPanelLayout();
    }

    private void OnModuleRailToggleClick(object? sender, RoutedEventArgs e)
    {
        _moduleRailCollapsed = !_moduleRailCollapsed;
        ModuleRail.Width = _moduleRailCollapsed ? CollapsedRailWidth : 148;
        ModuleRail.Classes.Set("moduleRailCollapsed", _moduleRailCollapsed);
        var label = _moduleRailCollapsed ? "展开左侧菜单" : "收起左侧菜单";
        ToolTip.SetTip(ModuleRailToggleButton, label);
        Avalonia.Automation.AutomationProperties.SetName(ModuleRailToggleButton, label);
    }

    private async void OnTikTokSubPageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page })
            await NavigateAsync("queue", page);
    }

    private string AccountPanelLayoutKey => _activeNavTag == "kuaishou" ? "queue" : _activeNavTag;

    private void ApplyAccountPanelLayout()
    {
        var hasAccounts = _activeNavTag is "queue" or "kuaishou" or "weixin";
        var collapsed = _collapsedAccountPanels.Contains(AccountPanelLayoutKey);
        var showAccounts = hasAccounts && !collapsed;
        AccountRail.IsVisible = hasAccounts;
        AccountPanelHost.Width = ExpandedSidebarWidth;
        AccountPanelHost.IsHitTestVisible = showAccounts;
        if (!hasAccounts || showAccounts)
            AccountPanelHost.IsVisible = showAccounts;
        AccountSidebar.IsVisible = _activeNavTag == "queue";
        KuaishouWorkspace.SetAccountPanelVisible(_activeNavTag == "kuaishou");
        WeixinWorkspace.SetAccountPanelVisible(_activeNavTag == "weixin");

        if (hasAccounts)
            AccountRail.Width = collapsed ? CollapsedRailWidth : ExpandedSidebarWidth;
        AccountRail.Classes.Set("accountRailExpanded", showAccounts);
        AccountRail.Classes.Set("accountRailCollapsed", !showAccounts);
        var toggleLabel = collapsed ? "展开账号栏" : "收起账号栏";
        ToolTip.SetTip(SidebarToggleButton, toggleLabel);
        Avalonia.Automation.AutomationProperties.SetName(SidebarToggleButton, toggleLabel);
    }

    private void OnAccountRailSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!AccountPanelHost.IsHitTestVisible && e.NewSize.Width <= CollapsedRailWidth)
            AccountPanelHost.IsVisible = false;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_licenseRequiresClose && WeixinWorkspace.HasActiveUploads)
        {
            e.Cancel = true;
            if (!_weixinClosePending)
                _ = StopWeixinBeforeClosingAsync();
        }
        base.OnClosing(e);
    }

    private async Task StopWeixinBeforeClosingAsync()
    {
        _weixinClosePending = true;
        try
        {
            if (!await ConfirmDialog.ShowAsync(this, "关闭短剧助手", "微信视频号还有任务在运行。停止任务并保存进度后退出？"))
                return;
            WeixinWorkspace.StopUploads();
            while (WeixinWorkspace.HasActiveUploads)
                await Task.Delay(150);
            Close();
        }
        finally
        {
            _weixinClosePending = false;
        }
    }

    private async Task DisposeWeixinWorkspaceAsync()
    {
        try
        {
            await WeixinWorkspace.CloseWorkspaceAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("关闭视频号工作区失败：{0}", ex);
        }
    }

    private async Task<string?> SendClippedMaterialsToWeixinAsync(IReadOnlyList<string> paths)
    {
        await NavigateAsync("weixin");
        if (_activeNavTag != "weixin")
            return "未打开视频号工作区。";
        await WeixinWorkspace.ActivateAsync();
        return await WeixinWorkspace.QueueClippedMaterialsAsync(paths);
    }

    private void UpdateWeixinStatus()
    {
        var status = WeixinWorkspace.RuntimeStatus;
        var showWeixin = _activeNavTag == "weixin" || WeixinWorkspace.HasActiveUploads;
        WeixinStatusText.IsVisible = showWeixin;
        PrimaryStatusText.IsVisible = !showWeixin;
        WeixinStatusText.Text = string.IsNullOrWhiteSpace(status) ? "微信视频号" : $"视频号 · {status}";
        ToolTip.SetTip(WeixinStatusText, WeixinStatusText.Text);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        foreach (var check in _apiLoginChecks.Values.ToArray())
            check.Cancel();
        _apiLoginChecks.Clear();
        SavePlacement();
        StopResourceMonitor();
        _licenseVerifyTimer?.Stop();
        _licenseVerifyTimer = null;
        _viewModel.StopXingeRemoteCommandService();
        _accountInventorySync.StatusChanged -= _viewModel.AppendLog;
        _accountInventorySync.Dispose();
        _ = DisposeWeixinWorkspaceAsync();
        KuaishouWorkspace.Dispose();
        MaterialClipPage.Stop();
        _browserHost.CloseAll();
        try
        {
            _viewModel.FlushActiveAccountSelection();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("保存当前账号失败：{0}", ex.Message);
        }
    }

    public void StartLicenseVerifyTimer()
    {
        _licenseVerifyTimer?.Stop();
        _licenseVerifyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromHours(LicenseAuthService.VerifyIntervalHours),
        };
        _licenseVerifyTimer.Tick += OnLicenseVerifyTimerTick;
        _licenseVerifyTimer.Start();
    }

    private async void OnLicenseVerifyTimerTick(object? sender, EventArgs e)
    {
        if (_licenseVerifyRunning)
            return;

        _licenseVerifyRunning = true;
        try
        {
            var state = await LicenseGate.VerifyAsync(forceVerify: true);
            if (state is not null)
            {
                SaveVerifiedLicenseState(state);
                _viewModel.StatusMessage = "软件授权联网校验通过";
                return;
            }

            _licenseVerifyTimer?.Stop();
            _viewModel.StatusMessage = "软件授权联网校验失败，软件即将退出。";
            _viewModel.RequestStopQueue();
            _licenseRequiresClose = true;
            Close();
        }
        finally
        {
            _licenseVerifyRunning = false;
        }
    }

    private async Task RequireLicenseReloginAsync(string message)
    {
        _licenseVerifyTimer?.Stop();
        _viewModel.StopXingeRemoteCommandService();
        _viewModel.StatusMessage = message;
        _viewModel.RequestStopQueue();
        WeixinWorkspace.StopUploads();
        HideForLicenseGate();

        var loggedIn = await ShowLicenseLoginDialogAsync(message, standalone: true);
        if (loggedIn)
        {
            _viewModel.StatusMessage = "软件授权登录成功";
            ShowAfterLicenseGate();
            StartLicenseVerifyTimer();
            return;
        }

        _licenseRequiresClose = true;
        Close();
    }

    private async Task<bool> ShowLicenseLoginDialogAsync(string message, bool standalone = false)
    {
        if (_licenseLoginDialogOpen)
            return false;

        _licenseLoginDialogOpen = true;
        try
        {
            var login = LicenseGate.GetLoginDefaults();
            var result = standalone
                ? await LicenseLoginDialog.ShowStandaloneAsync(
                    login.ServerUrl,
                    login.Account,
                    login.Password,
                    message)
                : await LicenseLoginDialog.ShowAsync(
                    this,
                    login.ServerUrl,
                    login.Account,
                    login.Password,
                    message);
            if (result is null)
                return false;

            SaveLicenseLoginResult(result);
            return true;
        }
        finally
        {
            _licenseLoginDialogOpen = false;
        }
    }

    private void SaveLicenseLoginResult(LicenseLoginDialogResult result)
    {
        LicenseGate.SaveLoginResult(result);
        _ = RefreshModulePermissionsAsync();
        _viewModel.SystemServices.Load();
        _viewModel.SystemServices.RefreshLicenseSummaryDisplay();
        _viewModel.StartXingeRemoteCommandService();
    }

    private void SaveVerifiedLicenseState(LicenseState state)
    {
        LicenseGate.SaveVerifiedState(state);
        _ = RefreshModulePermissionsAsync();
        _viewModel.SystemServices.Load();
        _viewModel.SystemServices.RefreshLicenseSummaryDisplay();
        _viewModel.StartXingeRemoteCommandService();
    }

    private void HideForLicenseGate()
    {
        CollapseBrowserHostMount();
        _browserHost.SetPresentationVisible(false);
        Hide();
    }

    private void ShowAfterLicenseGate()
    {
        Show();
        Activate();
        ShowPage(_activeNavTag);
    }

    private void BeginAccountLogin(bool forceRelogin)
    {
        if (_viewModel.SelectedAccount is null)
        {
            _viewModel.StatusMessage = "请先在左侧选择一个账号";
            return;
        }

        _viewModel.BeginAccountLogin(forceRelogin);
    }

    private async void OnAccountLoginRequested(AccountItemViewModel account, bool forceRelogin)
    {
        if (_apiLoginChecks.TryGetValue(account.Id, out var pending))
        {
            if (!forceRelogin) return;
            _apiLoginChecks.Remove(account.Id);
            pending.Cancel();
        }

        if (!forceRelogin && TikTokUploadChannels.IsApi(account.Model.TiktokUploadStrategy))
        {
            using var check = new CancellationTokenSource();
            _apiLoginChecks.Add(account.Id, check);
            try
            {
                var valid = await TikTokApiUploadService.VerifySavedLoginAsync(account.Model, check.Token);
                if (check.IsCancellationRequested) return;
                if (valid)
                {
                    _viewModel.HandleApiLoginVerified(account);
                    return;
                }
                _viewModel.StatusMessage = $"[{account.DisplayName}] 接口未确认已有会话，正在打开浏览器登录";
            }
            catch (OperationCanceledException) when (check.IsCancellationRequested) { return; }
            finally
            {
                if (_apiLoginChecks.TryGetValue(account.Id, out var current) && ReferenceEquals(current, check))
                    _apiLoginChecks.Remove(account.Id);
            }
        }

        if (string.Equals(
                account.Model.TiktokUploadBrowserMode,
                "playwright",
                StringComparison.OrdinalIgnoreCase))
        {
            await BeginExternalAccountLoginAsync(account);
            return;
        }

        if (forceRelogin)
        {
            var resetWarning = await _browserHost.ResetAccountAsync(account);
            if (!string.IsNullOrWhiteSpace(resetWarning))
                _viewModel.StatusMessage += $"；{resetWarning}";
        }

        await NavigateAsync("queue", "browser");
        if (IsTikTokBrowserActive)
            _browserHost.BeginLogin(account, forceRelogin);
    }

    private async Task BeginExternalAccountLoginAsync(AccountItemViewModel account)
    {
        if (!_externalLoginsInProgress.Add(account.Id))
        {
            _viewModel.StatusMessage = $"[{account.DisplayName}] 外部浏览器登录正在进行中";
            return;
        }

        try
        {
            _viewModel.AppendLog($"[{account.DisplayName}] 发布配置使用外部浏览器，正在启动可见登录窗口…");
            var result = await TikTokLoginService.LoginAsync(
                account.Model,
                _viewModel.AppendLog,
                CancellationToken.None,
                timeoutSeconds: 300,
                forceLaunchBrowser: true);
            _viewModel.HandleExternalAuthSaved(account, result);
            await _browserHost.SyncExternalAuthAsync(account);
        }
        catch (Exception ex)
        {
            account.Status = AccountStatus.Offline;
            _viewModel.BrowserAuthStatus = "外部浏览器登录失败";
            _viewModel.StatusMessage = $"[{account.DisplayName}] 外部浏览器登录失败：{ex.Message}";
            _viewModel.AppendLog(_viewModel.StatusMessage);
        }
        finally
        {
            _externalLoginsInProgress.Remove(account.Id);
        }
    }

    private async Task LogoutSelectedTikTokAccountAsync()
    {
        var account = _viewModel.SelectedAccount;
        if (account is null)
        {
            _viewModel.StatusMessage = "请先在左侧选择一个账号";
            return;
        }

        var confirmed = await ConfirmDialog.ShowAsync(
            this,
            "确认退出登录",
            $"确认退出「{account.DisplayName}」的 TikTok 登录？\n\n这会删除本机授权文件并清空该账号的内置浏览器会话，账号配置和工作目录会保留。");
        if (!confirmed)
            return;

        if (_apiLoginChecks.Remove(account.Id, out var pendingLogin))
            pendingLogin.Cancel();
        var warning = await _browserHost.ResetAccountAsync(account);
        account.Model.TiktokLastLoginEmail = "";
        account.Model.TiktokLastLoginAt = "";
        account.Status = AccountStatus.Offline;
        _viewModel.SaveAccountProfile(account.Model);
        account.RefreshFromModel();
        AccountsView.RefreshSelectedAccount();

        _viewModel.HandleEmbeddedAuthStatusChanged("已退出登录");
        _viewModel.StatusMessage = $"[{account.DisplayName}] 已退出 TikTok 登录";
        if (!string.IsNullOrWhiteSpace(warning))
            _viewModel.StatusMessage += $"；{warning}";
        _viewModel.AppendLog(_viewModel.StatusMessage);
    }

    private void OnAccountSwitchRequested(AccountItemViewModel account)
    {
        if (IsTikTokBrowserActive)
            ScheduleBrowserHostMountLayout();
        else if (_activeNavTag == "queue")
        {
            if (_tikTokSubPage == "logs")
                RefreshTikTokLogScope();
            else if (IsTikTokArchiveVisible)
                RefreshTikTokArchiveScope();
        }
    }

    private void EnsureBrowserHostMounted()
    {
        // 浏览器页正在展示时不得折叠挂载层/关闭渲染（否则页面变成不绘制的黑色原生窗口）。
        if (IsTikTokBrowserActive)
            return;

        // 后台上传：WebView2 挂在 1×1 隐藏层，避免原生 HWND 盖住队列页。
        CollapseBrowserHostMount();
        _browserHost.SetPresentationVisible(false);
    }

    private void CollapseBrowserHostMount()
    {
        BrowserHostMount.Margin = new Thickness(0);
        BrowserHostMount.Width = 1;
        BrowserHostMount.Height = 1;
        BrowserHostMount.HorizontalAlignment = HorizontalAlignment.Left;
        BrowserHostMount.VerticalAlignment = VerticalAlignment.Top;
        BrowserHostMount.ZIndex = 5;
    }

    private void SyncBrowserHostMountLayout()
    {
        if (!IsTikTokBrowserActive || !BrowserView.IsVisible)
        {
            CollapseBrowserHostMount();
            return;
        }

        var bounds = BrowserView.GetBrowserAreaBoundsIn(ContentHostPanel);
        if (bounds is null or { Width: <= 1 } or { Height: <= 1 })
            return;

        var rect = bounds.Value;
        // 仅在几何真正变化时更新，避免 LayoutUpdated → 改布局 → LayoutUpdated 死循环。
        var current = BrowserHostMount.Margin;
        var unchanged =
            Math.Abs(current.Left - rect.X) < 0.5 &&
            Math.Abs(current.Top - rect.Y) < 0.5 &&
            Math.Abs(BrowserHostMount.Width - rect.Width) < 0.5 &&
            Math.Abs(BrowserHostMount.Height - rect.Height) < 0.5;
        var windowMoved = ConsumeBrowserWindowMovement();
        // 切账号会触发布局，但挂载区域没变。此时再同步 WebView2 会在 UI 线程上
        // 反复调用 NotifyParentWindowPositionChanged，表现为点左侧账号卡住。
        // 窗口本身被拖动时矩形不变，仍要单独通知一次，否则网页停在旧位置。
        if (unchanged)
        {
            if (windowMoved)
                _browserHost.NotifyPresentationParentMoved();
            return;
        }

        BrowserHostMount.Margin = new Thickness(rect.X, rect.Y, 0, 0);
        BrowserHostMount.Width = rect.Width;
        BrowserHostMount.Height = rect.Height;
        BrowserHostMount.HorizontalAlignment = HorizontalAlignment.Left;
        BrowserHostMount.VerticalAlignment = VerticalAlignment.Top;
        BrowserHostMount.ZIndex = 11;
        _browserHost.RefreshPresentationBounds();
    }

    private bool _mountLayoutSyncPending;
    private bool _browserWindowMetricsReady;
    private PixelPoint _browserWindowPosition;
    private Size _browserWindowClientSize;

    private bool ConsumeBrowserWindowMovement()
    {
        var position = Position;
        var clientSize = ClientSize;
        var moved = !_browserWindowMetricsReady ||
                    position != _browserWindowPosition ||
                    clientSize != _browserWindowClientSize;
        _browserWindowMetricsReady = true;
        _browserWindowPosition = position;
        _browserWindowClientSize = clientSize;
        return moved;
    }

    private void ScheduleBrowserHostMountLayout()
    {
        // 合并多次调度；回调内不得调用 ShowAccount（会再触发 PresentationLayoutChanged 造成死循环）。
        if (_mountLayoutSyncPending)
            return;

        _mountLayoutSyncPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _mountLayoutSyncPending = false;
            SyncBrowserHostMountLayout();
        }, DispatcherPriority.Background);
    }

    private readonly DispatcherTimer _featurePermissionTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    private static bool IsModuleAllowed(string tag) => tag switch
    {
        "download" => ClientFeaturePermissions.Allows("download"),
        "material-clip" => ClientFeaturePermissions.Allows("edit"),
        "queue" => ClientFeaturePermissions.Allows("tiktok"),
        "kuaishou" => ClientFeaturePermissions.Allows("kuaishou"),
        "weixin" => ClientFeaturePermissions.Allows("weixin"),
        "analytics" => ClientFeaturePermissions.Allows("analytics"),
        _ => true,
    };

    private async Task RefreshModulePermissionsAsync()
    {
        await ClientFeaturePermissions.RefreshAsync(CancellationToken.None);
        foreach (var button in ModuleNav.Children.OfType<Button>())
            if (button.Tag is string tag)
                button.IsVisible = IsModuleAllowed(tag);
        if (!IsModuleAllowed(_activeNavTag))
        {
            _activeNavTag = "settings";
            UpdateNavHighlight();
            ShowPage(_activeNavTag);
        }
    }

    private void NavigateTo(string tag) => _ = NavigateAsync(tag);

    private bool IsTikTokBrowserActive => _activeNavTag == "queue" && _tikTokSubPage == "browser";

    private Task<bool> ConfirmTikTokAccountChangeAsync()
    {
        if (_activeNavTag != "queue")
            return Task.FromResult(true);
        if (_tikTokSubPage == "dedup")
            return DedupTemplatesView.TryLeaveAsync(this);
        return IsTikTokConfigurationPage(_tikTokSubPage) ? AccountsView.TryLeaveAsync(this) : Task.FromResult(true);
    }

    private static bool IsTikTokConfigurationPage(string page) =>
        page is "accounts" or "basics";

    private async Task NavigateAsync(string tag, string? tikTokSubPage = null)
    {
        if (_navigationPending)
            return;
        if (tag is "accounts" or "browser")
        {
            tikTokSubPage = tag;
            tag = "queue";
        }
        if (!IsModuleAllowed(tag))
            return;
        _navigationPending = true;
        try
        {
            if (_activeNavTag == "kuaishou" && tag != "kuaishou" &&
                !await KuaishouWorkspace.TryLeaveAsync(this))
                return;
            var staysOnEditor = tag == "queue" && (tikTokSubPage is null || tikTokSubPage == _tikTokSubPage ||
                (IsTikTokConfigurationPage(_tikTokSubPage) && IsTikTokConfigurationPage(tikTokSubPage)));
            if (!staysOnEditor && !await ConfirmTikTokAccountChangeAsync())
                return;
            if (tikTokSubPage is not null)
                _tikTokSubPage = tikTokSubPage;
            if (tag == "analytics" && _activeNavTag is "queue" or "kuaishou")
                _historyKuaishou = _activeNavTag == "kuaishou";
            _activeNavTag = tag;
            UpdateNavHighlight();
            ShowPage(tag);
        }
        finally
        {
            _navigationPending = false;
        }
    }

    private void UpdateNavHighlight()
    {
        foreach (var child in ModuleNav.Children)
        {
            if (child is not Button btn) continue;
            var tag = btn.Tag?.ToString() ?? "";
            var isActive = string.Equals(tag, _activeNavTag, StringComparison.OrdinalIgnoreCase);
            btn.Classes.Set("moduleNavActive", isActive);
            if (isActive && btn.IsLoaded)
                btn.BringIntoView();
        }
    }

    private void ShowPage(string tag)
    {
        var browserActive = IsTikTokBrowserActive;
        var tikTokActive = tag == "queue";
        var dedupActive = tikTokActive && _tikTokSubPage == "dedup";
        var kuaishouActive = tag == "kuaishou";
        var showUploadWorkspaces = tikTokActive || kuaishouActive;
        const int activeZ = 10;
        const int inactiveZ = 0;

        DownloadView.IsVisible = tag == "download";
        MaterialClipPage.IsVisible = tag == "material-clip";
        MaterialClipPage.ZIndex = tag == "material-clip" ? activeZ : inactiveZ;
        SetUploadWorkspacePresentation(TikTokWorkspace, showUploadWorkspaces, tikTokActive);
        if (DedupTemplatesView.IsVisible != dedupActive)
        {
            DedupTemplatesView.IsVisible = dedupActive;
            if (dedupActive)
                DedupTemplatesView.RefreshSelectedAccount();
        }
        SetUploadWorkspacePresentation(KuaishouWorkspace, showUploadWorkspaces, kuaishouActive);
        WeixinWorkspace.IsVisible = tag == "weixin";
        if (WeixinWorkspace.IsVisible)
            _ = WeixinWorkspace.ActivateAsync();
        else
            WeixinWorkspace.Deactivate();
        UpdateWeixinStatus();
        ApplyAccountPanelLayout();
        if (kuaishouActive)
            _ = KuaishouWorkspace.ActivateAsync();
        else
            KuaishouWorkspace.Deactivate();
        if (tikTokActive)
        {
            QueueView.IsVisible = _tikTokSubPage == "queue";
            TikTokQueueFooter.IsVisible = QueueView.IsVisible;
            LogView.IsVisible = _tikTokSubPage == "logs";
            TikTokAllLogAccounts.IsVisible = LogView.IsVisible;
            if (LogView.IsVisible)
                RefreshTikTokLogScope();
            AccountsView.IsVisible = IsTikTokConfigurationPage(_tikTokSubPage);
            if (AccountsView.IsVisible)
                AccountsView.SelectWorkspacePage(_tikTokSubPage == "basics" ? "基础设置" : "发布配置");
            ArchivedView.IsVisible = IsTikTokArchiveVisible;
            TikTokAllArchiveAccounts.IsVisible = ArchivedView.IsVisible;
            if (ArchivedView.IsVisible)
                RefreshTikTokArchiveScope();
            foreach (var button in TikTokSubNavigation.Children.OfType<Button>())
            {
                var active = Equals(button.Tag, _tikTokSubPage);
                button.Classes.Set("topNavItemActive", active);
                if (active && button.IsLoaded)
                    button.BringIntoView();
            }
        }
        SettingsView.IsVisible = tag == "settings";
        ApplyHistoryPage(tag);

        DownloadView.ZIndex = tag == "download" ? activeZ : inactiveZ;
        TikTokWorkspace.ZIndex = tikTokActive ? activeZ : inactiveZ;
        KuaishouWorkspace.ZIndex = kuaishouActive ? activeZ : inactiveZ;
        SettingsView.ZIndex = tag == "settings" ? activeZ : inactiveZ;
        HistoryWorkspace.ZIndex = HistoryWorkspace.IsVisible ? activeZ : inactiveZ;

        if (browserActive)
        {
            BrowserView.IsVisible = true;
            BrowserView.IsHitTestVisible = true;
            _browserHost.ShowAccount(_viewModel.SelectedAccount);
            SyncBrowserHostMountLayout();
            _browserHost.SetPresentationVisible(true);
            ScheduleBrowserHostMountLayout();
        }
        else
        {
            BrowserView.IsVisible = false;
            CollapseBrowserHostMount();
            _browserHost.SetPresentationVisible(false);
        }

        if (tag == "download" || (tag == "analytics" && _historyKuaishou))
            _ = KuaishouWorkspace.PrepareAsync();
        if (tag == "settings")
        {
            _viewModel.SystemServices.RefreshLicenseSummaryDisplay();
        }
    }

    private static void SetUploadWorkspacePresentation(Control workspace, bool visible, bool active)
    {
        // Both upload queues contribute their toolbar height while either workspace is open.
        workspace.IsVisible = visible;
        workspace.Opacity = active ? 1 : 0;
        workspace.IsHitTestVisible = active;
        workspace.IsEnabled = active;
    }

    private void OnAnalyticsPlatformChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_activeNavTag != "analytics" || sender is not TabStrip { SelectedItem: TabStripItem tab })
            return;
        var showKuaishou = ReferenceEquals(tab, KuaishouAnalyticsTab);
        if (_historyKuaishou == showKuaishou)
            return;
        _historyKuaishou = showKuaishou;
        ApplyHistoryPage(_activeNavTag);
        if (showKuaishou)
            _ = KuaishouWorkspace.PrepareAsync();
    }

    private void ApplyHistoryPage(string tag)
    {
        HistoryWorkspace.IsVisible = tag == "analytics";
        if (!HistoryWorkspace.IsVisible)
            return;

        var showTikTok = _tikTokAnalyticsEnabled;
        var showKuaishou = _historyKuaishou || !showTikTok;
        _historyKuaishou = showKuaishou;
        TikTokAnalyticsTab.IsEnabled = showTikTok;
        ToolTip.SetTip(TikTokAnalyticsTab, showTikTok ? null : "TikTok 数据统计未启用");
        AnalyticsPlatformTabs.SelectedItem = showKuaishou ? KuaishouAnalyticsTab : TikTokAnalyticsTab;
        var kuaishouReady = showKuaishou &&
            KuaishouWorkspace.BindAnalytics(KuaishouAnalyticsView);
        HistoryUnavailable.IsVisible = showKuaishou && !kuaishouReady;
        AnalyticsView.IsVisible = !showKuaishou;
        KuaishouAnalyticsView.IsVisible = kuaishouReady;

        if (AnalyticsView.IsVisible) AnalyticsView.Activate();
    }

    private bool IsTikTokArchiveVisible =>
        _activeNavTag == "queue" && _tikTokSubPage == "archived";

    private void OnTikTokLogScopeChanged(object? sender, RoutedEventArgs e)
    {
        if (_activeNavTag == "queue" && _tikTokSubPage == "logs")
            RefreshTikTokLogScope();
    }

    private void RefreshTikTokLogScope() =>
        _viewModel.SetLogAccountScope(TikTokAllLogAccounts.IsChecked == true);

    private void OnTikTokArchiveScopeChanged(object? sender, RoutedEventArgs e)
    {
        if (IsTikTokArchiveVisible)
            RefreshTikTokArchiveScope();
    }

    private void RefreshTikTokArchiveScope()
    {
        _viewModel.ArchivedProjects.UseAccountScope = true;
        _viewModel.ArchivedProjects.ShowAllAccounts = TikTokAllArchiveAccounts.IsChecked == true;
        _viewModel.ArchivedProjects.RefreshCommand.Execute(null);
    }

    private void ApplyStartupLayout()
    {
        var screens = Screens;
        if (screens is null)
            return;

        MainWindowPlacement? saved = null;
        try
        {
            if (AppSettingStore.TryLoadJson<MainWindowPlacement>(LaptopLayout.MainWindowPlacementKey, out var loaded))
                saved = loaded;
        }
        catch
        {
            saved = null;
        }

        var screen = saved is null
            ? screens.Primary
            : screens.ScreenFromPoint(new PixelPoint(saved.X + 16, saved.Y + 16)) ?? screens.Primary;
        if (screen is null)
            return;

        var scale = screen.Scaling <= 0 ? 1d : screen.Scaling;
        var workWidth = screen.WorkingArea.Width / scale;
        var workHeight = screen.WorkingArea.Height / scale;
        if (workWidth < 640 || workHeight < 480)
            return;

        var fit = LaptopLayout.FitWindow(workWidth, workHeight);

        _suspendPlacementTracking = true;
        try
        {
            MinWidth = fit.MinWidth;
            MinHeight = fit.MinHeight;

            ApplyCompactLayout(LaptopLayout.IsCompact(workWidth, workHeight));
            WindowStartupLocation = WindowStartupLocation.Manual;

            var restoreSaved = saved is not null
                && LaptopLayout.PlacementFits(saved.Width, saved.Height, workWidth, workHeight)
                && RestoredWindowMostlyVisible(saved, screen);
            if (restoreSaved && saved is not null)
            {
                Width = saved.Width;
                Height = saved.Height;
                Position = new PixelPoint(saved.X, saved.Y);
                _normalPlacement = saved;
                if (saved.Maximized)
                    WindowState = WindowState.Maximized;
            }
            else
            {
                Width = fit.Width;
                Height = fit.Height;
                var x = screen.WorkingArea.X + (int)Math.Round((screen.WorkingArea.Width - fit.Width * scale) / 2);
                var y = screen.WorkingArea.Y + (int)Math.Round((screen.WorkingArea.Height - fit.Height * scale) / 2);
                Position = new PixelPoint(Math.Max(screen.WorkingArea.X, x), Math.Max(screen.WorkingArea.Y, y));
                _normalPlacement = new MainWindowPlacement
                {
                    X = Position.X,
                    Y = Position.Y,
                    Width = fit.Width,
                    Height = fit.Height,
                };
            }
        }
        finally
        {
            _suspendPlacementTracking = false;
            _startupLayoutApplied = true;
        }
    }

    private void ApplyCompactLayout(bool compact)
    {
        _compactLayout = compact;
        Classes.Set("compact", compact);
        ApplyAccountPanelLayout();
        LogView.SetCompactLayout(compact);
        QueueView.SetCompactLayout(compact);
    }

    private void RememberNormalPlacement()
    {
        if (_suspendPlacementTracking || WindowState != WindowState.Normal)
            return;
        if (double.IsNaN(Width) || double.IsNaN(Height) || Width < 200 || Height < 200)
            return;

        _normalPlacement = new MainWindowPlacement
        {
            X = Position.X,
            Y = Position.Y,
            Width = Width,
            Height = Height,
        };
    }

    private void SavePlacement()
    {
        if (_normalPlacement is null)
            RememberNormalPlacement();
        if (_normalPlacement is null)
            return;

        _normalPlacement.Maximized = WindowState == WindowState.Maximized;
        try
        {
            AppSettingStore.SaveJson(LaptopLayout.MainWindowPlacementKey, _normalPlacement);
        }
        catch
        {
            // 关闭时写窗口位置失败不影响退出。
        }
    }

    private static bool RestoredWindowMostlyVisible(MainWindowPlacement saved, Screen screen)
    {
        var scale = screen.Scaling <= 0 ? 1d : screen.Scaling;
        var window = new PixelRect(
            saved.X,
            saved.Y,
            Math.Max(1, (int)Math.Round(saved.Width * scale)),
            Math.Max(1, (int)Math.Round(saved.Height * scale)));
        var overlap = window.Intersect(screen.WorkingArea);
        var windowArea = (double)window.Width * window.Height;
        var overlapArea = (double)overlap.Width * overlap.Height;
        return windowArea > 0 && overlapArea >= windowArea * 0.6;
    }

    private sealed class MainWindowPlacement
    {
        public int X { get; set; }
        public int Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }
}
