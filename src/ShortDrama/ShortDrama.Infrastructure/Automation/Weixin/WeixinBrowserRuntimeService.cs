using Microsoft.Playwright;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Automation.Weixin;

public sealed class WeixinBrowserRuntimeService : IWeixinBrowserRuntimeService
{
    public Task<WeixinBrowserRuntimeStatus> InspectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var browserRoot = ResolveBrowserRoot();
        if (browserRoot is not null)
        {
            var playwrightExecutablePath = ResolveChromiumExecutable(browserRoot);
            if (playwrightExecutablePath is not null)
            {
                return Task.FromResult(new WeixinBrowserRuntimeStatus(
                    IsReady: true,
                    BrowserType: "chromium",
                    BrowserRootDirectory: browserRoot,
                    BrowserExecutablePath: playwrightExecutablePath,
                    Message: $"Detected Playwright Chromium runtime: {playwrightExecutablePath}",
                    NeedsInstall: false));
            }
        }

        var localBrowser = ResolveLocalBrowserExecutable();
        if (localBrowser is not null)
        {
            return Task.FromResult(new WeixinBrowserRuntimeStatus(
                IsReady: true,
                BrowserType: localBrowser.Type,
                BrowserRootDirectory: null,
                BrowserExecutablePath: localBrowser.Path,
                Message: $"已检测到本机浏览器({localBrowser.DisplayName})：{localBrowser.Path}",
                NeedsInstall: false));
        }

        if (browserRoot is null)
        {
            return Task.FromResult(new WeixinBrowserRuntimeStatus(
                IsReady: false,
                BrowserType: "chromium",
                BrowserRootDirectory: WeixinRuntimePaths.DefaultPlaywrightBrowserDirectory,
                BrowserExecutablePath: null,
                Message: $"未找到 Playwright Chromium 运行时。建议安装到 {WeixinRuntimePaths.DefaultPlaywrightBrowserDirectory}",
                NeedsInstall: true));
        }

        var executablePath = ResolveChromiumExecutable(browserRoot);
        return Task.FromResult(new WeixinBrowserRuntimeStatus(
            IsReady: executablePath is not null,
            BrowserType: "chromium",
            BrowserRootDirectory: browserRoot,
            BrowserExecutablePath: executablePath,
            Message: executablePath is null
                ? $"检测到 Playwright 浏览器目录，但未找到 Chromium 可执行文件: {browserRoot}"
                : $"已检测到 Chromium 运行时: {executablePath}",
            NeedsInstall: executablePath is null));
    }

    public void ConfigureEnvironment(WeixinBrowserRuntimeStatus status)
    {
        if (string.IsNullOrWhiteSpace(status.BrowserRootDirectory) &&
            !string.IsNullOrWhiteSpace(status.BrowserExecutablePath))
        {
            return;
        }

        var browserRoot = !string.IsNullOrWhiteSpace(status.BrowserRootDirectory)
            ? status.BrowserRootDirectory
            : WeixinRuntimePaths.ResolveExistingPlaywrightBrowserDirectory() ?? WeixinRuntimePaths.DefaultPlaywrightBrowserDirectory;

        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browserRoot);
    }

    public async Task<IPlaywright> CreatePlaywrightAsync(CancellationToken cancellationToken)
    {
        var status = await InspectAsync(cancellationToken);
        ConfigureEnvironment(status);
        return await Playwright.CreateAsync();
    }

    private static string? ResolveBrowserRoot()
    {
        var configured = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        return WeixinRuntimePaths.ResolveExistingPlaywrightBrowserDirectory();
    }

    private static string? ResolveChromiumExecutable(string browserRoot)
    {
        var chromiumDirs = Directory.EnumerateDirectories(browserRoot, "chromium-*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var chromiumDir in chromiumDirs)
        {
            foreach (var candidate in WeixinRuntimePaths.GetChromiumExecutableCandidates(chromiumDir))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static LocalBrowserExecutable? ResolveLocalBrowserExecutable()
    {
        foreach (var candidate in EnumerateLocalBrowserCandidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate.Path) && File.Exists(candidate.Path))
            {
                return candidate with { Path = Path.GetFullPath(candidate.Path) };
            }
        }

        return null;
    }

    private static IEnumerable<LocalBrowserExecutable> EnumerateLocalBrowserCandidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return new LocalBrowserExecutable(
            "msedge",
            "Edge",
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"));
        yield return new LocalBrowserExecutable(
            "msedge",
            "Edge",
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"));
        yield return new LocalBrowserExecutable(
            "msedge",
            "Edge",
            Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe"));
        yield return new LocalBrowserExecutable(
            "chrome",
            "Chrome",
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"));
        yield return new LocalBrowserExecutable(
            "chrome",
            "Chrome",
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"));
        yield return new LocalBrowserExecutable(
            "chrome",
            "Chrome",
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"));
    }

    private sealed record LocalBrowserExecutable(string Type, string DisplayName, string Path);
}
