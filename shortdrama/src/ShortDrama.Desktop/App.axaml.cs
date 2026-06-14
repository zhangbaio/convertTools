using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using ShortDrama.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShortDrama.Desktop.Services;
using ShortDrama.Desktop.ViewModels;
using ShortDrama.Desktop.Views;
using ShortDrama.Infrastructure.Automation;
using ShortDrama.Infrastructure.DependencyInjection;
using System.Linq;

namespace ShortDrama.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;

    public ServiceProvider Services => _services
        ?? throw new InvalidOperationException("Desktop services have not been initialized.");

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();

            _services = BuildServices();
            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            desktop.Exit += (_, _) => viewModel.PersistState();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddDebug();
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        services.AddShortDramaServices();
        services.AddSingleton<GlobalSettingsService>();
        services.AddSingleton<DesktopConfigService>();
        services.AddSingleton<DesktopStateService>();
        services.AddSingleton<DesktopDependencyInspector>();
        services.AddSingleton<DesktopShellService>();
        services.AddSingleton<HongguoDramaSearchService>();
        services.AddSingleton<HongguoDramaDownloader>();
        services.AddSingleton<DramaSourceRouter>();
        services.AddSingleton<IDramaSearchService>(provider => provider.GetRequiredService<DramaSourceRouter>());
        services.AddSingleton<IDramaDownloader>(provider => provider.GetRequiredService<DramaSourceRouter>());
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
