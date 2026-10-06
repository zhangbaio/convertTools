using Microsoft.Playwright;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services.TikTok;

/// <summary>从剧集表单的发布账号级联框读取已创建账号，不改草稿勾选。</summary>
public static class TikTokPublishAccountSyncService
{
    public static async Task<IReadOnlyList<TikTokPublishAccountOption>> FetchAsync(
        TikTokAccountProfile account,
        Action<string>? log,
        CancellationToken ct)
    {
        var authPath = ResolveAuthPath(account);
        if (!File.Exists(authPath))
            throw new InvalidOperationException("未找到 TikTok 登录态，请先登录 TikTok。");

        PlaywrightBrowserRuntime.ConfigureBundledBrowsers(log);
        log?.Invoke("正在连接 Playwright 获取已创建发布账号…");
        var playwright = await Playwright.CreateAsync();
        try
        {
            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args =
                [
                    "--disable-blink-features=AutomationControlled",
                    "--no-sandbox",
                    "--window-size=1440,1200",
                ],
            };
            PlaywrightBrowserRuntime.ApplyChromiumExecutable(launchOptions, preferHeadlessShell: true, log);
            var browser = await playwright.Chromium.LaunchAsync(launchOptions);
            try
            {
                var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    Locale = "zh-CN",
                    ViewportSize = new ViewportSize { Width = 1440, Height = 1200 },
                    StorageStatePath = authPath,
                });
                try
                {
                    var page = await context.NewPageAsync();
                    log?.Invoke("正在打开新建剧集表单以读取发布账号…");
                    await page.GotoAsync(TikTokUrls.DefaultSeriesDraftUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 60000,
                    });
                    try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }); }
                    catch { /* SPA */ }

                    if (page.Url.Contains("/login", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("TikTok 登录态已失效，请先重新登录后再获取发布账号。");

                    await TikTokBrowserActions.DismissFloatingAssistantAsync(page, log);
                    var accounts = await TikTokBrowserActions.CollectPublishAccountsAsync(page, log, ct)
                        .ConfigureAwait(false);
                    try { await page.Keyboard.PressAsync("Escape"); }
                    catch { /* 只关闭下拉框，不保存草稿。 */ }

                    if (accounts.Count == 0)
                        throw new InvalidOperationException("未读取到已创建的发布账号。");

                    log?.Invoke($"已获取 {accounts.Count} 个发布账号。");
                    return accounts;
                }
                finally
                {
                    await context.CloseAsync();
                }
            }
            finally
            {
                await browser.CloseAsync();
            }
        }
        finally
        {
            playwright.Dispose();
        }
    }

    private static string ResolveAuthPath(TikTokAccountProfile account)
    {
        var explicitPath = (account.TiktokStorageStatePath ?? "").Trim();
        if (!string.IsNullOrEmpty(explicitPath))
        {
            try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(explicitPath)); }
            catch { return explicitPath; }
        }

        return AppPaths.DefaultStorageStatePath(account.Id);
    }
}
