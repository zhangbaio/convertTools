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
                    var draftDetailUrl = await TikTokEditFlowService.DiscoverEditableDraftDetailUrlAsync(
                        page, titleCandidates: null, log, ct);
                    if (string.IsNullOrWhiteSpace(draftDetailUrl))
                        throw new InvalidOperationException("未在原创管理中找到可用于读取发布账号的草稿。");

                    log?.Invoke($"进入草稿详情：{draftDetailUrl}");
                    await page.GotoAsync(draftDetailUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 60000,
                    });
                    try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }); }
                    catch { /* SPA */ }

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
