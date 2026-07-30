using Microsoft.Playwright;
using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services.TikTok;

/// <summary>
/// Updates only the copyright-proof tab of an existing TikTok series.
/// It never invokes the general create/edit form filler.
/// </summary>
public static class TikTokCopyrightProofEditService
{
    public static async Task<PublishResult> UpdateAsync(
        TikTokAccountProfile account,
        PublishItem item,
        IEmbeddedBrowser browser,
        FinalAction finalAction,
        Action<string>? log,
        CancellationToken ct)
    {
        void L(string message) => log?.Invoke(message);
        if (string.IsNullOrWhiteSpace(item.Title))
            return PublishResult.Fail("补全版权证明失败：新剧名不能为空。");

        IPlaywright? playwright = null;
        IBrowser? chromium = null;
        try
        {
            var useLaunch = string.Equals(
                (account.TiktokUploadBrowserMode ?? "").Trim(),
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
                        L,
                        ct)
                    .ConfigureAwait(false);
            }
            else
            {
                (playwright, chromium, page) = await EmbeddedBrowserAutomationBridge
                    .ConnectPageAsync(browser, TikTokUrls.DefaultSeriesListUrl, L, ct)
                    .ConfigureAwait(false);
            }

            if (IsLoginPage(page.Url))
                return PublishResult.Fail("TikTok 登录态失效，请先重新登录当前账号。");

            var detailUrl = await FindSeriesDetailUrlByExactNewTitleAsync(page, item.Title, L, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(detailUrl))
                return PublishResult.Fail($"平台原创管理未找到新剧名完全一致的项目：{item.Title}");

            L($"已按新剧名精确定位 TikTok 项目：{detailUrl}");
            await page.GotoAsync(detailUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 90000,
            }).ConfigureAwait(false);
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }); }
            catch { /* SPA */ }
            await TikTokBrowserActions.DismissFloatingAssistantAsync(page, L).ConfigureAwait(false);

            var copyrightTab = page.GetByText("版权证明", new() { Exact = true }).Last;
            await copyrightTab.WaitForAsync(new()
            {
                State = WaitForSelectorState.Visible,
                Timeout = 30000,
            }).ConfigureAwait(false);
            await copyrightTab.ClickAsync(new() { Timeout = 15000 }).ConfigureAwait(false);
            L("已进入版权证明页；本任务不会修改剧集信息、视频、商业模式或价格。");

            var existingMaterial = await TikTokBrowserActions
                .FindExistingCopyrightProofMaterialAsync(page, ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(existingMaterial))
            {
                L($"检测到 TikTok 版权证明页已有上传材料，跳过重复编辑：{existingMaterial}");
                return PublishResult.Success("TikTok 版权证明页已有材料，已跳过重复编辑");
            }

            var workflowDir = TikTokUploadStateStore.ResolveWorkflowProjectDir(item.ProjectDir);
            var options = TikTokPublishOptionsBuilder.FromAccount(account, workflowDir, L);
            await TikTokBrowserActions.ConfigureCopyrightProofAsync(page, options, L, ct)
                .ConfigureAwait(false);

            if (finalAction == FinalAction.None)
                return PublishResult.Success("版权证明页已填写完成（账号配置为只填不提交）");

            if (finalAction == FinalAction.Draft)
                await TikTokBrowserActions.SaveAsync(page, L, ct).ConfigureAwait(false);
            else
                await TikTokBrowserActions.SubmitAsync(page, L, ct, [item.Title]).ConfigureAwait(false);

            return PublishResult.Success("版权证明已补全并提交");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PublishResult.Fail($"补全版权证明失败：{ex.Message}");
        }
        finally
        {
            try { await (chromium?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false); }
            catch { /* disconnect only */ }
            playwright?.Dispose();
        }
    }

    private static async Task<string> FindSeriesDetailUrlByExactNewTitleAsync(
        IPage page,
        string newTitle,
        Action<string>? log,
        CancellationToken ct)
    {
        log?.Invoke($"TikTok 原创管理按新剧名精确搜索：{newTitle}");
        await TikTokSeriesListLookupService.OpenAsync(page, log, ct).ConfigureAwait(false);
        var rows = await TikTokSeriesListLookupService
            .SearchExactAsync(page, newTitle, ct)
            .ConfigureAwait(false);
        if (rows.Count > 1)
            throw new InvalidOperationException($"平台存在多个同名新剧名项目，已停止处理：{newTitle}");

        var exactMatches = rows
            .Select(row => row.DetailUrl)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (exactMatches.Length > 1)
            throw new InvalidOperationException($"平台存在多个同名新剧名项目，已停止处理：{newTitle}");
        return exactMatches.SingleOrDefault() ?? string.Empty;
    }

    private static bool IsLoginPage(string url) =>
        (url ?? string.Empty).Contains("/login", StringComparison.OrdinalIgnoreCase);
}
