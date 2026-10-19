using Avalonia;
using TikTokPublisher.Core.Licensing;

namespace TikTokPublisher.RegistrationPdf;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        LicenseAuthService.UseProduct(
            RegistrationPdfPaths.AppName,
            RegistrationPdfPaths.DeviceName,
            RegistrationPdfPaths.AppVersion);
        LicenseStore.UseStateDirectory(RegistrationPdfPaths.DataRoot);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
