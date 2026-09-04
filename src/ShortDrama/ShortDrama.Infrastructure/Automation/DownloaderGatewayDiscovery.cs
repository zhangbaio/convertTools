using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace ShortDrama.Infrastructure.Automation;

public static class DownloaderGatewayDiscovery
{
    public static bool TryStartInstalledDownloader()
    {
        if (!OperatingSystem.IsWindows()) return false;
        foreach (var candidate in ExecutableCandidates())
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                System.Diagnostics.Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "--background",
                    WorkingDirectory = Path.GetDirectoryName(candidate) ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                return true;
            }
            catch
            {
                // Try the next install location.
            }
        }
        return false;
    }

    public static string TryReadLocalApiKey()
    {
        if (!OperatingSystem.IsWindows()) return string.Empty;
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var path = Path.Combine(localAppData, "ShortDramaDownloader", "credentials.dat");
            if (!File.Exists(path)) return string.Empty;
            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(path),
                Encoding.ASCII.GetBytes("hongguo_high_bitrate_desktop"),
                DataProtectionScope.CurrentUser);
            using var document = JsonDocument.Parse(plain);
            return document.RootElement.TryGetProperty("localApiKey", out var key)
                ? key.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IEnumerable<string> ExecutableCandidates()
    {
        var configured = Environment.GetEnvironmentVariable("SHORTDRAMA_DOWNLOADER_EXE");
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured.Trim();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Programs", "ShortDramaDownloader", "shortdrama-downloader.exe");
        yield return Path.Combine(local, "ShortDramaDownloader", "shortdrama-downloader.exe");
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programs, "ShortDramaDownloader", "shortdrama-downloader.exe");
    }
}
