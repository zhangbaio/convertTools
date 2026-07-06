namespace TikTokPublisher.Ui.Services.TikTok;

internal static class PlaywrightBrowserRuntime
{
    public const string DesktopChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.7103.25 Safari/537.36";

    public static void ConfigureBundledBrowsers(Action<string>? log = null)
    {
        var configured = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (HasPlaywrightChromium(configured))
        {
            return;
        }

        foreach (var root in EnumerateSearchRoots())
        {
            var candidate = Path.Combine(root, "ms-playwright");
            if (!HasPlaywrightChromium(candidate))
            {
                continue;
            }

            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", candidate);
            log?.Invoke($"Playwright browser path: {candidate}");
            return;
        }
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
