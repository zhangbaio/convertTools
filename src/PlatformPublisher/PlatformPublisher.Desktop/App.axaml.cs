using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Kuaishou.Publishing;
using PlatformPublisher.Weixin.Publishing;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Desktop.Views;
using ShortDrama.Infrastructure.DependencyInjection;
using TikTokPublisher.Ui.ViewModels;
using PlatformPublisher.Adx.Automation;
using PlatformPublisher.Adx.Security;
using PlatformPublisher.Adx.Storage;
using PlatformPublisher.Analytics.Services;
using PlatformPublisher.Analytics.Storage;
using PlatformPublisher.Kuaishou.Analytics;
using PlatformPublisher.Weixin.Analytics;
using PlatformPublisher.Persistence;
using PlatformPublisher.Materials;
using PlatformPublisher.Publishing.Execution;
using PlatformPublisher.Publishing.Storage;

namespace PlatformPublisher.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = BuildServices();
            _ = _services.GetRequiredService<AnalyticsRepository>();
            _services.GetRequiredService<LegacyDatabaseImporter>().Import(
                PlatformPublisherPaths.LegacySettingsDatabasePath,
                PlatformPublisherPaths.LegacyAnalyticsDatabasePath);
            KuaishouPersonalConfig.ConfigureDatabase(_services.GetRequiredService<AccountJsonSettingStore>());
            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            var migratedDrafts=_services.GetRequiredService<LegacyPublishDraftMigrator>().MigrateAsync().GetAwaiter().GetResult();
            var settingsViewModel = _services.GetRequiredService<SystemSettingsViewModel>();
            var publishCoordinator = _services.GetRequiredService<PlatformPublishCoordinator>();
            var mainWindow = new MainWindow { DataContext = viewModel };
            mainWindow.BindDatabaseMaintenance(_services.GetRequiredService<PlatformDatabase>(),_services.GetRequiredService<DatabaseBackupService>());
            mainWindow.BindAccountDatabase(_services.GetRequiredService<ChannelsPublisher.Core.Services.AccountStore>());
            mainWindow.BindSettings(settingsViewModel);
            mainWindow.BindWeixinSeries(publishCoordinator.GetAdapter(PublishPlatform.WeixinChannel));
            mainWindow.BindWeixinWorkflow(viewModel, _services.GetRequiredService<AdxAutomationService>(), _services.GetRequiredService<AdxBatchStore>(), _services.GetRequiredService<UnifiedPublishViewModel>());
            if(migratedDrafts>0)viewModel.StatusMessage=$"已将 {migratedDrafts} 个旧素材任务迁移为统一发布草稿。";
            mainWindow.BindWeixinDownload(viewModel);
            mainWindow.BindAnalytics(_services.GetRequiredService<AnalyticsViewModel>(), viewModel);
            desktop.MainWindow = mainWindow;
            desktop.Exit += (_, _) => viewModel.Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information));
        services.AddShortDramaServices();
        services.AddSingleton(_ =>
        {
            var database = new PlatformDatabase(PlatformPublisherPaths.MainDatabasePath);
            PlatformDatabaseInitializer.EnsureMainDatabase(database);
            return database;
        });
        services.AddSingleton<IJsonSettingStore, JsonSettingStore>();
        services.AddSingleton<ISecureBlobStore, SecureBlobStore>();
        services.AddSingleton<AccountJsonSettingStore>();
        services.AddSingleton<DatabaseBackupService>();
        services.AddSingleton<LegacyDatabaseImporter>();
        services.AddSingleton<PublishItemEventStore>();
        services.AddSingleton<ProjectStateDocumentStore>();
        services.AddSingleton<PublishJobStore>();
        services.AddSingleton<PublishAccountStore>();
        services.AddSingleton(provider => new ChannelsPublisher.Core.Services.AccountStore(
            provider.GetRequiredService<PlatformDatabase>(), ChannelsPublisher.Core.Services.AppPaths.AccountsFile));
        services.AddSingleton(_ => new AnalyticsRepository(PlatformPublisherPaths.MainDatabasePath));
        services.AddSingleton<AnalyticsQueryService>();
        services.AddSingleton<LocalPublishActivitySyncService>();
        services.AddSingleton<IAnalyticsActivitySink, AnalyticsActivitySink>();
        services.AddSingleton<AnalyticsCollectionCoordinator>();
        services.AddSingleton<YunfanAnalyticsImporter>();
        services.AddSingleton<WeixinAnalyticsCollector>();
        services.AddSingleton<KuaishouAnalyticsCollector>();
        services.AddSingleton<WeixinWorkflowSettingsStore>();
        services.AddSingleton<WeixinDirectoryMaterialPublishService>();
        services.AddSingleton<WeixinSystemHighlightPublishService>();
        services.AddSingleton<WeixinLocalVideoPublishService>();
        services.AddSingleton<WeixinAdxMaterialPublishService>();
        services.AddSingleton<IAdxDataProtector, WindowsAdxDataProtector>();
        services.AddSingleton(provider => new AdxSettingsStore(
            provider.GetRequiredService<IJsonSettingStore>(),
            Path.Combine(PlatformPublisherPaths.DataRoot, "adx", "settings.json")));
        services.AddSingleton(provider => new AdxCredentialStore(
            Path.Combine(PlatformPublisherPaths.DataRoot, "adx", "password.dat"),
            provider.GetRequiredService<IAdxDataProtector>(),
            provider.GetRequiredService<ISecureBlobStore>()));
        services.AddSingleton(provider => new AdxSessionStore(
            Path.Combine(PlatformPublisherPaths.DataRoot, "adx", "auth-state.dat"),
            provider.GetRequiredService<IAdxDataProtector>(),
            provider.GetRequiredService<ISecureBlobStore>()));
        services.AddSingleton<AdxBatchStore>();
        services.AddSingleton<AdxAutomationService>();
        services.AddSingleton<IMaterialSourceResolver, ProjectMaterialResolver>();
        services.AddSingleton<IMaterialSourceResolver, LocalDirectoryMaterialResolver>();
        services.AddSingleton<IMaterialSourceResolver, DirectoryGroupMaterialResolver>();
        services.AddSingleton<IMaterialSourceResolver, CustomFileMaterialResolver>();
        services.AddSingleton<IMaterialSourceResolver, AdxMaterialResolver>();
        services.AddSingleton<IMaterialSourceResolver, SystemHighlightMaterialResolver>();
        services.AddSingleton<IMaterialSourceResolver, DownloadedWorkMaterialResolver>();
        services.AddSingleton<MaterialResolverRegistry>();
        services.AddSingleton<MaterialDraftFactory>();
        services.AddSingleton<UnifiedPublishRepository>();
        services.AddSingleton<LegacyPublishDraftMigrator>();
        services.AddSingleton<DramaTitleImportService>();
        services.AddSingleton<IPublishBatchStore>(provider=>provider.GetRequiredService<UnifiedPublishRepository>());
        services.AddSingleton<AccountOperationGate>();
        services.AddSingleton<IUnifiedMaterialExecutor, WeixinUnifiedMaterialExecutor>();
        services.AddSingleton<PublishBatchCoordinator>();
        services.AddSingleton<WeixinAutoShelfService>();
        services.AddSingleton<WeixinSmartRecutService>();
        services.AddSingleton<WeixinManagementSyncService>();
        services.AddSingleton<WeixinProofArtifactsService>();
        services.AddSingleton<WeixinSeriesConfigOverrideService>();
        services.AddSingleton<IAiRuntimeSettingsProvider, PlatformAiRuntimeSettingsProvider>();
        services.AddSingleton<IPlatformPublishAdapter, WeixinChannelPublishAdapter>();
        services.AddSingleton<KuaishouPersonalSessionService>();
        services.AddSingleton<KuaishouPersonalProjectDataService>();
        services.AddSingleton<KuaishouPersonalPreparationService>();
        services.AddSingleton<KuaishouPersonalFirstPageService>();
        services.AddSingleton<KuaishouPersonalEpisodeUploadService>();
        services.AddSingleton<KuaishouPersonalUploadStateStore>();
        services.AddSingleton<KuaishouPersonalUploadService>();
        services.AddSingleton<IPlatformPublishAdapter, KuaishouPersonalPublishAdapter>();
        services.AddSingleton<IPlatformPublishAdapter>(
            _ => new UnavailableKuaishouPublishAdapter(PublishPlatform.KuaishouEnterpriseRevenue));
        services.AddSingleton<PlatformPublishCoordinator>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<AnalyticsViewModel>();
        services.AddSingleton<UnifiedPublishViewModel>();
        services.AddSingleton(_ => new SystemSettingsViewModel(PlatformPublisherPaths.SettingsDatabasePath)
        {
            LoginSettingsHint = "短剧搜索、下载和数据链路参数为多平台助手独立配置；平台登录信息请到左侧账号档案中维护。",
        });
        return services.BuildServiceProvider();
    }
}
