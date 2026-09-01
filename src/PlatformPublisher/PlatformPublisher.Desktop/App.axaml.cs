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
using ShortDrama.Core.Interfaces;
using TikTokPublisher.Ui.ViewModels;

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
            mainWindow.BindWeixinWorkflow(viewModel);
            mainWindow.BindWeixinDownload(
                _services.GetRequiredService<IDramaSearchService>(),
                _services.GetRequiredService<IDramaProjectBootstrapper>(),
                _services.GetRequiredService<IWorkService>(),
                viewModel);
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
        services.AddSingleton<WeixinWorkflowSettingsStore>();
        services.AddSingleton<WeixinDirectoryMaterialPublishService>();
        services.AddSingleton<WeixinSystemHighlightPublishService>();
        services.AddSingleton<WeixinLocalVideoPublishService>();
        services.AddSingleton<WeixinAutoShelfService>();
        services.AddSingleton<WeixinSmartRecutService>();
        services.AddSingleton<WeixinManagementSyncService>();
        services.AddSingleton<WeixinProofArtifactsService>();
        services.AddSingleton<WeixinSeriesConfigOverrideService>();
        services.AddSingleton<IAiRuntimeSettingsProvider, PlatformAiRuntimeSettingsProvider>();
        services.AddSingleton<IPlatformPublishAdapter, WeixinChannelPublishAdapter>();
        services.AddSingleton<IPlatformPublishAdapter>(
            _ => new UnavailableKuaishouPublishAdapter(PublishPlatform.KuaishouPersonalRevenue));
        services.AddSingleton<IPlatformPublishAdapter>(
            _ => new UnavailableKuaishouPublishAdapter(PublishPlatform.KuaishouEnterpriseRevenue));
        services.AddSingleton<PlatformPublishCoordinator>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton(_ => new SystemSettingsViewModel(PlatformPublisherPaths.SettingsDatabasePath)
        {
            LoginSettingsHint = "短剧搜索、下载和数据链路参数为多平台助手独立配置；平台登录信息请到左侧账号档案中维护。",
        });
        return services.BuildServiceProvider();
    }
}
