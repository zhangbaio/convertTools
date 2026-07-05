using Avalonia.Threading;
using Microsoft.Playwright;
using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Controls;

namespace TikTokPublisher.Ui.Services;

/// <summary>连接内置 WebView2（CDP）并获取可自动化的 Playwright 页面。</summary>
public static class EmbeddedBrowserAutomationBridge
{
    /// <summary>用 Playwright 独立启动一个浏览器（不依赖内置 WebView2 / 外部浏览器），复用账号授权文件登录态。</summary>
    public static async Task<(IPlaywright Playwright, IBrowser Browser, IPage Page)> LaunchPageAsync(
        TikTokAccountProfile account,
        string? navigateUrl,
        string authPath,
        bool headless,
        Action<string>? log,
        CancellationToken ct)
    {
        var pw = await Playwright.CreateAsync().ConfigureAwait(false);
        IBrowser? chromium = null;
        try
        {
            chromium = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless,
                Args =
                [
                    "--disable-blink-features=AutomationControlled",
                    "--disable-background-timer-throttling",
                    "--disable-backgrounding-occluded-windows",
                    "--disable-renderer-backgrounding",
                    headless ? "--window-size=1440,900" : "--start-maximized",
                ],
            }).ConfigureAwait(false);

            var contextOptions = new BrowserNewContextOptions
            {
                Locale = "zh-CN",
                ViewportSize = ViewportSize.NoViewport,
            };
            var proxy = TikTokProxyHelper.BuildFromAccount(account);
            if (proxy is not null)
            {
                contextOptions.Proxy = new Proxy
                {
                    Server = proxy.Server,
                    Username = string.IsNullOrWhiteSpace(proxy.Username) ? null : proxy.Username,
                    Password = string.IsNullOrWhiteSpace(proxy.Password) ? null : proxy.Password,
                };
                log?.Invoke($"独立浏览器已启用账号代理：{proxy.Description}");
            }

            if (File.Exists(authPath))
            {
                contextOptions.StorageStatePath = authPath;
                log?.Invoke($"独立浏览器已复用登录态文件：{authPath}");
            }
            else
            {
                log?.Invoke("独立浏览器未找到登录态文件，可能需要在浏览器页先用内置浏览器登录一次。");
            }

            var context = await chromium.NewContextAsync(contextOptions).ConfigureAwait(false);
            var page = await context.NewPageAsync().ConfigureAwait(false);

            page.Dialog += (_, dialog) =>
            {
                _ = string.Equals(dialog.Type, "beforeunload", StringComparison.OrdinalIgnoreCase)
                    ? dialog.AcceptAsync()
                    : dialog.DismissAsync();
            };

            var url = string.IsNullOrWhiteSpace(navigateUrl) ? TikTokUrls.DefaultSeriesDraftUrl : navigateUrl;
            log?.Invoke($"独立浏览器导航：{url}");
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 90000,
            }).ConfigureAwait(false);
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }).ConfigureAwait(false); }
            catch { /* SPA */ }
            await page.WaitForTimeoutAsync(1200).ConfigureAwait(false);

            log?.Invoke($"已启动独立浏览器（{(headless ? "无头" : "有头")}）并打开自动化页面");
            return (pw, chromium, page);
        }
        catch
        {
            if (chromium is not null)
                await chromium.DisposeAsync().ConfigureAwait(false);
            pw.Dispose();
            throw;
        }
    }

    public static async Task<(IPlaywright Playwright, IBrowser Browser, IPage Page)> ConnectPageAsync(
        IEmbeddedBrowser browser,
        string? navigateUrl,
        Action<string>? log,
        CancellationToken ct)
    {
        var cdp = browser.CdpEndpoint
            ?? throw new InvalidOperationException("内置浏览器 CDP 未就绪，请打开「浏览器」页等待加载完成");

        if (!string.IsNullOrWhiteSpace(navigateUrl))
        {
            log?.Invoke($"内置浏览器导航：{navigateUrl}");
            if (browser is WebView2Host host)
                await host.NavigateOnUiThreadAsync(navigateUrl).ConfigureAwait(false);
            else
                await browser.NavigateAsync(navigateUrl).ConfigureAwait(false);
            await Task.Delay(800, ct).ConfigureAwait(false);
        }

        var pw = await Playwright.CreateAsync().ConfigureAwait(false);
        var chromium = await pw.Chromium.ConnectOverCDPAsync(cdp).ConfigureAwait(false);
        var context = chromium.Contexts.FirstOrDefault()
            ?? throw new InvalidOperationException("内置浏览器上下文不可用");
        var page = context.Pages.FirstOrDefault()
            ?? throw new InvalidOperationException("内置浏览器页面不可用");

        // 上一轮失败可能遗留未保存的表单；导航会触发 beforeunload（“是否离开网站”）。
        // Playwright 默认丢弃对话框（等于留在旧页面），必须显式接受才能重置页面。
        page.Dialog += (_, dialog) =>
        {
            _ = string.Equals(dialog.Type, "beforeunload", StringComparison.OrdinalIgnoreCase)
                ? dialog.AcceptAsync()
                : dialog.DismissAsync();
        };

        if (!string.IsNullOrWhiteSpace(navigateUrl)
            && !string.Equals(page.Url, navigateUrl, StringComparison.OrdinalIgnoreCase)
            && !page.Url.Contains("/series/", StringComparison.OrdinalIgnoreCase))
        {
            await page.GotoAsync(navigateUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60000,
            }).ConfigureAwait(false);
        }

        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }).ConfigureAwait(false); }
        catch { /* SPA */ }
        await page.WaitForTimeoutAsync(1500).ConfigureAwait(false);

        log?.Invoke("已通过内置浏览器 CDP 连接自动化页面");
        return (pw, chromium, page);
    }
}
