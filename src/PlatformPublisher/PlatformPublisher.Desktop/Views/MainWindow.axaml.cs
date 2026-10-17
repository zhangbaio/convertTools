using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using PlatformPublisher.Persistence;
using TikTokPublisher.Ui.Services;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Weixin.Publishing;
using PlatformPublisher.Kuaishou.Publishing;
using TikTokPublisher.Ui.ViewModels;
using TikTokPublisher.Ui.Views;
using PlatformPublisher.Adx.Automation;
using PlatformPublisher.Adx.Storage;
using PlatformPublisher.Analytics.Models;
using PlatformPublisher.Publishing.Models;
using ShortDrama.Core.Interfaces;
using ShortDrama.Desktop.Services;

namespace PlatformPublisher.Desktop.Views;

public partial class MainWindow : Window
{
    private const string PlacementKey = "ui.yunfan_main_window_placement";
    private const double CollapsedSidebarWidth = 42;
    private PlatformDatabase? _platformDatabase;
    private DatabaseBackupService? _databaseBackupService;
    private IJsonSettingStore? _windowSettings;
    private bool _isGlobalSidebarCollapsed;
    private bool _compactLayout;
    private bool _startupLayoutApplied;
    private bool _suspendPlacementTracking;
    private MainWindowPlacement? _normalPlacement;

    private double ExpandedSidebarWidth =>
        _compactLayout ? LaptopLayout.CompactSidebarWidth : LaptopLayout.ExpandedSidebarWidth;

    public MainWindow()
    {
        InitializeComponent();
        WeixinPublisherView.SetSidebarVisible(false);
        GlobalAccountSidebar.UseShellChrome();
        KuaishouWorkflowPage.KuaishouConfigRequested += (_, _) =>
            OpenKuaishouPersonalConfig_Click(null, new RoutedEventArgs());
        KuaishouWorkflowPage.SettingsRequested += (_, _) => ShowSettingsPage();
        Opened += (_, _) =>
        {
            if (!_startupLayoutApplied)
                ApplyStartupLayout();
            ShowWeixinPage();
        };
        Closed += (_, _) => SavePlacement();
        SizeChanged += (_, _) => RememberNormalPlacement();
        PositionChanged += (_, _) => RememberNormalPlacement();
    }

    public void BindWindowPlacement(IJsonSettingStore settingsStore) => _windowSettings = settingsStore;

    public void BindSettings(SystemSettingsViewModel viewModel)
    {
        SharedSettingsView.Bind(viewModel);
        viewModel.Load();
        viewModel.SettingsSaved += _ => ViewModel?.RefreshDramaSourceStatus();
    }

    public void BindAdxSettings(AdxAutomationService service) => AdxSettingsView.Bind(service);

    public void BindDatabaseMaintenance(PlatformDatabase database,DatabaseBackupService backupService)
    {
        _platformDatabase=database;_databaseBackupService=backupService;
    }

    public void BindWeixinSeries(IPlatformPublishAdapter adapter)
    {
        var seriesView = new WeixinSeriesUploadView();
        seriesView.Bind(adapter, () => WeixinPublisherView.SelectedAccountProfile);
        WeixinPublisherView.SetSeriesPublishContent(seriesView);
    }

    public void BindWeixinWorkflow(MainWindowViewModel viewModel, IProjectScanner projectScanner,
        AdxAutomationService adxService, AdxBatchStore adxBatchStore,
        WeixinDirectoryMaterialPublishService directoryPublishService,
        WeixinMaterialDownloadService materialDownloadService,
        WeixinHighlightScheduleService highlightScheduleService,
        WeixinMaterialChannelVideoDeleteService channelVideoDeleteService,
        UnifiedPublishViewModel unifiedPublishViewModel)
    {
        unifiedPublishViewModel.BindAccounts(() => WeixinPublisherView.AccountProfiles.Select((account,index) =>
            new PublishTarget(account.Id,account.Name,account.ProfileDir,index)).ToArray());
        var unifiedPublishView=new UnifiedPublishView{DataContext=unifiedPublishViewModel};
        WeixinPublisherView.SetUnifiedPublishContent(unifiedPublishView);
        var workflowView = new WeixinWorkflowView { DataContext = viewModel };
        workflowView.Bind(() => WeixinPublisherView.SelectedAccountProfile);
        workflowView.SettingsRequested += (_, _) => ShowSettingsPage();
        var materialView = new WeixinMaterialUploadView();
        materialView.Bind(
            () => WeixinPublisherView.AccountProfiles,
            () => WeixinPublisherView.SelectedAccountProfile,
            WeixinPublisherView.SelectAccount,
            viewModel,
            projectScanner,
            adxService,
            adxBatchStore,
            directoryPublishService,
            materialDownloadService,
            highlightScheduleService,
            channelVideoDeleteService,
            unifiedPublishViewModel,
            WeixinPublisherView.ShowUnifiedPublish,
            ShowSettingsPage);
        WeixinPublisherView.SelectedAccountChanged += account =>
        {
            workflowView.ApplyAccount(account);
            materialView.ApplyAccount(account);
            unifiedPublishViewModel.RefreshCommand.Execute(null);
        };
        WeixinPublisherView.SetWorkflowContent(workflowView);
        WeixinPublisherView.SetMaterialWorkflowContent(materialView);
        WeixinPublisherView.SetRuntimeLogContent(new WeixinRuntimeLogView { DataContext = viewModel });
        WeixinPublisherView.SetArchivedProjectsContent(new WeixinArchivedProjectsView { DataContext = viewModel });
    }

    public void BindKuaishouAdx(AdxAutomationService adxService, AdxBatchStore adxBatchStore,
        KuaishouAdxBatchResolver resolver)
    {
        KuaishouWorkflowPage.KuaishouAdxRequested += async (_, request) =>
        {
            var context = ViewModel?.GetKuaishouAdxProjectContext();
            if (context is null)
            {
                if (ViewModel is not null)
                    ViewModel.StatusMessage = "请先在快手分账个人版列表中选择一个剧集项目。";
                return;
            }
            if (string.IsNullOrWhiteSpace(context.OriginalTitle) || string.IsNullOrWhiteSpace(context.NewTitle))
            {
                ViewModel!.StatusMessage = "所选项目必须同时包含原剧名和新剧名。";
                return;
            }
            var dialog = new KuaishouAdxMaterialsDialog(adxService, adxBatchStore, resolver,
                context, request.TopCount, request.PublishLocal, ViewModel!.QueueKuaishouAdxPublishAsync);
            await dialog.ShowDialog(this);
        };
    }

    public void BindAccountDatabase(ChannelsPublisher.Core.Services.AccountStore accountStore)
    {
        WeixinPublisherView.UseAccountStore(accountStore);
        GlobalAccountSidebar.DataContext = WeixinPublisherView.DataContext;
        WeixinPublisherView.SelectedAccountChanged += account =>
            ViewModel?.UseGlobalAccounts(WeixinPublisherView.AccountProfiles, account);
        ViewModel?.UseGlobalAccounts(WeixinPublisherView.AccountProfiles, WeixinPublisherView.SelectedAccountProfile);
    }

    public void BindLegacySessionImport(LegacyAccountSessionImportService importService)
    {
        WeixinPublisherView.LegacySessionImportRequested += async (_, _) =>
        {
            var dialog = new LegacySessionImportDialog(importService, WeixinPublisherView.AccountProfiles);
            if (!await dialog.ShowDialog<bool>(this) || dialog.ImportedAccounts.Count == 0) return;
            WeixinPublisherView.RefreshImportedAccounts(dialog.ImportedAccounts);
            ViewModel?.UseGlobalAccounts(WeixinPublisherView.AccountProfiles, WeixinPublisherView.SelectedAccountProfile);
            if (ViewModel is not null)
                ViewModel.StatusMessage = $"已导入 {dialog.ImportedAccounts.Count} 个旧版账号登录状态。";
        };
    }

    public void BindWeixinDownload(MainWindowViewModel mainViewModel)
    {
        var viewModel = new DramaDownloadViewModel(PlatformPublisherPaths.SettingsDatabasePath);
        viewModel.ConfigurePlatformQueues(
        [
            (DramaQueuePlatforms.KuaishouPersonal, "快手分账个人"),
            (DramaQueuePlatforms.KuaishouEnterprise, "快手分账企业"),
            (DramaQueuePlatforms.Weixin, "视频号"),
        ]);
        viewModel.PlatformQueueTargetRequested += ResolveDramaQueueTarget;
        viewModel.TikTokQueueTargetRequested += () =>
        {
            var account = WeixinPublisherView.SelectedAccountProfile;
            return account is null
                ? null
                : new TikTokQueueImportTarget(account.Id, account.Name, account.WorkRootDirectory ?? string.Empty);
        };
        viewModel.TikTokQueueTargetsRequested += ListDramaQueueTargets;
        viewModel.ImportToQueueRequested += async request =>
        {
            await mainViewModel.ImportDownloadedProjectsAsync(request);
        };
        var view = new DramaDownloadView();
        view.Bind(viewModel, message => mainViewModel.StatusMessage = message);
        viewModel.SearchViewMode = "封面视图";
        void RefreshTarget()
        {
            var account = WeixinPublisherView.SelectedAccountProfile;
            viewModel.UpdateTikTokQueueTarget(account is null
                ? null
                : new TikTokQueueImportTarget(account.Id, account.Name, account.WorkRootDirectory ?? string.Empty));
        }
        WeixinPublisherView.SelectedAccountChanged += _ => RefreshTarget();
        mainViewModel.ActiveAccountWorkRootDirectoryChanged += RefreshTarget;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DramaDownloadViewModel.DownloadWorkspace))
                RefreshTarget();
        };
        RefreshTarget();
        DramaDownloadHost.Content = view;
        WeixinPublisherView.SetDramaDownloadContent(CreateDramaDownloadShortcut());
    }

    private Control CreateDramaDownloadShortcut()
    {
        var panel = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(new TextBlock
        {
            Text = "短剧搜索和下载在顶部「短剧下载」。可以把选中的剧加入快手分账个人、快手分账企业或视频号队列。",
            Foreground = new SolidColorBrush(Color.Parse("#64748B")),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
            TextAlignment = TextAlignment.Center,
        });
        var button = new Button
        {
            Content = "打开短剧下载",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        button.Click += (_, _) => ShowDramaDownloadPage();
        panel.Children.Add(button);
        return panel;
    }

    private TikTokQueueImportTarget? ResolveDramaQueueTarget(string platformKey)
    {
        var account = WeixinPublisherView.SelectedAccountProfile;
        if (account is null) return null;
        var workspace = ResolveDramaQueueWorkspace(account, platformKey);
        if (workspace is null) return null;
        return new TikTokQueueImportTarget(
            account.Id,
            account.Name,
            workspace,
            platformKey,
            DramaQueuePlatformLabel(platformKey));
    }

    private IReadOnlyList<TikTokQueueImportTarget> ListDramaQueueTargets()
    {
        var targets = new List<TikTokQueueImportTarget>();
        foreach (var account in WeixinPublisherView.AccountProfiles)
        {
            foreach (var platformKey in new[]
                     {
                         DramaQueuePlatforms.KuaishouPersonal,
                         DramaQueuePlatforms.KuaishouEnterprise,
                         DramaQueuePlatforms.Weixin,
                     })
            {
                var workspace = ResolveDramaQueueWorkspace(account, platformKey);
                if (workspace is null) continue;
                targets.Add(new TikTokQueueImportTarget(
                    account.Id,
                    account.Name,
                    workspace,
                    platformKey,
                    DramaQueuePlatformLabel(platformKey)));
            }
        }
        return targets;
    }

    private static string? ResolveDramaQueueWorkspace(ChannelsPublisher.Core.Models.PublishAccount account, string platformKey)
    {
        var configPath = platformKey switch
        {
            DramaQueuePlatforms.KuaishouPersonal => account.KuaishouPersonalConfigPath,
            DramaQueuePlatforms.KuaishouEnterprise => account.KuaishouEnterpriseConfigPath,
            _ => string.Empty,
        };
        if (platformKey is DramaQueuePlatforms.KuaishouPersonal or DramaQueuePlatforms.KuaishouEnterprise &&
            (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath)))
            return null;
        var configured = ReadConfigWorkspace(configPath);
        if (Directory.Exists(configured)) return Path.GetFullPath(configured);
        return Directory.Exists(account.WorkRootDirectory) ? Path.GetFullPath(account.WorkRootDirectory) : null;
    }

    private static string ReadConfigWorkspace(string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath)) return string.Empty;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
            if (document.RootElement.TryGetProperty("LastWorkspace", out var workspace))
                return workspace.GetString()?.Trim() ?? string.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
        }
        catch (IOException)
        {
        }
        return string.Empty;
    }

    private static string DramaQueuePlatformLabel(string platformKey) => platformKey switch
    {
        DramaQueuePlatforms.KuaishouPersonal => "快手分账个人",
        DramaQueuePlatforms.KuaishouEnterprise => "快手分账企业",
        _ => "视频号",
    };

    public void BindAnalytics(AnalyticsViewModel analyticsViewModel, MainWindowViewModel mainViewModel)
    {
        analyticsViewModel.Bind(
            () => WeixinPublisherView.AccountProfiles
                .Select(account => new AnalyticsAccount(account.Id, PublishPlatform.WeixinChannel, account.Name, account.ProfileDir))
                .Concat(mainViewModel.ListAnalyticsAccounts().Where(account => account.Platform != PublishPlatform.WeixinChannel))
                .ToArray(),
            async (accountId, cancellationToken) =>
            {
                if (mainViewModel.IsBusy || WeixinPublisherView.HasActivePublish)
                    throw new InvalidOperationException("当前有发布任务运行，暂时不能采集视频号数据。");
                return await WeixinPublisherView.EnsureAccountCdpEndpointAsync(accountId, cancellationToken);
            });
        AnalyticsPage.DataContext = analyticsViewModel;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnGlobalSidebarToggleClick(object? sender, RoutedEventArgs e)
    {
        _isGlobalSidebarCollapsed = !_isGlobalSidebarCollapsed;
        ApplySidebarWidth();
    }

    private void OnGlobalAccountSettingsRequested(object? sender, EventArgs e)
    {
        ShowWeixinPage();
        WeixinPublisherView.ShowAccountSettings();
    }

    private void OnWeixinNavClick(object? sender, RoutedEventArgs e) => ShowWeixinPage();

    private void OnDramaDownloadNavClick(object? sender, RoutedEventArgs e) => ShowDramaDownloadPage();

    private void OnKuaishouPersonalNavClick(object? sender, RoutedEventArgs e) =>
        ShowKuaishouPage(PublishPlatform.KuaishouPersonalRevenue);

    private void OnKuaishouEnterpriseNavClick(object? sender, RoutedEventArgs e) =>
        ShowKuaishouPage(PublishPlatform.KuaishouEnterpriseRevenue);

    private void OnSettingsNavClick(object? sender, RoutedEventArgs e) => ShowSettingsPage();
    private void OnAnalyticsNavClick(object? sender, RoutedEventArgs e) => ShowAnalyticsPage();
    private async void OnDatabaseClick(object? sender,RoutedEventArgs e)
    {
        if(_platformDatabase is null||_databaseBackupService is null)return;
        await new DatabaseMaintenanceDialog(_platformDatabase,_databaseBackupService).ShowDialog(this);
    }

    private void ShowWeixinPage()
    {
        ViewModel?.SelectPlatform(PublishPlatform.WeixinChannel);
        WeixinPublisherView.IsVisible = true;
        DramaDownloadPage.IsVisible = false;
        PipelinePage.IsVisible = false;
        KuaishouWorkflowPage.IsVisible = false;
        SettingsPage.IsVisible = false;
        AnalyticsPage.IsVisible = false;
        SetActiveNavigation(WeixinNavButton);
    }

    private void ShowDramaDownloadPage()
    {
        WeixinPublisherView.IsVisible = false;
        DramaDownloadPage.IsVisible = true;
        PipelinePage.IsVisible = false;
        KuaishouWorkflowPage.IsVisible = false;
        SettingsPage.IsVisible = false;
        AnalyticsPage.IsVisible = false;
        SetActiveNavigation(DramaDownloadNavButton);
    }

    private void ShowKuaishouPage(PublishPlatform platform)
    {
        ViewModel?.SelectPlatform(platform);
        WeixinPublisherView.IsVisible = false;
        DramaDownloadPage.IsVisible = false;
        PipelinePage.IsVisible = true;
        PipelineContent.IsVisible = false;
        KuaishouWorkflowPage.IsVisible = true;
        KuaishouWorkflowPage.IsKuaishouMode = true;
        SettingsPage.IsVisible = false;
        AnalyticsPage.IsVisible = false;
        SetActiveNavigation(platform == PublishPlatform.KuaishouPersonalRevenue
            ? KuaishouPersonalNavButton
            : KuaishouEnterpriseNavButton);
    }

    private void ShowSettingsPage()
    {
        WeixinPublisherView.IsVisible = false;
        DramaDownloadPage.IsVisible = false;
        PipelinePage.IsVisible = true;
        PipelineContent.IsVisible = false;
        KuaishouWorkflowPage.IsVisible = false;
        SettingsPage.IsVisible = true;
        AnalyticsPage.IsVisible = false;
        SetActiveNavigation(SettingsNavButton);
    }

    private void ShowAnalyticsPage()
    {
        WeixinPublisherView.IsVisible = false;
        DramaDownloadPage.IsVisible = false;
        PipelinePage.IsVisible = false;
        KuaishouWorkflowPage.IsVisible = false;
        SettingsPage.IsVisible = false;
        AnalyticsPage.IsVisible = true;
        SetActiveNavigation(AnalyticsNavButton);
    }

    private void SetActiveNavigation(Button activeButton)
    {
        foreach (var button in new[]
                 {
                     WeixinNavButton,
                     DramaDownloadNavButton,
                     KuaishouPersonalNavButton,
                     KuaishouEnterpriseNavButton,
                     AnalyticsNavButton,
                     SettingsNavButton,
                 })
        {
            SetActiveNav(button, ReferenceEquals(button, activeButton));
        }
    }

    private static void SetActiveNav(Button button, bool active)
    {
        if (active)
            button.Classes.Add("navActive");
        else
            button.Classes.Remove("navActive");
    }

    private async void PickProjectDirectory_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择短剧项目目录",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && ViewModel is not null)
            ViewModel.DraftProjectDirectory = folders[0].Path.LocalPath;
    }

    private async void PickConfigFile_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择平台账号配置",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON 配置") { Patterns = ["*.json"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count > 0 && ViewModel is not null)
        {
            var path = files[0].Path.LocalPath;
            if (ViewModel.SelectedPlatform.Value is PublishPlatform.KuaishouPersonalRevenue or PublishPlatform.KuaishouEnterpriseRevenue)
                await ViewModel.UpdateSelectedAccountConfigPathAsync(path);
            else
                ViewModel.DraftConfigPath = path;
        }
    }

    private async void PickCustomVideoFiles_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要发表的视频",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("视频文件")
                {
                    Patterns = ["*.mp4", "*.mov", "*.m4v", "*.mkv", "*.avi", "*.flv", "*.ts", "*.wmv", "*.webm"],
                },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count == 0 || ViewModel is null)
            return;

        var paths = files.Select(file => file.Path.LocalPath).ToArray();
        ViewModel.DraftCustomVideoFilesText = string.Join(Environment.NewLine, paths);
        if (!Directory.Exists(ViewModel.DraftProjectDirectory))
            ViewModel.DraftProjectDirectory = Path.GetDirectoryName(paths[0]) ?? string.Empty;
    }

    private async void OpenWeixinPublishConfig_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        var draftJob = new PublishJob
        {
            PlatformOptionsJson = ViewModel.DraftPlatformOptionsJson,
            PublishDescription = ViewModel.DraftPublishDescription,
            DeclareOriginal = ViewModel.DraftDeclareOriginal,
            HideLocation = ViewModel.DraftHideLocation,
        };
        var result = await WeixinPublishConfigDialog.ShowAsync(this, WeixinPublishOptions.FromJob(draftJob));
        if (result is null)
            return;

        ViewModel.DraftPlatformOptionsJson = result.ToJson();
        ViewModel.DraftPublishDescription = result.DescriptionTemplate;
        ViewModel.DraftDeclareOriginal = result.DeclareOriginal;
        ViewModel.DraftHideLocation = !string.IsNullOrWhiteSpace(result.LocationOptionText);
        ViewModel.StatusMessage = "视频号高级发表配置已应用到新任务。";
    }

    private async void OpenKuaishouPersonalConfig_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel?.SelectedAccount is null)
            {
                if (ViewModel is not null) ViewModel.StatusMessage = "请先在左侧添加并选择账号。";
                await InfoDialog.ShowAsync(this, "请先在左侧点击「添加账号」，选中账号后再打开快手分账配置。", "快手分账配置");
                return;
            }

            var account = ViewModel.SelectedAccount.Model;
            var platform = ViewModel.SelectedPlatform.Value;
            var configuredPath = account.BaseConfigPath?.Trim() ?? string.Empty;
            var path = string.IsNullOrWhiteSpace(configuredPath)
                ? KuaishouPersonalConfig.DefaultConfigPath(account.Id, platform)
                : Path.GetFullPath(configuredPath);
            var config = KuaishouPersonalConfig.Load(new PublishJob
            {
                Platform = platform,
                AccountId = account.Id,
                ConfigPath = File.Exists(path) ? path : string.Empty,
            });
            var result = await KuaishouPersonalConfigDialog.ShowAsync(this, config, platform);
            if (result is null) return;
            await result.SaveAsync(path);
            await ViewModel.UpdateSelectedAccountConfigPathAsync(path);
        }
        catch (Exception ex)
        {
            if (ViewModel is not null)
                ViewModel.StatusMessage = "打开快手分账配置失败：" + ex.Message;
            await InfoDialog.ShowAsync(this, ex.Message, "打开快手分账配置失败");
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
            if (_windowSettings is not null &&
                _windowSettings.TryLoad<MainWindowPlacement>(PlacementKey, out var loaded))
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
        ApplySidebarWidth();
        BrandTitle.Text = compact ? "云帆" : "云帆多平台发布助手";
        ToolTip.SetTip(BrandTitle, compact ? "云帆多平台发布助手" : null);
        SetNavLabel(WeixinNavButton, "视频号助手", "视频号", shortenNav);
        SetNavLabel(DramaDownloadNavButton, "短剧下载", "下载", shortenNav);
        SetNavLabel(KuaishouPersonalNavButton, "快手分账 · 个人", "快手个人", shortenNav);
        SetNavLabel(KuaishouEnterpriseNavButton, "快手分账 · 企业", "快手企业", shortenNav);
        SetNavLabel(AnalyticsNavButton, "数据统计", "统计", shortenNav);
        SetNavLabel(DatabaseNavButton, "数据库", "数据库", shortenNav);
        SetNavLabel(SettingsNavButton, "系统设置", "设置", shortenNav);
    }

    private void ApplySidebarWidth()
    {
        ShellLayout.ColumnDefinitions[0].Width = new GridLength(
            _isGlobalSidebarCollapsed ? CollapsedSidebarWidth : ExpandedSidebarWidth);
        ExpandedGlobalSidebar.IsVisible = !_isGlobalSidebarCollapsed;
        CollapsedGlobalSidebar.IsVisible = _isGlobalSidebarCollapsed;
    }

    private static void SetNavLabel(Button button, string full, string shortLabel, bool shorten)
    {
        var useShort = shorten && !string.Equals(shortLabel, full, StringComparison.Ordinal);
        button.Content = useShort ? shortLabel : full;
        ToolTip.SetTip(button, useShort ? full : null);
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
        if (_windowSettings is null)
            return;
        if (_normalPlacement is null)
            RememberNormalPlacement();
        if (_normalPlacement is null)
            return;

        _normalPlacement.Maximized = WindowState == WindowState.Maximized;
        try
        {
            _windowSettings.Save(PlacementKey, _normalPlacement);
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
