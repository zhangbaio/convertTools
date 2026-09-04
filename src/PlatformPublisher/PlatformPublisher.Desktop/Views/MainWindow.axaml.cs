using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Weixin.Publishing;
using PlatformPublisher.Kuaishou.Publishing;
using TikTokPublisher.Ui.ViewModels;
using PlatformPublisher.Adx.Automation;
using PlatformPublisher.Adx.Storage;
using PlatformPublisher.Analytics.Models;
using PlatformPublisher.Persistence;
using PlatformPublisher.Publishing.Models;
using ShortDrama.Core.Interfaces;
using ShortDrama.Desktop.Services;

namespace PlatformPublisher.Desktop.Views;

public partial class MainWindow : Window
{
    private const double ExpandedSidebarWidth = 198;
    private const double CollapsedSidebarWidth = 42;
    private PlatformDatabase? _platformDatabase;
    private DatabaseBackupService? _databaseBackupService;
    private bool _isGlobalSidebarCollapsed;

    public MainWindow()
    {
        InitializeComponent();
        WeixinPublisherView.SetSidebarVisible(false);
        GlobalAccountSidebar.UseShellChrome();
        KuaishouWorkflowPage.KuaishouConfigRequested += (_, _) =>
            OpenKuaishouPersonalConfig_Click(null, new RoutedEventArgs());
        KuaishouWorkflowPage.SettingsRequested += (_, _) => ShowSettingsPage();
        Opened += (_, _) => ShowWeixinPage();
    }

    public void BindSettings(SystemSettingsViewModel viewModel)
    {
        SharedSettingsView.Bind(viewModel);
        viewModel.Load();
        viewModel.SettingsSaved += _ => ViewModel?.RefreshDramaSourceStatus();
    }

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
            WeixinPublisherView.ShowUnifiedPublish);
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
        var viewModel = new TikTokPublisher.Ui.ViewModels.DramaDownloadViewModel(PlatformPublisherPaths.SettingsDatabasePath);
        viewModel.ConfigureQueuePlatform("视频号");
        viewModel.TikTokQueueTargetRequested += () =>
        {
            var account = WeixinPublisherView.SelectedAccountProfile;
            var workspace = account?.WorkRootDirectory;
            if (account is null || string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace)) return null;
            return new TikTokPublisher.Ui.ViewModels.TikTokQueueImportTarget(account.Id, account.Name, workspace);
        };
        viewModel.ImportToQueueRequested += async request =>
        {
            await mainViewModel.ImportVideoChannelProjectDirectoriesAsync(request);
        };
        var view = new TikTokPublisher.Ui.Views.DramaDownloadView();
        view.Bind(viewModel, message => mainViewModel.StatusMessage = message);
        viewModel.SearchViewMode = "封面视图";
        void RefreshTarget()
        {
            var account = WeixinPublisherView.SelectedAccountProfile;
            var workspace = account?.WorkRootDirectory;
            viewModel.UpdateTikTokQueueTarget(account is null || string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace)
                ? null
                : new TikTokPublisher.Ui.ViewModels.TikTokQueueImportTarget(account.Id, account.Name, workspace));
        }
        WeixinPublisherView.SelectedAccountChanged += _ => RefreshTarget();
        mainViewModel.ActiveAccountWorkRootDirectoryChanged += RefreshTarget;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TikTokPublisher.Ui.ViewModels.DramaDownloadViewModel.DownloadWorkspace))
                RefreshTarget();
        };
        RefreshTarget();
        WeixinPublisherView.SetDramaDownloadContent(view);
    }

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
        ShellLayout.ColumnDefinitions[0].Width = new GridLength(
            _isGlobalSidebarCollapsed ? CollapsedSidebarWidth : ExpandedSidebarWidth);
        ExpandedGlobalSidebar.IsVisible = !_isGlobalSidebarCollapsed;
        CollapsedGlobalSidebar.IsVisible = _isGlobalSidebarCollapsed;
    }

    private void OnGlobalAccountSettingsRequested(object? sender, EventArgs e)
    {
        ShowWeixinPage();
        WeixinPublisherView.ShowAccountSettings();
    }

    private void OnWeixinNavClick(object? sender, RoutedEventArgs e) => ShowWeixinPage();

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
        PipelinePage.IsVisible = false;
        KuaishouWorkflowPage.IsVisible = false;
        SharedSettingsView.IsVisible = false;
        AnalyticsPage.IsVisible = false;
        SetActiveNavigation(WeixinNavButton);
    }

    private void ShowKuaishouPage(PublishPlatform platform)
    {
        ViewModel?.SelectPlatform(platform);
        WeixinPublisherView.IsVisible = false;
        PipelinePage.IsVisible = true;
        PipelineContent.IsVisible = false;
        KuaishouWorkflowPage.IsVisible = true;
        SharedSettingsView.IsVisible = false;
        AnalyticsPage.IsVisible = false;
        SetActiveNavigation(platform == PublishPlatform.KuaishouPersonalRevenue
            ? KuaishouPersonalNavButton
            : KuaishouEnterpriseNavButton);
    }

    private void ShowSettingsPage()
    {
        WeixinPublisherView.IsVisible = false;
        PipelinePage.IsVisible = true;
        PipelineContent.IsVisible = false;
        KuaishouWorkflowPage.IsVisible = false;
        SharedSettingsView.IsVisible = true;
        AnalyticsPage.IsVisible = false;
        SetActiveNavigation(SettingsNavButton);
    }

    private void ShowAnalyticsPage()
    {
        WeixinPublisherView.IsVisible = false;
        PipelinePage.IsVisible = false;
        KuaishouWorkflowPage.IsVisible = false;
        SharedSettingsView.IsVisible = false;
        AnalyticsPage.IsVisible = true;
        SetActiveNavigation(AnalyticsNavButton);
    }

    private void SetActiveNavigation(Button activeButton)
    {
        foreach (var button in new[]
                 {
                     WeixinNavButton,
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
        if (ViewModel?.SelectedAccount is null)
        {
            if (ViewModel is not null) ViewModel.StatusMessage = "请先在左侧选择全局账号。";
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
}
