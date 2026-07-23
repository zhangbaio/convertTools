using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TikTokPublisher.Desktop.Views;

namespace TikTokPublisher.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await StartWithLicenseGateAsync(desktop);
                }
                catch (Exception ex)
                {
                    StartupFailureReporter.Report(ex, "App.StartWithLicenseGateAsync", showMessage: true);
                    desktop.Shutdown();
                }
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartWithLicenseGateAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (!await EnsureLicenseBeforeMainWindowAsync())
        {
            desktop.Shutdown();
            return;
        }

        var mainWindow = new MainWindow();
        desktop.MainWindow = mainWindow;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
        mainWindow.StartLicenseVerifyTimer();
    }

    private static async Task<bool> EnsureLicenseBeforeMainWindowAsync()
    {
        var state = await LicenseGate.VerifyAsync(forceVerify: true);
        if (state is not null)
        {
            LicenseGate.SaveVerifiedState(state);
            return true;
        }

        return false;
    }
}
