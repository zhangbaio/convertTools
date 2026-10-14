using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Threading;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Desktop;
using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.Services.TikTok;
using TikTokPublisher.Ui.ViewModels;
using TikTokPublisher.Ui.Views;

namespace TikTokPublisher.Desktop.Views;

public partial class MainWindow : Window
{
    private const double CollapsedSidebarWidth = 44;

    private static readonly Dictionary<string, (string Full, string Short)> NavLabels = new(StringComparer.Ordinal)
    {
        ["browser"] = ("浏览器", "浏览器"),
        ["download"] = ("短剧下载", "下载"),
        ["queue"] = ("TikTok 上传", "上传"),
        ["accounts"] = ("账号管理", "账号"),
        ["logs"] = ("运行日志", "日志"),
        ["settings"] = ("系统设置", "设置"),
        ["services"] = ("系统服务", "服务"),
        ["archived"] = ("已归档项目", "归档"),
    };

    private readonly AccountStore _accountStore = new();
    private readonly BrowserSessionHost _browserHost = new();
    private readonly MainViewModel _viewModel;
    private readonly TikTokAccountInventorySyncCoordinator _accountInventorySync;
    private readonly HashSet<string> _externalLoginsInProgress = new(StringComparer.Ordinal);
    private string _activeNavTag = "queue";
    private bool _isSidebarCollapsed;
    private bool _compactLayout;
    private bool _startupLayoutApplied;
    private bool _suspendPlacementTracking;
    private MainWindowPlacement? _normalPlacement;
    private DispatcherTimer? _licenseVerifyTimer;
    private bool _licenseVerifyRunning;
    private bool _licenseLoginDialogOpen;

    public MainWindow()
    {
        var context = new AccountContextService(_accountStore);
        _viewModel = new MainViewModel(_accountStore, context);
        _accountInventorySync = new TikTokAccountInventorySyncCoordinator(_accountStore);
        InitializeComponent();
        DataContext = _viewModel;

        _browserHost.Attach(BrowserHostMount);
        _browserHost.PresentationLayoutChanged += () =>
        {
            if (_activeNavTag == "browser")
                ScheduleBrowserHostMountLayout();
        };
        QueueView.Initialize(_viewModel, _browserHost, EnsureBrowserHostMounted);
        BrowserView.Initialize(_browserHost, _viewModel);
        BrowserView.LayoutUpdated += (_, _) =>
        {
            if (_activeNavTag == "browser")
                SyncBrowserHostMountLayout();
        };
        AccountSidebar.DataContext = _viewModel;
        AccountsView.Bind(_viewModel);
        LogView.Bind(_viewModel, _viewModel.Logs);
        SettingsView.Bind(_viewModel.SystemSettings);
        ServicesView.Bind(_viewModel.SystemServices);
        _viewModel.SystemServices.ConfigImported += () =>
        {
            _viewModel.ReloadAccountsFromDisk();
            _viewModel.SystemSettings.Load();
            _viewModel.SystemServices.Load();
        };
        ArchivedView.Bind(_viewModel.ArchivedProjects);
        DownloadView.Bind(_viewModel.DramaDownload, _viewModel.AppendLog);

        QueueView.OpenBrowserRequested += (_, _) => NavigateTo("browser");
        QueueView.PublishBrowserFocusRequested += account =>
        {
            if (_viewModel.SelectedAccount?.Id != account.Id)
                _viewModel.SelectedAccount = account;
            NavigateTo("browser");
        };
        QueueView.OpenLogsRequested += (_, _) => NavigateTo("logs");
        AccountsView.LoginRequested += (_, _) => BeginAccountLogin(forceRelogin: false);
        AccountsView.ReloginRequested += (_, _) => BeginAccountLogin(forceRelogin: true);
        AccountsView.LogoutRequested += (_, _) => _ = LogoutSelectedTikTokAccountAsync();
        _viewModel.AccountLoginRequested += OnAccountLoginRequested;
        _browserHost.AuthSaved += args => _viewModel.HandleEmbeddedAuthSaved(args.Account, args.Result);
        _browserHost.AuthSaveFailed += _viewModel.HandleEmbeddedAuthSaveFailed;
        _browserHost.AuthStatusChanged += _viewModel.HandleEmbeddedAuthStatusChanged;
        LogView.ReturnRequested += (_, _) => NavigateTo("queue");
        LogView.StopRequested += (_, _) => _viewModel.RequestStopQueue();
        _viewModel.NavigatePageRequested += NavigateTo;
        _viewModel.RemoteQueueRunRequested += QueueView.StartQueueRunFromRemoteAsync;
        _viewModel.RemoteAllQueueRunRequested += QueueView.StartAllQueueRunFromRemoteAsync;
        _viewModel.StartXingeRemoteCommandService();
        _accountInventorySync.StatusChanged += _viewModel.AppendLog;
        _accountInventorySync.Start();
        _viewModel.AccountProfileNetworkChanged += profile => _browserHost.InvalidateHostIfNetworkChanged(profile);
        AccountSidebar.NavigatePageRequested += (_, _) => NavigateTo("accounts");
        _viewModel.AccountSwitchRequested += OnAccountSwitchRequested;
        _viewModel.DailyLimitReached += message =>
            _ = ConfirmDialog.ShowAsync(
                this,
                "单日创建剧集已达上限",
                $"{message}\n\n任务队列已自动停止，请明天再继续上传。");

        SetSidebarCollapsed(false);
        NavigateTo("queue");
        Closed += OnWindowClosed;
        ApplyStartupLayout();
        SizeChanged += (_, _) => RememberNormalPlacement();
        PositionChanged += (_, _) => RememberNormalPlacement();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!_startupLayoutApplied)
            ApplyStartupLayout();
    }

    private double ExpandedSidebarWidth =>
        _compactLayout ? LaptopLayout.CompactSidebarWidth : LaptopLayout.ExpandedSidebarWidth;

    private void OnNavItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && !string.IsNullOrWhiteSpace(tag))
            NavigateTo(tag);
    }

    private void OnSidebarToggleClick(object? sender, RoutedEventArgs e) =>
        SetSidebarCollapsed(!_isSidebarCollapsed);

    private void SetSidebarCollapsed(bool collapsed)
    {
        _isSidebarCollapsed = collapsed;

        var width = collapsed ? CollapsedSidebarWidth : ExpandedSidebarWidth;
        TopLayout.ColumnDefinitions[0].Width = new GridLength(width);
        MainLayout.ColumnDefinitions[0].Width = new GridLength(width);

        AccountSidebar.IsVisible = !collapsed;
        CollapsedSidebarRail.IsVisible = collapsed;
        BrandLabel.IsVisible = !collapsed;
        BrandPane.Padding = collapsed
            ? new Avalonia.Thickness(4, 0)
            : _compactLayout
                ? new Avalonia.Thickness(8, 0, 6, 0)
                : new Avalonia.Thickness(12, 0, 10, 0);

        SidebarToggleButton.Content = collapsed ? "›" : "‹";
        ToolTip.SetTip(SidebarToggleButton, collapsed ? "展开左侧面板" : "收起左侧面板");
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        SavePlacement();
        _licenseVerifyTimer?.Stop();
        _licenseVerifyTimer = null;
        _viewModel.StopXingeRemoteCommandService();
        _accountInventorySync.StatusChanged -= _viewModel.AppendLog;
        _accountInventorySync.Dispose();
        _browserHost.CloseAll();
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
        _viewModel.StatusMessage = message;
        _viewModel.RequestStopQueue();
        HideForLicenseGate();

        var loggedIn = await ShowLicenseLoginDialogAsync(message, standalone: true);
        if (loggedIn)
        {
            _viewModel.StatusMessage = "软件授权登录成功";
            ShowAfterLicenseGate();
            StartLicenseVerifyTimer();
            return;
        }

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
        _viewModel.SystemServices.Load();
        _viewModel.SystemServices.RefreshLicenseSummaryDisplay();
        _viewModel.StartXingeRemoteCommandService();
    }

    private void SaveVerifiedLicenseState(LicenseState state)
    {
        LicenseGate.SaveVerifiedState(state);
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

        NavigateTo("browser");
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
        if (_activeNavTag == "browser")
            ScheduleBrowserHostMountLayout();
    }

    private void EnsureBrowserHostMounted()
    {
        // 浏览器页正在展示时不得折叠挂载层/关闭渲染（否则页面变成不绘制的黑色原生窗口）。
        if (_activeNavTag == "browser")
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
        if (_activeNavTag != "browser" || !BrowserView.IsVisible)
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
        if (unchanged)
        {
            _browserHost.RefreshPresentationBounds();
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

    private void NavigateTo(string tag)
    {
        _activeNavTag = tag;
        UpdateNavHighlight();
        ShowPage(tag);
    }

    private void UpdateNavHighlight()
    {
        foreach (var child in ModuleNav.Children)
        {
            if (child is not Button btn) continue;
            var tag = btn.Tag?.ToString() ?? "";
            var isActive = string.Equals(tag, _activeNavTag, StringComparison.OrdinalIgnoreCase);
            btn.Classes.Set("topNavItemActive", isActive);
            if (isActive && btn.IsLoaded)
                btn.BringIntoView();
        }
    }

    private void ShowPage(string tag)
    {
        var browserActive = tag == "browser";
        const int activeZ = 10;
        const int inactiveZ = 0;

        DownloadView.IsVisible = tag == "download";
        QueueView.IsVisible = tag == "queue";
        AccountsView.IsVisible = tag == "accounts";
        LogView.IsVisible = tag == "logs";
        SettingsView.IsVisible = tag == "settings";
        ServicesView.IsVisible = tag == "services";
        ArchivedView.IsVisible = tag == "archived";

        DownloadView.ZIndex = tag == "download" ? activeZ : inactiveZ;
        QueueView.ZIndex = tag == "queue" ? activeZ : inactiveZ;
        AccountsView.ZIndex = tag == "accounts" ? activeZ : inactiveZ;
        LogView.ZIndex = tag == "logs" ? activeZ : inactiveZ;
        SettingsView.ZIndex = tag == "settings" ? activeZ : inactiveZ;
        ServicesView.ZIndex = tag == "services" ? activeZ : inactiveZ;
        ArchivedView.ZIndex = tag == "archived" ? activeZ : inactiveZ;

        if (browserActive)
        {
            BrowserView.IsVisible = true;
            BrowserView.IsHitTestVisible = true;
            BrowserView.ZIndex = activeZ;
            _browserHost.ShowAccount(_viewModel.SelectedAccount);
            SyncBrowserHostMountLayout();
            _browserHost.SetPresentationVisible(true);
            ScheduleBrowserHostMountLayout();
        }
        else
        {
            BrowserView.IsVisible = false;
            BrowserView.ZIndex = inactiveZ;
            CollapseBrowserHostMount();
            _browserHost.SetPresentationVisible(false);
        }

        if (tag == "logs")
            _viewModel.RefreshLogSnapshot();
        if (tag == "archived")
            _viewModel.ArchivedProjects.RefreshCommand.Execute(null);
        if (tag == "services")
        {
            _viewModel.SystemServices.Load();
            _viewModel.SystemServices.RefreshLicenseSummaryDisplay();
        }
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

            ApplyCompactLayout(LaptopLayout.IsCompact(workWidth, workHeight), LaptopLayout.ShouldShortenNav(workWidth));
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

    private void ApplyCompactLayout(bool compact, bool shortenNav)
    {
        _compactLayout = compact;
        Classes.Set("compact", compact);
        ApplyNavLabels(shortenNav);
        SetSidebarCollapsed(_isSidebarCollapsed);
        LogView.SetCompactLayout(compact);
        QueueView.SetCompactLayout(compact);
    }

    private void ApplyNavLabels(bool shorten)
    {
        foreach (var child in ModuleNav.Children)
        {
            if (child is not Button button || button.Tag is not string tag)
                continue;
            if (!NavLabels.TryGetValue(tag, out var labels))
                continue;

            var useShort = shorten && !string.Equals(labels.Short, labels.Full, StringComparison.Ordinal);
            button.Content = useShort ? labels.Short : labels.Full;
            ToolTip.SetTip(button, useShort ? labels.Full : null);
        }
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
