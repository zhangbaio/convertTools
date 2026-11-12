using Microsoft.Playwright;
using PlatformPublisher.Common.Models;
using ShortDrama.Infrastructure.Automation.Weixin;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouPersonalSessionService
{
    private readonly WeixinBrowserRuntimeService _runtimeService;
    public KuaishouPersonalSessionService(WeixinBrowserRuntimeService runtimeService) => _runtimeService = runtimeService;

    public async Task OpenLoginAsync(PublishJob job, CancellationToken cancellationToken)
    {
        var config = KuaishouPersonalConfig.Load(job);
        var timeout = Math.Clamp(config.LoginTimeoutSeconds, 30, 900) * 1000;
        await RunBrowserAsync(config, headless: false, async (page, context, ct) =>
        {
            await page.GotoAsync(config.EntryUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = timeout });
            await page.BringToFrontAsync();
            var saved = false;
            while (!ct.IsCancellationRequested && context.Pages.Count > 0)
            {
                if (!saved && await IsLoggedInAsync(page))
                {
                    await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = config.AuthStatePath });
                    saved = true;
                }
                await Task.Delay(500, ct);
            }
        }, cancellationToken, allowInvalidStateFallback: true);
    }

    public async Task ValidateLoginAsync(PublishJob job, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var config = KuaishouPersonalConfig.Load(job);
        var label = job.Platform.DisplayName();
        var timeout = Math.Clamp(config.LoginTimeoutSeconds, 30, 900) * 1000;
        await RunBrowserAsync(config, config.RevenueShareHeadless, async (page, _, ct) =>
        {
            progress?.Report($"{label}：正在打开经营者管理平台…");
            await page.GotoAsync(config.EntryUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = timeout });
            await page.WaitForTimeoutAsync(1500);
            if (!await IsLoggedInAsync(page))
                throw new InvalidOperationException($"{label}登录态无效，请先点击“登录 / 打开浏览器”扫码登录。");
            progress?.Report($"{label}：经营者管理平台登录态有效。 ");
        }, cancellationToken);
    }

    public async Task ExecuteAuthenticatedAsync(
        PublishJob job,
        Func<IPage, KuaishouPersonalConfig, CancellationToken, Task> action,
        CancellationToken cancellationToken,
        bool holdUntilClosed = false,
        bool? headless = null)
    {
        var config = KuaishouPersonalConfig.Load(job);
        var label = job.Platform.DisplayName();
        var timeout = Math.Clamp(config.LoginTimeoutSeconds, 30, 900) * 1000;
        await RunBrowserAsync(config, headless ?? config.RevenueShareHeadless, async (page, context, ct) =>
        {
            await page.GotoAsync(config.EntryUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = timeout });
            await page.WaitForTimeoutAsync(1200);
            if (!await IsLoggedInAsync(page))
                throw new InvalidOperationException($"{label}登录态无效，请先扫码登录。");
            await action(page, config, ct);
            if (holdUntilClosed)
            {
                while (!ct.IsCancellationRequested && context.Pages.Count > 0)
                    await Task.Delay(500, ct);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 上传剧集走共享浏览器。同一账号、同一版本的下一部只新开标签页。
    /// 登录、统计、上架和补传仍各自打开独立浏览器。
    /// </summary>
    public async Task ExecuteUploadAsync(
        PublishJob job,
        Func<IPage, KuaishouPersonalConfig, CancellationToken, Task> action,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null,
        bool holdUntilClosed = false)
    {
        var config = KuaishouPersonalConfig.Load(job);
        var headless = config.RevenueShareHeadless;
        var label = job.Platform.DisplayName();
        Action<string>? log = progress is null ? null : message => progress.Report(message);
        var lease = await KuaishouUploadBrowserPool.Shared.RentAsync(
            config,
            headless,
            token => LaunchHeldBrowserAsync(config, headless, token),
            log,
            cancellationToken).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Stopped);
        try
        {
            var page = lease.Page;
            using var closePageOnCancel = linked.Token.UnsafeRegister(static state =>
            {
                if (state is IPage openPage)
                    RequestPageClose(openPage);
            }, page);
            if (!await IsLoggedInAsync(page).ConfigureAwait(false))
            {
                var message = $"{label}登录态无效，请先扫码登录。";
                lease.StopAccount(message);
                throw new InvalidOperationException(message);
            }

            try
            {
                await action(page, config, linked.Token).ConfigureAwait(false);
                if (holdUntilClosed && lease.IsOnlyUser)
                {
                    while (!cancellationToken.IsCancellationRequested && !page.IsClosed)
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);
                }
            }
            catch (Exception ex) when (KuaishouDailyUploadLimit.Is(ex) || IsLoginFailure(ex))
            {
                lease.StopAccount(ex.Message);
                throw;
            }
            catch (OperationCanceledException) when (lease.StopReason.Length > 0 && !cancellationToken.IsCancellationRequested)
            {
                if (KuaishouDailyUploadLimit.Matches(lease.StopReason))
                    throw new KuaishouDailyUploadLimitException();
                throw new InvalidOperationException(lease.StopReason);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       && config.KeepBrowserOpenOnFailure
                                       && !headless
                                       && lease.IsOnlyUser)
            {
                while (!cancellationToken.IsCancellationRequested && !page.IsClosed)
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                throw;
            }
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<(IPlaywright Playwright, IBrowser Browser, IBrowserContext Context)> LaunchHeldBrowserAsync(
        KuaishouPersonalConfig config,
        bool headless,
        CancellationToken cancellationToken)
    {
        var runtime = await _runtimeService.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!runtime.IsReady)
            throw new InvalidOperationException(runtime.Message);
        _runtimeService.ConfigureEnvironment(runtime);
        var playwright = await _runtimeService.CreatePlaywrightAsync(runtime, cancellationToken).ConfigureAwait(false);
        IBrowser? browser = null;
        try
        {
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                ExecutablePath = runtime.BrowserExecutablePath,
                Headless = headless,
                Args = headless
                    ? ["--disable-blink-features=AutomationControlled", "--no-sandbox", "--start-maximized", "--disable-site-isolation-trials", "--disable-gpu"]
                    : ["--disable-blink-features=AutomationControlled", "--no-sandbox", "--start-maximized", "--disable-site-isolation-trials"],
            }).ConfigureAwait(false);
            var options = new BrowserNewContextOptions
            {
                ViewportSize = headless ? new ViewportSize { Width = 1920, Height = 1030 } : ViewportSize.NoViewport,
            };
            if (File.Exists(config.AuthStatePath))
                options.StorageStatePath = config.AuthStatePath;
            var context = await CreateContextAsync(browser, options, allowInvalidStateFallback: false).ConfigureAwait(false);
            return (playwright, browser, context);
        }
        catch
        {
            if (browser is not null)
            {
                try { await browser.CloseAsync().ConfigureAwait(false); }
                catch { /* 启动失败时浏览器可能还没起来。 */ }
            }

            playwright.Dispose();
            throw;
        }
    }

    private static void RequestPageClose(IPage page) => _ = ClosePageQuietlyAsync(page);

    private static async Task ClosePageQuietlyAsync(IPage page)
    {
        try
        {
            if (!page.IsClosed)
                await page.CloseAsync().ConfigureAwait(false);
        }
        catch
        {
            // 标签页已经关掉。
        }
    }

    private static bool IsLoginFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("登录态无效", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private async Task RunBrowserAsync(
        KuaishouPersonalConfig config,
        bool headless,
        Func<IPage, IBrowserContext, CancellationToken, Task> action,
        CancellationToken cancellationToken,
        bool allowInvalidStateFallback = false)
    {
        var runtime = await _runtimeService.InspectAsync(cancellationToken);
        if (!runtime.IsReady) throw new InvalidOperationException(runtime.Message);
        _runtimeService.ConfigureEnvironment(runtime);
        using var playwright = await _runtimeService.CreatePlaywrightAsync(runtime, cancellationToken);
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            ExecutablePath = runtime.BrowserExecutablePath,
            Headless = headless,
            Args = headless
                ? ["--disable-blink-features=AutomationControlled", "--no-sandbox", "--start-maximized", "--disable-site-isolation-trials", "--disable-gpu"]
                : ["--disable-blink-features=AutomationControlled", "--no-sandbox", "--start-maximized", "--disable-site-isolation-trials"],
        });
        IBrowserContext? context = null;
        try
        {
            var options = new BrowserNewContextOptions { ViewportSize = headless ? new ViewportSize { Width = 1920, Height = 1030 } : ViewportSize.NoViewport };
            if (File.Exists(config.AuthStatePath)) options.StorageStatePath = config.AuthStatePath;
            context = await CreateContextAsync(browser, options, allowInvalidStateFallback);
            var page = await context.NewPageAsync();
            var succeeded = false;
            // 停止只取消令牌。关掉浏览器，进行中的页面动作才会返回，上传槽才能放开。
            using var closeOnCancel = cancellationToken.UnsafeRegister(static state =>
            {
                if (state is IBrowser openBrowser)
                    RequestBrowserClose(openBrowser);
            }, browser);
            try
            {
                await action(page, context, cancellationToken);
                succeeded = true;
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                if (ex is OperationCanceledException)
                    throw;
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && config.KeepBrowserOpenOnFailure && !headless)
            {
                while (!cancellationToken.IsCancellationRequested && context.Pages.Count > 0)
                    await Task.Delay(500, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                throw;
            }
            finally
            {
                try
                {
                    if (succeeded && context.Pages.Count > 0)
                        await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = config.AuthStatePath });
                }
                catch { /* 登录态保存失败由下次校验暴露。 */ }
            }
        }
        finally
        {
            if (context is not null)
            {
                try { await context.CloseAsync(); }
                catch { /* 停止时浏览器可能已经关掉。 */ }
            }

            try { await browser.CloseAsync(); }
            catch { /* 停止时浏览器可能已经关掉。 */ }
        }
    }

    private static void RequestBrowserClose(IBrowser browser) => _ = CloseBrowserQuietlyAsync(browser);

    private static async Task CloseBrowserQuietlyAsync(IBrowser browser)
    {
        try
        {
            await browser.CloseAsync().ConfigureAwait(false);
        }
        catch
        {
            // 浏览器已经退出。
        }
    }

    private static async Task<IBrowserContext> CreateContextAsync(IBrowser browser, BrowserNewContextOptions options,
        bool allowInvalidStateFallback)
    {
        try { return await browser.NewContextAsync(options); }
        catch when (allowInvalidStateFallback && !string.IsNullOrWhiteSpace(options.StorageStatePath))
        {
            options.StorageStatePath = null;
            return await browser.NewContextAsync(options);
        }
    }

    private static async Task<bool> IsLoggedInAsync(IPage page)
    {
        if (page.Url.Contains("login", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var text in new[] { "内容管理", "原生短剧", "内容上传", "经营者管理平台" })
        {
            var locator = page.GetByText(text, new PageGetByTextOptions { Exact = false }).First;
            if (await locator.CountAsync() > 0 && await locator.IsVisibleAsync()) return true;
        }
        return !page.Url.Contains("passport", StringComparison.OrdinalIgnoreCase) && page.Url.Contains("kdj.kuaishou.com", StringComparison.OrdinalIgnoreCase);
    }
}
