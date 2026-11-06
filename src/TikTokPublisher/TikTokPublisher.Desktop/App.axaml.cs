using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Desktop.Views;
using TikTokPublisher.Ui.Views;

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
        if (LicenseGate.GetStartupAction() == LicenseStartupAction.PromptLogin)
            return await PromptLicenseLoginAsync("首次使用或本机尚未登录，请联网登录并获取授权后继续使用。");

        var state = await LicenseGate.VerifyAsync(forceVerify: true);
        if (state is not null)
        {
            LicenseGate.SaveVerifiedState(state);
            return true;
        }

        return await PromptLicenseLoginAsync("软件授权联网校验失败，请重新登录后继续使用。");
    }

    private static async Task<bool> PromptLicenseLoginAsync(string message)
    {
        var login = LicenseGate.GetLoginDefaults();
        var result = await LicenseLoginDialog.ShowStandaloneAsync(
            login.ServerUrl,
            login.Account,
            login.Password,
            message);
        if (result is null)
            return false;

        LicenseGate.SaveLoginResult(result);
        return true;
    }
}
