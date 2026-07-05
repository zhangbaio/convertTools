namespace ShortDrama.Desktop.Services;

internal static class DesktopCrashTrace
{
    public static void Write(string message)
    {
        try
        {
            var logRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShortDramaDesktop",
                "logs");
            Directory.CreateDirectory(logRoot);

            var logPath = Path.Combine(logRoot, "ui-trace.log");
            File.AppendAllText(
                logPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Crash tracing must never become another source of UI failure.
        }
    }
}
