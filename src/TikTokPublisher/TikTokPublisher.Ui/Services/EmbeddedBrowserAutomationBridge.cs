using Avalonia.Threading;
using Microsoft.Playwright;
using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Controls;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Ui.Services;

/// <summary>连接内置 WebView2（CDP）并获取可自动化的 Playwright 页面。</summary>
public static class EmbeddedBrowserAutomationBridge
{
    /// <summary>用 Playwright 自动启动外部浏览器（不依赖内置 WebView2 / 外部 CDP），复用账号授权文件登录态。</summary>
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
                log?.Invoke($"外部浏览器已启用账号代理：{proxy.Description}");
            }

            if (File.Exists(authPath))
            {
                contextOptions.StorageStatePath = authPath;
                log?.Invoke($"外部浏览器已复用登录态文件：{authPath}");
            }
            else
            {
                log?.Invoke("外部浏览器未找到登录态文件，可能需要在浏览器页先用内置浏览器登录一次。");
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
            log?.Invoke($"外部浏览器导航：{url}");
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 90000,
            }).ConfigureAwait(false);
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }).ConfigureAwait(false); }
            catch { /* SPA */ }
            await page.WaitForTimeoutAsync(1200).ConfigureAwait(false);
            await EnsureTikTokPageHealthyAsync(page, url, log, ct).ConfigureAwait(false);

            log?.Invoke($"已启动外部浏览器（{(headless ? "无头" : "有头")}）并打开自动化页面");
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

        var pw = await Playwright.CreateAsync().ConfigureAwait(false);
        IBrowser chromium;
        try
        {
            chromium = await pw.Chromium.ConnectOverCDPAsync(cdp).ConfigureAwait(false);
        }
        catch (PlaywrightException ex) when (IsPlaywrightConnectionFailure(ex.Message))
        {
            pw.Dispose();
            throw new InvalidOperationException(
                $"连接浏览器自动化端口失败（{cdp}）。浏览器可能崩溃、端口失效或页面已断开，请打开「浏览器」页确认已登录并刷新页面后重试。",
                ex);
        }
        catch
        {
            pw.Dispose();
            throw;
        }
        var context = chromium.Contexts.FirstOrDefault()
            ?? throw new InvalidOperationException("内置浏览器上下文不可用");
        var page = context.Pages.FirstOrDefault()
            ?? throw new InvalidOperationException("内置浏览器页面不可用");

        AttachAutomationDialogHandler(page, log);

        await PrepareCleanTikTokPageAsync(page, navigateUrl, log, ct).ConfigureAwait(false);

        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }).ConfigureAwait(false); }
        catch { /* SPA */ }
        await page.WaitForTimeoutAsync(1500).ConfigureAwait(false);
        await EnsureTikTokPageHealthyAsync(page, navigateUrl, log, ct).ConfigureAwait(false);

        log?.Invoke("已通过内置浏览器 CDP 连接自动化页面");
        return (pw, chromium, page);
    }

    private static void AttachAutomationDialogHandler(IPage page, Action<string>? log)
    {
        // 上一轮失败可能遗留未保存的表单；后续导航会触发 beforeunload（“是否离开网站”）。
        // Playwright 默认丢弃对话框（等于留在旧页面），必须在导航前显式接受。
        page.Dialog += (_, dialog) =>
        {
            _ = HandleAutomationDialogAsync(dialog, log);
        };
    }

    private static async Task HandleAutomationDialogAsync(IDialog dialog, Action<string>? log)
    {
        try
        {
            if (IsLeaveSiteDialog(dialog))
            {
                await dialog.AcceptAsync().ConfigureAwait(false);
                log?.Invoke("检测到浏览器遗留的离开确认弹窗，已自动选择离开。");
                return;
            }

            await dialog.DismissAsync().ConfigureAwait(false);
        }
        catch
        {
            // 多个 CDP 连接反复挂载监听时，旧监听可能抢先处理；忽略已关闭的对话框。
        }
    }

    private static bool IsLeaveSiteDialog(IDialog dialog)
    {
        if (string.Equals(dialog.Type, "beforeunload", StringComparison.OrdinalIgnoreCase))
            return true;

        var message = dialog.Message ?? "";
        return message.Contains("更改可能未保存", StringComparison.Ordinal) ||
               message.Contains("是否离开网站", StringComparison.Ordinal) ||
               message.Contains("Changes you made may not be saved", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Leave site", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task PrepareCleanTikTokPageAsync(
        IPage page,
        string? navigateUrl,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await TikTokBrowserActions.ResetLeftoverPageStateAsync(page, log, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(navigateUrl))
            return;

        log?.Invoke($"内置浏览器导航：{navigateUrl}");
        await GotoWithLeftoverDialogRecoveryAsync(page, navigateUrl, log, ct).ConfigureAwait(false);
        await TikTokBrowserActions.ResetLeftoverPageStateAsync(page, log, ct).ConfigureAwait(false);

        if (!IsSamePagePath(page.Url, navigateUrl))
        {
            await GotoWithLeftoverDialogRecoveryAsync(page, navigateUrl, log, ct).ConfigureAwait(false);
            await TikTokBrowserActions.ResetLeftoverPageStateAsync(page, log, ct).ConfigureAwait(false);
        }
    }

    private static async Task GotoWithLeftoverDialogRecoveryAsync(
        IPage page,
        string url,
        Action<string>? log,
        CancellationToken ct)
    {
        try
        {
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60000,
            }).ConfigureAwait(false);
        }
        catch (PlaywrightException ex) when (!ct.IsCancellationRequested && IsNavigationBlockedByDialog(ex.Message))
        {
            log?.Invoke("导航被上一轮遗留表单拦截，正在确认离开后重试。");
            await TikTokBrowserActions.ResetLeftoverPageStateAsync(page, log, ct).ConfigureAwait(false);
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60000,
            }).ConfigureAwait(false);
        }
    }

    private static bool IsNavigationBlockedByDialog(string? message)
    {
        var value = message ?? "";
        return value.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Navigation", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("beforeunload", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("dialog", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSamePagePath(string? currentUrl, string targetUrl)
    {
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var current) ||
            !Uri.TryCreate(targetUrl, UriKind.Absolute, out var target))
            return string.Equals(currentUrl, targetUrl, StringComparison.OrdinalIgnoreCase);

        return string.Equals(current.Host, target.Host, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   current.AbsolutePath.TrimEnd('/'),
                   target.AbsolutePath.TrimEnd('/'),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static async Task EnsureTikTokPageHealthyAsync(
        IPage page,
        string? recoveryUrl,
        Action<string>? log,
        CancellationToken ct)
    {
        if (!await LooksLikeTikTokCrashPageAsync(page).ConfigureAwait(false))
            return;

        ct.ThrowIfCancellationRequested();
        log?.Invoke("检测到 TikTok 页面异常（出了点问题/React 崩溃），正在刷新页面重试。");
        if (!string.IsNullOrWhiteSpace(recoveryUrl))
        {
            await page.GotoAsync(recoveryUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 90000,
            }).ConfigureAwait(false);
        }
        else
        {
            await page.ReloadAsync(new PageReloadOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 90000,
            }).ConfigureAwait(false);
        }

        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }).ConfigureAwait(false); }
        catch { /* SPA */ }
        await page.WaitForTimeoutAsync(1500).ConfigureAwait(false);

        if (await LooksLikeTikTokCrashPageAsync(page).ConfigureAwait(false))
            throw new InvalidOperationException("TikTok 页面刷新后仍显示异常，请在「浏览器」页点击页面里的“重试”或重新登录后再执行队列。");
    }

    private static async Task<bool> LooksLikeTikTokCrashPageAsync(IPage page)
    {
        try
        {
            var text = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions
            {
                Timeout = 3000,
            }).ConfigureAwait(false);
            return ContainsTikTokCrashMarker(text);
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsTikTokCrashMarker(string? text)
    {
        var value = text ?? "";
        return value.Contains("出了点问题", StringComparison.Ordinal) ||
               value.Contains("Minified React error", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("reactjs.org/docs/error-decoder", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("error-decoder.html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlaywrightConnectionFailure(string? message)
    {
        var value = message ?? "";
        return value.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Target page, context or browser has been closed", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Browser closed", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("WebSocket", StringComparison.OrdinalIgnoreCase);
    }
}
