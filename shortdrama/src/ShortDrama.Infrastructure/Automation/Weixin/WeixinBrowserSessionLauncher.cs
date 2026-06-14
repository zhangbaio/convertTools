using Microsoft.Playwright;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation.Weixin.Pages;

namespace ShortDrama.Infrastructure.Automation.Weixin;

public sealed class WeixinBrowserSessionLauncher : IWeixinBrowserSessionLauncher
{
    private readonly IWeixinAutomationConfigLoader _configLoader;
    private readonly IWeixinAuthStateService _authStateService;
    private readonly WeixinBrowserRuntimeService _browserRuntimeService;
    private readonly WeixinHomePage _homePage;

    public WeixinBrowserSessionLauncher(
        IWeixinAutomationConfigLoader configLoader,
        IWeixinAuthStateService authStateService,
        WeixinBrowserRuntimeService browserRuntimeService,
        WeixinHomePage homePage)
    {
        _configLoader = configLoader;
        _authStateService = authStateService;
        _browserRuntimeService = browserRuntimeService;
        _homePage = homePage;
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
        Directory.CreateDirectory(config.Browser.UserDataDirectory);

        using var playwright = await _browserRuntimeService.CreatePlaywrightAsync(cancellationToken);
        await using var context = await playwright.Chromium.LaunchPersistentContextAsync(
            config.Browser.UserDataDirectory,
            new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = false,
                SlowMo = config.Browser.SlowMoMs,
                UserAgent = config.Browser.UserAgent,
                ViewportSize = null,
                Args =
                [
                    "--disable-blink-features=AutomationControlled",
                    "--no-sandbox",
                    "--start-maximized"
                ]
            });

        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        await _homePage.OpenAsync(page, config.BaseUrl, cancellationToken);
        await page.BringToFrontAsync();

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
}
