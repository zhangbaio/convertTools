using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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
            RegisterUnhandledUiExceptionHandler(desktop);

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
        services.AddSingleton<ManualMaterialProjectService>();
        services.AddSingleton<MaterialDirectoryPublishService>();
        services.AddSingleton<MaterialSystemHighlightBatchPublishService>();
        services.AddSingleton<MaterialSystemHighlightScheduleService>();
        services.AddSingleton<MaterialHighlightGenerationService>();
        services.AddSingleton<WeixinMaterialChannelVideoDeleteService>();
        services.AddSingleton<IWeixinLoginNotificationService, DesktopWeixinLoginNotificationService>();
        services.AddSingleton<XingeRemoteControlService>();
        services.AddSingleton<IDramaSettingsProvider, GlobalDramaSettingsProvider>();
        services.AddSingleton<HongguoNewApiService>();
        services.AddSingleton<HongguoLocalApiService>();
        services.AddSingleton<HongguoMemoryReaderService>();
        services.AddSingleton<HongguoDramaSearchService>();
        services.AddSingleton<HongguoDramaDownloader>();
        services.AddSingleton<DramaSourceRouter>();
        services.AddSingleton<IDramaSearchService>(provider => provider.GetRequiredService<DramaSourceRouter>());
        services.AddSingleton<IDramaDownloader>(provider => provider.GetRequiredService<DramaSourceRouter>());
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider();
    }

    private static void RegisterUnhandledUiExceptionHandler(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            var exception = e.Exception;
            TryWriteUnhandledUiExceptionLog(exception);

            try
            {
                if (desktop.MainWindow?.DataContext is MainWindowViewModel viewModel)
                {
                    var message = $"界面操作异常已拦截：{exception.Message}";
                    viewModel.StatusMessage = message;
                    viewModel.AppendExternalLog(
                        message,
                        stepKey: "ui",
                        stepLabel: "界面操作",
                        isFailure: true);
                }
            }
            catch
            {
                // Avoid a secondary UI error from rethrowing while handling the original exception.
            }

            e.Handled = true;
        };
    }

    private static void TryWriteUnhandledUiExceptionLog(Exception exception)
    {
        try
        {
            var logRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShortDramaDesktop",
                "logs");
            Directory.CreateDirectory(logRoot);
            var logPath = Path.Combine(logRoot, "ui-unhandled.log");
            File.AppendAllText(
                logPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never turn a recoverable UI exception into a crash.
        }
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
