using System.Diagnostics;
using System.Text.Json;

namespace TikTokPublisher.Ui.Controls;

internal static class WebView2ProcessRecovery
{
    private const string MarkerFileName = ".tiktok-webview-process.json";
    private sealed record ProcessMarker(int ProcessId, long StartTimeUtcTicks);

    public static void RecoverStaleProcess(string? userDataFolder, Action<string>? log = null)
    {
        var markerPath = MarkerPath(userDataFolder);
        if (markerPath is null || !File.Exists(markerPath))
            return;

        try
        {
            var marker = JsonSerializer.Deserialize<ProcessMarker>(File.ReadAllText(markerPath));
            if (marker is null || marker.ProcessId <= 0)
                return;

            using var process = Process.GetProcessById(marker.ProcessId);
            var sameProcess = process.ProcessName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)
                              && Math.Abs(process.StartTime.ToUniversalTime().Ticks - marker.StartTimeUtcTicks)
                              < TimeSpan.FromSeconds(2).Ticks;
            if (!sameProcess)
                return;

            log?.Invoke($"recovering stale WebView2 pid={marker.ProcessId} udf={userDataFolder}");
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // Process already exited.
        }
        catch (Exception ex)
        {
            log?.Invoke($"stale WebView2 recovery failed udf={userDataFolder} :: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TryDelete(markerPath);
        }
    }

    public static void SaveMarker(string? userDataFolder, uint processId)
    {
        var markerPath = MarkerPath(userDataFolder);
        if (markerPath is null || processId == 0 || processId > int.MaxValue)
            return;

        try
        {
            var ownedProcessId = checked((int)processId);
            using var process = Process.GetProcessById(ownedProcessId);
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            var marker = new ProcessMarker(ownedProcessId, process.StartTime.ToUniversalTime().Ticks);
            File.WriteAllText(markerPath, JsonSerializer.Serialize(marker));
        }
        catch
        {
            // Recovery metadata must never prevent browser startup.
        }
    }

    private static string? MarkerPath(string? userDataFolder)
    {
        if (string.IsNullOrWhiteSpace(userDataFolder))
            return null;
        try { return Path.Combine(Path.GetFullPath(userDataFolder), MarkerFileName); }
        catch { return null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }
}
