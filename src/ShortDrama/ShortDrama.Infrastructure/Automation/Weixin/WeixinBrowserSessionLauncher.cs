using Microsoft.Playwright;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation.Weixin.Pages;
using ShortDrama.Infrastructure.Notifications;

namespace ShortDrama.Infrastructure.Automation.Weixin;

public sealed class WeixinBrowserSessionLauncher : IWeixinBrowserSessionLauncher
{
    private readonly IWeixinAutomationConfigLoader _configLoader;
    private readonly IWeixinAuthStateService _authStateService;
    private readonly WeixinBrowserRuntimeService _browserRuntimeService;
    private readonly WeixinHomePage _homePage;
    private readonly IWeixinLoginNotificationService _loginNotificationService;

    public WeixinBrowserSessionLauncher(
        IWeixinAutomationConfigLoader configLoader,
        IWeixinAuthStateService authStateService,
        WeixinBrowserRuntimeService browserRuntimeService,
        WeixinHomePage homePage,
        IWeixinLoginNotificationService? loginNotificationService = null)
    {
        _configLoader = configLoader;
        _authStateService = authStateService;
        _browserRuntimeService = browserRuntimeService;
        _homePage = homePage;
        _loginNotificationService = loginNotificationService ?? NoopWeixinLoginNotificationService.Instance;
    }

    public async Task OpenHomeAsync(
        string? configPath,
        string projectDir,
        CancellationToken cancellationToken)
    {
        var config = await LoadLaunchConfigAsync(configPath, projectDir, cancellationToken);
        var runtimeStatus = await _browserRuntimeService.InspectAsync(cancellationToken);
        if (!runtimeStatus.IsReady)
        {
            throw new InvalidOperationException(runtimeStatus.Message);
        }

        _browserRuntimeService.ConfigureEnvironment(runtimeStatus);
        await _authStateService.ResolveAsync(config, cancellationToken);
        Directory.CreateDirectory(config.OutputDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(config.AuthFilePath) ?? config.ConfigDirectory);

        using var playwright = await _browserRuntimeService.CreatePlaywrightAsync(cancellationToken);
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions
            {
                ExecutablePath = runtimeStatus.BrowserExecutablePath,
                Headless = false,
                SlowMo = config.Browser.SlowMoMs,
                Args =
                [
                    "--disable-blink-features=AutomationControlled",
                    "--no-sandbox",
                    "--start-maximized",
                    "--window-position=0,0",
                    "--window-size=1920,1080"
                ]
            });
        await using var context = await CreateBrowserContextAsync(browser, config);

        var page = await context.NewPageAsync();
        await MaximizeBrowserWindowAsync(page);
        await _homePage.OpenAsync(page, config.BaseUrl, cancellationToken);
        await page.BringToFrontAsync();
        await MaximizeBrowserWindowAsync(page);

        if (!await _homePage.IsLoggedInAsync(page, cancellationToken))
        {
            var screenshotPath = await _homePage.SaveLoginQrScreenshotAsync(
                page,
                config.OutputDirectory,
                "weixin-login-qr.png",
                cancellationToken);

            await TryNotifyLoginQrRequiredAsync(
                new WeixinLoginNotificationRequest(
                    ProjectKey: Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    DisplayName: Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    ProjectDirectory: projectDir,
                    BaseUrl: config.BaseUrl,
                    AuthFilePath: config.AuthFilePath,
                    ScreenshotPath: screenshotPath,
                    Message: "浏览器已打开，请使用微信扫码登录视频号后台。"),
                cancellationToken);
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!context.Pages.Any())
                {
                    break;
                }

                await Task.Delay(500, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Let finally persist auth state before closing.
        }
        finally
        {
            try
            {
                await context.StorageStateAsync(new BrowserContextStorageStateOptions
                {
                    Path = config.AuthFilePath
                });
            }
            catch
            {
                // Ignore auth snapshot save failures for manual browser helper.
            }
        }
    }

    internal async Task<WeixinAutomationConfig> LoadLaunchConfigAsync(
        string? configPath,
        string projectDir,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
        {
            throw new ArgumentException("projectDir is required.", nameof(projectDir));
        }

        var normalizedProjectDir = Path.GetFullPath(projectDir);
        return await _configLoader.LoadAsync(configPath, normalizedProjectDir, cancellationToken);
    }

    private static async Task MaximizeBrowserWindowAsync(IPage page)
    {
        try
        {
            var session = await page.Context.NewCDPSessionAsync(page).ConfigureAwait(false);
            var windowInfo = await session.SendAsync("Browser.getWindowForTarget").ConfigureAwait(false);
            if (windowInfo is null ||
                !windowInfo.Value.TryGetProperty("windowId", out var windowIdElement))
            {
                return;
            }

            await session.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = windowIdElement.GetInt32(),
                ["bounds"] = new Dictionary<string, object>
                {
                    ["windowState"] = "maximized"
                }
            }).ConfigureAwait(false);
        }
        catch
        {
            // Chrome can restore persisted window bounds; CDP maximization is best effort.
        }
    }

    private static async Task<IBrowserContext> CreateBrowserContextAsync(
        IBrowser browser,
        WeixinAutomationConfig config)
    {
        var contextOptions = new BrowserNewContextOptions
        {
            ViewportSize = ViewportSize.NoViewport,
            UserAgent = config.Browser.UserAgent
        };
        if (!string.IsNullOrWhiteSpace(config.AuthFilePath) && File.Exists(config.AuthFilePath))
        {
            contextOptions.StorageStatePath = config.AuthFilePath;
        }

        try
        {
            return await browser.NewContextAsync(contextOptions);
        }
        catch when (!string.IsNullOrWhiteSpace(contextOptions.StorageStatePath))
        {
            contextOptions.StorageStatePath = null;
            return await browser.NewContextAsync(contextOptions);
        }
    }

    private async Task TryNotifyLoginQrRequiredAsync(
        WeixinLoginNotificationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _loginNotificationService.NotifyLoginRequiredAsync(request, cancellationToken);
        }
        catch
        {
            // Manual browser helper should stay resilient even when notification delivery fails.
        }
    }
}
