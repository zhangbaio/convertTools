using Microsoft.Playwright;
using PlatformPublisher.Common.Models;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation.Weixin;
using ShortDrama.Infrastructure.Automation.Weixin.Pages;

namespace PlatformPublisher.Weixin.Publishing;

public sealed record WeixinAutoShelfResult(int ShelvedCount, int FailedCount, int ScannedRows, int VisitedPages, int Rounds);

public sealed class WeixinAutoShelfService
{
    private readonly IWeixinAutomationConfigLoader _configLoader;
    private readonly IWeixinAuthStateService _authStateService;
    private readonly WeixinBrowserRuntimeService _runtimeService;
    private readonly WeixinHomePage _homePage;

    public WeixinAutoShelfService(
        IWeixinAutomationConfigLoader configLoader,
        IWeixinAuthStateService authStateService,
        WeixinBrowserRuntimeService runtimeService,
        WeixinHomePage homePage)
    {
        _configLoader = configLoader;
        _authStateService = authStateService;
        _runtimeService = runtimeService;
        _homePage = homePage;
    }

    public async Task<WeixinAutoShelfResult> RunAsync(
        PublishJob job,
        int maxPages,
        int maxRounds,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var config = await _configLoader.LoadAsync(NullIfEmpty(job.ConfigPath), job.ProjectDirectory, cancellationToken);
        if (!string.IsNullOrWhiteSpace(job.AccountSessionDirectory))
        {
            var sessionDirectory = Path.GetFullPath(job.AccountSessionDirectory);
            Directory.CreateDirectory(sessionDirectory);
            config = config with
            {
                AuthFilePath = Path.Combine(sessionDirectory, "weixin-series-auth.json"),
                Browser = config.Browser with { UserDataDirectory = sessionDirectory },
            };
        }

        var runtime = await _runtimeService.InspectAsync(cancellationToken);
        if (!runtime.IsReady) throw new InvalidOperationException(runtime.Message);
        _runtimeService.ConfigureEnvironment(runtime);
        await _authStateService.ResolveAsync(config, cancellationToken);

        using var playwright = await _runtimeService.CreatePlaywrightAsync(runtime, cancellationToken);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            ExecutablePath = runtime.BrowserExecutablePath,
            Headless = config.Browser.Headless,
            SlowMo = config.Browser.SlowMoMs,
            Args = ["--disable-blink-features=AutomationControlled", "--no-sandbox", "--start-maximized"],
        });
        await using var context = await CreateContextAsync(browser, config);
        var page = await context.NewPageAsync();
        await _homePage.OpenAsync(page, config.BaseUrl, cancellationToken);
        if (!await _homePage.IsLoggedInAsync(page, cancellationToken))
            throw new InvalidOperationException("视频号登录态已失效，请先在左侧账号区重新登录。");

        var result = await RunListFlowAsync(page, config.BaseUrl, maxPages, maxRounds, progress, cancellationToken);
        try { await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = config.AuthFilePath }); }
        catch { /* 登录态保存失败不覆盖上架结果。 */ }
        return result;
    }

    private static async Task<WeixinAutoShelfResult> RunListFlowAsync(
        IPage page,
        string baseUrl,
        int maxPages,
        int maxRounds,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var listUrl = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "platform/playlet").ToString();
        var totalShelved = 0;
        var totalFailed = 0;
        var totalScanned = 0;
        var visitedPages = 0;
        var executedRounds = 0;
        for (var round = 1; round <= Math.Clamp(maxRounds, 1, 100); round++)
        {
            executedRounds = round;
            await page.GotoAsync(listUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
            await TryClickTextAsync(page, "剧集列表");
            await WaitForListReadyAsync(page, cancellationToken);
            var roundShelved = 0;
            for (var pageIndex = 1; pageIndex <= Math.Clamp(maxPages, 1, 100); pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                visitedPages = Math.Max(visitedPages, pageIndex);
                var pageResult = await ShelfCurrentPageAsync(page, progress, cancellationToken);
                totalShelved += pageResult.Shelved;
                totalFailed += pageResult.Failed;
                totalScanned += pageResult.Scanned;
                roundShelved += pageResult.Shelved;
                progress?.Report($"自动上架第 {round} 轮第 {pageIndex} 页：扫描 {pageResult.Scanned}，上架 {pageResult.Shelved}，失败 {pageResult.Failed}。");
                if (!await GoToNextPageAsync(page, cancellationToken)) break;
            }
            if (roundShelved == 0) break;
        }
        progress?.Report($"自动上架完成：共上架 {totalShelved} 部，失败 {totalFailed} 部，扫描 {totalScanned} 行。");
        return new WeixinAutoShelfResult(totalShelved, totalFailed, totalScanned, visitedPages, executedRounds);
    }

    private static async Task<(int Shelved, int Failed, int Scanned)> ShelfCurrentPageAsync(
        IPage page,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var shelved = 0;
        var failed = 0;
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var scanned = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = await ResolveRowsAsync(page);
            scanned = Math.Max(scanned, rows.Count);
            (ILocator Row, ILocator Action, string Title)? target = null;
            foreach (var row in rows)
            {
                var text = Normalize(await row.InnerTextAsync());
                if (string.IsNullOrWhiteSpace(text) || attempted.Contains(text) || text.Contains("已上架", StringComparison.Ordinal)) continue;
                var action = row.GetByText("上架", new LocatorGetByTextOptions { Exact = true }).First;
                if (await action.CountAsync() == 0 || !await action.IsVisibleAsync()) continue;
                target = (row, action, ExtractTitle(text));
                attempted.Add(text);
                break;
            }
            if (target is null) break;
            try
            {
                progress?.Report($"发现待上架剧集：{target.Value.Title}");
                await target.Value.Action.ClickAsync();
                await ConfirmDialogAsync(page);
                await WaitForShelfAppliedAsync(target.Value.Row, target.Value.Action, cancellationToken);
                shelved++;
                progress?.Report($"已上架剧集：{target.Value.Title}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                progress?.Report($"剧集上架失败，继续下一部：{target.Value.Title}；{ex.Message}");
            }
        }
        return (shelved, failed, scanned);
    }

    private static async Task<IReadOnlyList<ILocator>> ResolveRowsAsync(IPage page)
    {
        foreach (var selector in new[] { "table tbody tr", ".weui-desktop-table tbody tr", "[role='row']" })
        {
            var locator = page.Locator(selector);
            var count = Math.Min(await locator.CountAsync(), 200);
            var rows = new List<ILocator>();
            for (var index = 0; index < count; index++)
            {
                var row = locator.Nth(index);
                if (await row.IsVisibleAsync()) rows.Add(row);
            }
            if (rows.Count > 0) return rows;
        }
        return [];
    }

    private static async Task ConfirmDialogAsync(IPage page)
    {
        var dialog = page.Locator("[role='dialog']:visible, .weui-desktop-dialog:visible, .weui-desktop-dialog__wrp:visible").Last;
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        var button = dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "确定", Exact = false }).Last;
        await button.ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });
    }

    private static async Task WaitForListReadyAsync(IPage page, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((await ResolveRowsAsync(page)).Count > 0) return;
            if (await page.GetByText("暂无数据", new PageGetByTextOptions { Exact = false }).CountAsync() > 0) return;
            await page.WaitForTimeoutAsync(250);
        }
        throw new TimeoutException("进入剧集列表后未检测到列表内容。");
    }

    private static async Task WaitForShelfAppliedAsync(ILocator row, ILocator action, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var text = Normalize(await row.InnerTextAsync());
                if (text.Contains("已上架", StringComparison.Ordinal) || !await action.IsVisibleAsync()) return;
            }
            catch
            {
                return;
            }
            await Task.Delay(300, cancellationToken);
        }
        // 请求已提交但列表可能延迟刷新；由下一轮再次核对。
    }

    private static async Task<bool> GoToNextPageAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var locator in new[]
                 {
                     page.GetByText("下一页", new PageGetByTextOptions { Exact = true }).Last,
                     page.Locator(".weui-desktop-pagination__next, li.next, button[aria-label*='下一页']").Last,
                 })
        {
            if (await locator.CountAsync() == 0 || !await locator.IsVisibleAsync() || !await locator.IsEnabledAsync()) continue;
            var disabled = await locator.GetAttributeAsync("aria-disabled");
            if (string.Equals(disabled, "true", StringComparison.OrdinalIgnoreCase)) continue;
            await locator.ClickAsync();
            await page.WaitForTimeoutAsync(500);
            return true;
        }
        return false;
    }

    private static async Task TryClickTextAsync(IPage page, string text)
    {
        var locator = page.GetByText(text, new PageGetByTextOptions { Exact = false }).First;
        if (await locator.CountAsync() > 0 && await locator.IsVisibleAsync())
        {
            try { await locator.ClickAsync(new LocatorClickOptions { Timeout = 3_000 }); }
            catch { /* 可能已经位于列表 TAB。 */ }
        }
    }

    private static async Task<IBrowserContext> CreateContextAsync(IBrowser browser, WeixinAutomationConfig config)
    {
        var options = new BrowserNewContextOptions { UserAgent = config.Browser.UserAgent, ViewportSize = ViewportSize.NoViewport };
        if (File.Exists(config.AuthFilePath)) options.StorageStatePath = config.AuthFilePath;
        try { return await browser.NewContextAsync(options); }
        catch when (!string.IsNullOrWhiteSpace(options.StorageStatePath))
        {
            options.StorageStatePath = null;
            return await browser.NewContextAsync(options);
        }
    }

    private static string Normalize(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string ExtractTitle(string rowText) => rowText.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "未命名剧集";
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
