using Microsoft.Playwright;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Automation.Weixin.Pages;

public sealed class WeixinHomePage
{
    private static readonly string[] LoggedInMarkerTexts =
    [
        "内容管理",
        "互动管理",
        "直播",
        "收入与服务",
        "带货中心",
        "数据中心",
        "通知中心",
        "设置"
    ];

    private static readonly string[] LoginPromptTexts =
    [
        "扫码登录",
        "微信扫码登录",
        "请使用微信扫码登录",
        "使用微信扫一扫登录",
        "登录后继续"
    ];

    public async Task OpenAsync(IPage page, string baseUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await page.GotoAsync(baseUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60_000
        });
    }

    public async Task<bool> IsLoggedInAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
            {
                Timeout = 10_000
            });

            var currentUrl = page.Url ?? string.Empty;
            if (currentUrl.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                currentUrl.Contains("passport", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var loggedInMarkerCount = 0;
            foreach (var markerText in LoggedInMarkerTexts)
            {
                if (await page.GetByText(markerText, new PageGetByTextOptions
                    {
                        Exact = false
                    }).First.IsVisibleAsync())
                {
                    loggedInMarkerCount++;
                }
            }

            if (loggedInMarkerCount >= 2 || (currentUrl.Contains("/platform", StringComparison.OrdinalIgnoreCase) && loggedInMarkerCount >= 1))
            {
                return true;
            }

            var hasQrCode = await page
                .Locator(".qrcode-img, .login-qrcode, [class*='qrCode'], [class*='qrcode'], img[alt*='二维码']")
                .First
                .IsVisibleAsync();
            if (hasQrCode)
            {
                return false;
            }

            foreach (var promptText in LoginPromptTexts)
            {
                if (await page.GetByText(promptText, new PageGetByTextOptions
                    {
                        Exact = false
                    }).First.IsVisibleAsync())
                {
                    return false;
                }
            }

            return loggedInMarkerCount > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> SaveScreenshotAsync(
        IPage page,
        string outputDirectory,
        string fileName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(outputDirectory);
        var screenshotPath = Path.Combine(outputDirectory, fileName);
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = screenshotPath,
            FullPage = true
        });
        return screenshotPath;
    }

    public async Task<string> SaveLoginQrScreenshotAsync(
        IPage page,
        string outputDirectory,
        string fileName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(outputDirectory);
        var screenshotPath = Path.Combine(outputDirectory, fileName);
        var qrLocator = page
            .Locator(".qrcode-img, .login-qrcode, [class*='qrCode'], [class*='qrcode'], img[alt*='二维码']")
            .First;

        try
        {
            if (await qrLocator.IsVisibleAsync())
            {
                await qrLocator.ScreenshotAsync(new LocatorScreenshotOptions
                {
                    Path = screenshotPath
                });
                return screenshotPath;
            }
        }
        catch
        {
            // Fall back to a normal page screenshot when QR locator is unavailable.
        }

        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = screenshotPath,
            FullPage = false
        });
        return screenshotPath;
    }

    public async Task SaveDebugArtifactsAsync(
        IPage page,
        WeixinAutomationConfig config,
        string stem,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!config.Debug.SaveHtml && !config.Debug.SaveText)
        {
            return;
        }

        Directory.CreateDirectory(config.OutputDirectory);
        var root = await GetDebugRootAsync(page);
        if (config.Debug.SaveHtml)
        {
            var html = await root.EvaluateAsync<string>("node => node.outerHTML ?? ''");
            await File.WriteAllTextAsync(
                Path.Combine(config.OutputDirectory, $"{stem}.html"),
                html,
                cancellationToken);
        }

        if (config.Debug.SaveText)
        {
            var text = await root.EvaluateAsync<string>("node => node.innerText ?? ''");
            await File.WriteAllTextAsync(
                Path.Combine(config.OutputDirectory, $"{stem}.txt"),
                text,
                cancellationToken);
        }
    }

    private static async Task<ILocator> GetDebugRootAsync(IPage page)
    {
        var candidates = new[]
        {
            page.Locator("wujie-app").First,
            page.Locator(".micro-drama-post").First,
            page.Locator(".main-body-wrap").First,
            page.Locator("body").First
        };

        foreach (var candidate in candidates)
        {
            try
            {
                await candidate.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = 1_000
                });
                return candidate;
            }
            catch
            {
                // Skip unavailable roots.
            }
        }

        return page.Locator("body").First;
    }
}
