using Microsoft.Playwright;

namespace TikTokPublisher.Ui.Services.TikTok;

internal static class PlaywrightBrowserRuntime
{
    public const string DesktopChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.7103.25 Safari/537.36";

    public static void ConfigureBundledBrowsers(Action<string>? log = null)
    {
        var browserRoot = ResolveBrowserRoot();
        if (browserRoot is null)
            return;

        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browserRoot);
        log?.Invoke($"Playwright browser path: {browserRoot}");
    }

    public static void ApplyChromiumExecutable(
        BrowserTypeLaunchOptions options,
        bool preferHeadlessShell,
        Action<string>? log = null)
    {
        var executable = ResolveChromiumExecutable(preferHeadlessShell);
        if (!string.IsNullOrWhiteSpace(executable))
        {
            options.ExecutablePath = executable;
            log?.Invoke($"Playwright executable: {executable}");
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            options.Channel = "msedge";
            log?.Invoke("Playwright executable not found; fallback to installed Microsoft Edge.");
        }
    }

    private static string? ResolveBrowserRoot()
    {
        foreach (var candidate in EnumerateBrowserRootCandidates())
        {
            if (HasPlaywrightChromium(candidate))
                return candidate;
        }

        return null;
    }

    private static string? ResolveChromiumExecutable(bool preferHeadlessShell)
    {
        foreach (var browserRoot in EnumerateBrowserRootCandidates())
        {
            var executable = FindChromiumExecutable(browserRoot, preferHeadlessShell) ??
                             FindChromiumExecutable(browserRoot, preferHeadlessShell: false);
            if (!string.IsNullOrWhiteSpace(executable))
                return executable;
        }

        return null;
    }

    private static bool HasPlaywrightChromium(string? browserRoot)
    {
        if (string.IsNullOrWhiteSpace(browserRoot) || !Directory.Exists(browserRoot))
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var hasChromium = Directory
                    .EnumerateDirectories(browserRoot, "chromium-*")
                    .Any(dir => File.Exists(Path.Combine(dir, "chrome-win", "chrome.exe")));
                var hasHeadlessShell = Directory
                    .EnumerateDirectories(browserRoot, "chromium_headless_shell-*")
                    .Any(dir => File.Exists(Path.Combine(dir, "chrome-win", "headless_shell.exe")));
                return hasChromium && hasHeadlessShell;
            }

            return Directory.EnumerateDirectories(browserRoot, "chromium-*").Any() &&
                   Directory.EnumerateDirectories(browserRoot, "chromium_headless_shell-*").Any();
        }
        catch
        {
            return false;
        }
    }

    private static string? FindChromiumExecutable(string? browserRoot, bool preferHeadlessShell)
    {
        if (string.IsNullOrWhiteSpace(browserRoot) || !Directory.Exists(browserRoot))
            return null;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var directoryPattern = preferHeadlessShell ? "chromium_headless_shell-*" : "chromium-*";
                var executablePath = preferHeadlessShell
                    ? Path.Combine("chrome-win", "headless_shell.exe")
                    : Path.Combine("chrome-win", "chrome.exe");

                return Directory
                    .EnumerateDirectories(browserRoot, directoryPattern)
                    .Select(dir => Path.Combine(dir, executablePath))
                    .FirstOrDefault(File.Exists);
            }

            return Directory
                .EnumerateDirectories(browserRoot, preferHeadlessShell ? "chromium_headless_shell-*" : "chromium-*")
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateBrowserRootCandidates()
    {
        foreach (var root in EnumerateSearchRoots())
            yield return Path.Combine(root, "ms-playwright");

        var configured = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return Path.Combine(localAppData, "ms-playwright");
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var current = Path.GetFullPath(start);
            while (!string.IsNullOrWhiteSpace(current) && seen.Add(current))
            {
                yield return current;
                var parent = Directory.GetParent(current);
                if (parent is null)
                {
                    break;
                }

                current = parent.FullName;
            }
        }
    }
}
