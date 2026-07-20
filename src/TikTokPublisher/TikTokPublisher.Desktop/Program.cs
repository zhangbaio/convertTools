using Avalonia;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Desktop;

internal static class Program
{
    private const string ResetHgnewCredentialsArg = "--reset-hgnew-credentials";
    private const string ResetInstallerDataSecretsArg = "--reset-installer-data-secrets";

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (Array.Exists(args, IsResetInstallerDataSecretsArg))
            {
                ClientSettingsStore.ResetInstallerDataSecrets();
                return 0;
            }

            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            {
                if (eventArgs.ExceptionObject is Exception exception)
                    StartupFailureReporter.Report(exception, "AppDomain.UnhandledException", showMessage: false);
            };
            TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            {
                StartupFailureReporter.Report(eventArgs.Exception, "TaskScheduler.UnobservedTaskException", showMessage: false);
                eventArgs.SetObserved();
            };

            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            StartupFailureReporter.Report(ex, "Program.Main", showMessage: true);
            return 1;
        }
    }

    private static bool IsResetInstallerDataSecretsArg(string arg) =>
        string.Equals(arg, ResetInstallerDataSecretsArg, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, ResetHgnewCredentialsArg, StringComparison.OrdinalIgnoreCase);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
