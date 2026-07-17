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
        if (Array.Exists(args, IsResetInstallerDataSecretsArg))
        {
            ClientSettingsStore.ResetInstallerDataSecrets();
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
