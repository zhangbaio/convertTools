namespace ShortDrama.Infrastructure.Automation.Weixin;

internal static class WeixinRuntimePaths
{
    private const string UserDataDirectoryName = ".weixin_channel_tool";

    public static string UserDataRootDirectory => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        UserDataDirectoryName));

    public static string DefaultAuthFilePath => Path.Combine(UserDataRootDirectory, "runtime", "wx_auth_state.json");

    public static string DefaultBrowserProfileDirectory => Path.Combine(UserDataRootDirectory, "runtime", "chromium-profile");

    public static string DefaultOutputDirectory => Path.Combine(UserDataRootDirectory, "output", "weixin-channel-assistant");

    public static string DefaultPlaywrightBrowserDirectory => Path.Combine(UserDataRootDirectory, "ms-playwright");

    public static string? ResolveExistingPlaywrightBrowserDirectory()
    {
        foreach (var candidate in EnumeratePlaywrightBrowserDirectories())
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static IEnumerable<string> GetChromiumExecutableCandidates(string chromiumDirectory)
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(chromiumDirectory, "chrome-mac", "Chromium.app", "Contents", "MacOS", "Chromium");
            yield return Path.Combine(chromiumDirectory, "chrome-mac-arm64", "Chromium.app", "Contents", "MacOS", "Chromium");
            yield return Path.Combine(chromiumDirectory, "chrome-mac", "Google Chrome for Testing.app", "Contents", "MacOS", "Google Chrome for Testing");
            yield return Path.Combine(chromiumDirectory, "chrome-mac-arm64", "Google Chrome for Testing.app", "Contents", "MacOS", "Google Chrome for Testing");
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(chromiumDirectory, "chrome-win", "chrome.exe");
            yield return Path.Combine(chromiumDirectory, "chrome-win", "Chromium.exe");
        }
        else
        {
            yield return Path.Combine(chromiumDirectory, "chrome-linux", "chrome");
        }
    }

    private static IEnumerable<string> EnumeratePlaywrightBrowserDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in GetBundledBrowserCandidates().Concat(GetSystemBrowserCandidates()))
        {
            var fullPath = Path.GetFullPath(candidate);
            if (seen.Add(fullPath))
            {
                yield return fullPath;
            }
        }
    }

    private static IEnumerable<string> GetBundledBrowserCandidates()
    {
        foreach (var root in EnumerateSearchRoots())
        {
            yield return Path.Combine(root, "ms-playwright");
        }
    }

    private static IEnumerable<string> GetSystemBrowserCandidates()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "ms-playwright");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, "AppData", "Local", "ms-playwright");
            yield return Path.Combine(home, "Library", "Caches", "ms-playwright");
            yield return Path.Combine(home, ".cache", "ms-playwright");
        }

        yield return DefaultPlaywrightBrowserDirectory;
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
