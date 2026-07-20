using System.Runtime.InteropServices;
using System.Text;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Desktop;

internal static class StartupFailureReporter
{
    private static readonly object SyncRoot = new();
    public static string LogPath => Path.Combine(AppPaths.DataRoot, "reports", "startup.log");

    public static void Report(Exception exception, string phase, bool showMessage)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var entry = new StringBuilder()
                    .AppendLine(new string('=', 80))
                    .AppendLine($"time: {DateTimeOffset.Now:O}")
                    .AppendLine($"phase: {phase}")
                    .AppendLine($"version: {typeof(StartupFailureReporter).Assembly.GetName().Version}")
                    .AppendLine(exception.ToString())
                    .ToString();
                File.AppendAllText(LogPath, entry, Encoding.UTF8);
            }
        }
        catch
        {
            // Reporting must not mask the original startup failure.
        }

        if (showMessage && OperatingSystem.IsWindows())
        {
            var detail = exception.GetBaseException().Message;
            if (detail.Length > 500) detail = detail[..500] + "\u2026";
            MessageBoxW(
                IntPtr.Zero,
                $"TikTok \u77ed\u5267\u52a9\u624b\u542f\u52a8\u5931\u8d25\u3002\n\n{detail}\n\n\u8bca\u65ad\u65e5\u5fd7\uff1a\n{LogPath}",
                "TikTok \u77ed\u5267\u52a9\u624b - \u542f\u52a8\u5931\u8d25",
                0x00000010u);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
