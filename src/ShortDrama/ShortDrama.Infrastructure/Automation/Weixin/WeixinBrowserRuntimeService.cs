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
}
