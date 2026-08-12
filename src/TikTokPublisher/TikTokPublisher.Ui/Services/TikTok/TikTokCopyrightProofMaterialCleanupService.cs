using Microsoft.Playwright;
using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services.TikTok;

public sealed record TikTokCopyrightProofMaterialCleanupResult(
    string Title,
    bool Success,
    string Message);

public static class TikTokCopyrightProofMaterialCleanupService
{
    public static async Task<IReadOnlyList<TikTokCopyrightProofMaterialCleanupResult>> ExecuteAsync(
        TikTokAccountProfile account,
        IEmbeddedBrowser? browser,
        IReadOnlyList<string> titles,
        Action<string>? log,
        CancellationToken ct)
    {
        IPlaywright? playwright = null;
        IBrowser? chromium = null;
        try
        {
            var useLaunch = string.Equals(
                (account.TiktokUploadBrowserMode ?? string.Empty).Trim(),
                "playwright",
                StringComparison.OrdinalIgnoreCase);
            IPage page;
            if (useLaunch)
            {
                var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(account);
                (playwright, chromium, page) = await EmbeddedBrowserAutomationBridge
                    .LaunchPageAsync(
                        account,
                        TikTokUrls.DefaultSeriesListUrl,
                        authPath,
                        account.TiktokPlaywrightUploadHeadless,
                        log,
                        ct)
                    .ConfigureAwait(false);
            }
            else
            {
                if (browser is null)
                    throw new InvalidOperationException("当前账号的内置浏览器尚未就绪或未登录。");
                (playwright, chromium, page) = await EmbeddedBrowserAutomationBridge
                    .ConnectPageAsync(browser, TikTokUrls.DefaultSeriesListUrl, log, ct)
                    .ConfigureAwait(false);
            }

            var results = new List<TikTokCopyrightProofMaterialCleanupResult>(titles.Count);
            foreach (var title in titles)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    log?.Invoke($"开始清理版权辅助材料：{title}");
                    var detailUrl = await FindDetailUrlAsync(page, title, log, ct).ConfigureAwait(false);
                    await OpenCopyrightProofTabAsync(page, detailUrl, log, ct).ConfigureAwait(false);
                    await TikTokBrowserActions.RemoveAuxiliaryCopyrightProofMaterialsAsync(page, log, ct)
                        .ConfigureAwait(false);
                    await TikTokBrowserActions.SubmitAsync(
                            page,
                            log,
                            ct,
                            [title],
                            verifySeriesListStatus: false)
                        .ConfigureAwait(false);

                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    await OpenCopyrightProofTabAsync(page, detailUrl, log, ct).ConfigureAwait(false);
                    await TikTokBrowserActions.VerifyAuxiliaryCopyrightProofMaterialsRemovedAsync(page, ct)
                        .ConfigureAwait(false);
                    results.Add(new TikTokCopyrightProofMaterialCleanupResult(
                        title,
                        true,
                        "已删除两个辅助材料、取消勾选并通过提交后复查"));
                    log?.Invoke($"版权辅助材料清理成功：{title}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    results.Add(new TikTokCopyrightProofMaterialCleanupResult(title, false, ex.Message));
                    log?.Invoke($"版权辅助材料清理失败：{title}，{ex.Message}");
                }
            }

            return results;
        }
        finally
        {
            try
            {
                if (chromium is not null)
                    await chromium.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Launch mode closes its browser; CDP mode only disconnects automation.
            }
            playwright?.Dispose();
        }
    }

    private static async Task<string> FindDetailUrlAsync(
        IPage page,
        string title,
        Action<string>? log,
        CancellationToken ct)
    {
        await TikTokSeriesListLookupService.OpenAsync(page, log, ct).ConfigureAwait(false);
        var rows = await TikTokSeriesListLookupService
            .SearchExactAsync(page, title, ct, log)
            .ConfigureAwait(false);
        var detailUrls = rows
            .Select(row => row.DetailUrl)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (detailUrls.Length == 0)
            throw new InvalidOperationException($"平台原创管理未找到新剧名完全一致的项目：{title}");
        if (detailUrls.Length > 1)
            throw new InvalidOperationException($"平台存在多个同名项目，已停止处理：{title}");
        return detailUrls[0];
    }

    private static async Task OpenCopyrightProofTabAsync(
        IPage page,
        string detailUrl,
        Action<string>? log,
        CancellationToken ct)
    {
        await page.GotoAsync(detailUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 90000,
        }).WaitAsync(ct).ConfigureAwait(false);
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }); }
        catch { /* SPA background polling */ }
        await TikTokBrowserActions.DismissFloatingAssistantAsync(page, log).ConfigureAwait(false);
        if ((page.Url ?? string.Empty).Contains("/login", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TikTok 登录态失效，请先重新登录当前账号。");

        var tab = page.GetByText("版权证明", new() { Exact = true }).Last;
        await tab.WaitForAsync(new()
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30000,
        }).WaitAsync(ct).ConfigureAwait(false);
        await tab.ClickAsync(new() { Timeout = 15000 }).WaitAsync(ct).ConfigureAwait(false);
    }
}
