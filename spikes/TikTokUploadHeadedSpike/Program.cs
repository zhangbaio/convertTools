using Avalonia;

namespace TikTokUploadHeadedSpike;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* ignore */ }

        var prep = SpikeBootstrap.Prepare(args);
        if (prep.ExitCode is int code)
            return code;

        SpikeHostContext.Current = prep.Context!;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
