using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Ui.Views;

namespace TikTokPublisher.RegistrationPdf;

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
                    if (!await EnsureLicenseAsync())
                    {
                        desktop.Shutdown();
                        return;
                    }

                    var window = ArticlesOfIncorporationDialog.CreateStandaloneWindow();
                    desktop.MainWindow = window;
                    desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    window.Show();
                }
                catch (Exception)
                {
                    desktop.Shutdown();
                }
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task<bool> EnsureLicenseAsync()
    {
        var login = RegistrationPdfLoginStore.Load();
        var state = LicenseStore.Load();
        if (state.IsActivated()
            && !string.IsNullOrWhiteSpace(state.BoundLicenseKey)
            && LicenseAuthService.IsAllowedOnThisMachine(state)
            && !state.IsExpired())
        {
            try
            {
                await Task.Run(() => LicenseAuthService.VerifyRegistrationLicense(
                    state,
                    login.ServerUrl,
                    login.Account,
                    login.Password));
                return true;
            }
            catch (Exception ex)
            {
                return await PromptLoginAsync(ex.Message);
            }
        }

        return await PromptLoginAsync("请输入授权账号和授权码。两项都要通过后才能使用，此登录与短剧助手互不影响。");
    }

    private static async Task<bool> PromptLoginAsync(string message)
    {
        var login = RegistrationPdfLoginStore.Load();
        var result = await LicenseLoginDialog.ShowStandaloneAsync(
            login.ServerUrl,
            login.Account,
            login.Password,
            message,
            requireLicenseCode: true,
            licenseCode: login.LicenseCode);
        if (result is null)
            return false;

        RegistrationPdfLoginStore.Save(result.ServerUrl, result.Account, result.Password, result.LicenseCode);
        return true;
    }
}
