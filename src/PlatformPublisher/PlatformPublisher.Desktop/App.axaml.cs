using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlatformPublisher.Core.Models;
using PlatformPublisher.Core.Publishing;
using PlatformPublisher.Core.Services;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Desktop.Views;
using ShortDrama.Infrastructure.DependencyInjection;

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
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
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
        services.AddSingleton<WeixinDirectoryMaterialPublishService>();
        services.AddSingleton<WeixinSystemHighlightPublishService>();
        services.AddSingleton<IPlatformPublishAdapter, WeixinChannelPublishAdapter>();
        services.AddSingleton<IPlatformPublishAdapter>(
            _ => new UnavailableKuaishouPublishAdapter(PublishPlatform.KuaishouPersonalRevenue));
        services.AddSingleton<IPlatformPublishAdapter>(
            _ => new UnavailableKuaishouPublishAdapter(PublishPlatform.KuaishouEnterpriseRevenue));
        services.AddSingleton<PlatformPublishCoordinator>();
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider();
    }
}
