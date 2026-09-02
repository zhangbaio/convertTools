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
        await RunBrowserAsync(config, headless: false, async (page, context, ct) =>
        {
            await page.GotoAsync(config.EntryUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            await page.BringToFrontAsync();
            while (!ct.IsCancellationRequested && context.Pages.Count > 0)
                await Task.Delay(500, ct);
        }, cancellationToken);
    }

    public async Task ValidateLoginAsync(PublishJob job, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var config = KuaishouPersonalConfig.Load(job);
        await RunBrowserAsync(config, config.Headless, async (page, _, ct) =>
        {
            progress?.Report("快手分账个人版：正在打开经营者管理平台…");
            await page.GotoAsync(config.EntryUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            await page.WaitForTimeoutAsync(1500);
            if (!await IsLoggedInAsync(page))
                throw new InvalidOperationException("快手分账个人版登录态无效，请先点击“登录 / 打开浏览器”扫码登录。");
            progress?.Report("快手分账个人版：经营者管理平台登录态有效。 ");
        }, cancellationToken);
    }

    public async Task ExecuteAuthenticatedAsync(
        PublishJob job,
        Func<IPage, KuaishouPersonalConfig, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        var config = KuaishouPersonalConfig.Load(job);
        await RunBrowserAsync(config, config.Headless, async (page, _, ct) =>
        {
            await page.GotoAsync(config.EntryUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            await page.WaitForTimeoutAsync(1200);
            if (!await IsLoggedInAsync(page))
                throw new InvalidOperationException("快手分账个人版登录态无效，请先扫码登录。");
            await action(page, config, ct);
        }, cancellationToken);
    }

    private async Task RunBrowserAsync(
        KuaishouPersonalConfig config,
        bool headless,
        Func<IPage, IBrowserContext, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        var runtime = await _runtimeService.InspectAsync(cancellationToken);
        if (!runtime.IsReady) throw new InvalidOperationException(runtime.Message);
        _runtimeService.ConfigureEnvironment(runtime);
        using var playwright = await _runtimeService.CreatePlaywrightAsync(runtime, cancellationToken);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            ExecutablePath = runtime.BrowserExecutablePath,
            Headless = headless,
            Args = ["--disable-blink-features=AutomationControlled", "--no-sandbox", "--start-maximized"],
        });
        var options = new BrowserNewContextOptions { ViewportSize = headless ? new ViewportSize { Width = 1920, Height = 1030 } : ViewportSize.NoViewport };
        if (File.Exists(config.AuthStatePath)) options.StorageStatePath = config.AuthStatePath;
        await using var context = await CreateContextAsync(browser, options);
        var page = await context.NewPageAsync();
        try { await action(page, context, cancellationToken); }
        finally
        {
            try { await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = config.AuthStatePath }); }
            catch { /* 登录态保存失败由下次校验暴露。 */ }
        }
    }

    private static async Task<IBrowserContext> CreateContextAsync(IBrowser browser, BrowserNewContextOptions options)
    {
        try { return await browser.NewContextAsync(options); }
        catch when (!string.IsNullOrWhiteSpace(options.StorageStatePath))
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
