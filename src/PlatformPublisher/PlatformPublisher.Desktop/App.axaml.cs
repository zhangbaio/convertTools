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
            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            var settingsViewModel = _services.GetRequiredService<SystemSettingsViewModel>();
            var publishCoordinator = _services.GetRequiredService<PlatformPublishCoordinator>();
            var mainWindow = new MainWindow { DataContext = viewModel };
            mainWindow.BindSettings(settingsViewModel);
            mainWindow.BindWeixinSeries(publishCoordinator.GetAdapter(PublishPlatform.WeixinChannel));
            mainWindow.BindWeixinWorkflow(viewModel, _services.GetRequiredService<AdxAutomationService>(), _services.GetRequiredService<AdxBatchStore>());
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
        services.AddSingleton<PublishJobStore>();
        services.AddSingleton<PublishAccountStore>();
        services.AddSingleton(_ => new AnalyticsRepository(PlatformPublisherPaths.AnalyticsDatabasePath));
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
        services.AddSingleton(_ => new AdxSettingsStore(Path.Combine(PlatformPublisherPaths.DataRoot, "adx", "settings.json")));
        services.AddSingleton(provider => new AdxCredentialStore(
            Path.Combine(PlatformPublisherPaths.DataRoot, "adx", "password.dat"),
            provider.GetRequiredService<IAdxDataProtector>()));
        services.AddSingleton(provider => new AdxSessionStore(
            Path.Combine(PlatformPublisherPaths.DataRoot, "adx", "auth-state.dat"),
            provider.GetRequiredService<IAdxDataProtector>()));
        services.AddSingleton<AdxBatchStore>();
        services.AddSingleton<AdxAutomationService>();
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
        services.AddSingleton(_ => new SystemSettingsViewModel(PlatformPublisherPaths.SettingsDatabasePath)
        {
            LoginSettingsHint = "短剧搜索、下载和数据链路参数为多平台助手独立配置；平台登录信息请到左侧账号档案中维护。",
        });
        return services.BuildServiceProvider();
    }
}
