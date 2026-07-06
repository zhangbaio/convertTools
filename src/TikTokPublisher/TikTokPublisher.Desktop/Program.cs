using Avalonia;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Desktop;

internal static class Program
{
    private const string ResetHgnewCredentialsArg = "--reset-hgnew-credentials";

    [STAThread]
    public static int Main(string[] args)
    {
        if (Array.Exists(args, arg => string.Equals(arg, ResetHgnewCredentialsArg, StringComparison.OrdinalIgnoreCase)))
        {
            ClientSettingsStore.ResetHgnewCredentials();
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
