using Microsoft.Playwright;
using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services.TikTok;

internal sealed record TikTokCopyrightProofEditBrowserPlan(
    bool UsePlaywright,
    bool Headless,
    string Description);

/// <summary>
/// Updates only the copyright-proof tab of an existing TikTok series.
/// It never invokes the general create/edit form filler.
/// </summary>
public static class TikTokCopyrightProofEditService
{
    internal static TikTokCopyrightProofEditBrowserPlan ResolveBrowserPlan(
        TikTokAccountProfile account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var usePlaywright = string.Equals(
            (account.TiktokUploadBrowserMode ?? string.Empty).Trim(),
            "playwright",
            StringComparison.OrdinalIgnoreCase);
        if (!usePlaywright)
        {
            return new TikTokCopyrightProofEditBrowserPlan(
                UsePlaywright: false,
                Headless: false,
                "内置浏览器（WebView2）");
        }

        var headless = account.TiktokPlaywrightUploadHeadless;
        return new TikTokCopyrightProofEditBrowserPlan(
            UsePlaywright: true,
            headless,
            $"程序自动打开的 Playwright 浏览器（{(headless ? "无头" : "有头")}）");
    }

    public static async Task<PublishResult> UpdateAsync(
        TikTokAccountProfile account,
        PublishItem item,
        IEmbeddedBrowser browser,
        FinalAction finalAction,
        Action<string>? log,
        CancellationToken ct,
        bool forceAiOutlineSupplement = false)
    {
        void L(string message) => log?.Invoke(message);
        if (string.IsNullOrWhiteSpace(item.Title))
            return PublishResult.Fail("补全版权证明失败：新剧名不能为空。");

        IPlaywright? playwright = null;
        IBrowser? chromium = null;
        try
        {
            var browserPlan = ResolveBrowserPlan(account);
            L($"版权证明编辑浏览器：{browserPlan.Description}（与账号发布配置一致）。");

            IPage page;
            if (browserPlan.UsePlaywright)
            {
                var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(account);
                if (!File.Exists(authPath))
                    return PublishResult.Fail("TikTok 登录态文件不存在，请先重新登录当前账号。");
                (playwright, chromium, page) = await EmbeddedBrowserAutomationBridge
                    .LaunchPageAsync(
                        account,
                        TikTokUrls.DefaultSeriesListUrl,
                        authPath,
                        browserPlan.Headless,
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

            var workflowDir = TikTokUploadStateStore.ResolveWorkflowProjectDir(item.ProjectDir);
            var options = TikTokPublishOptionsBuilder.FromAccount(
                account,
                workflowDir,
                L,
                item.EnabledQueueSteps);
            if (forceAiOutlineSupplement)
            {
                if (!options.UploadAiScriptOutlineWithScreenshots ||
                    string.IsNullOrWhiteSpace(options.AiScriptOutlineFilePath) ||
                    !File.Exists(options.AiScriptOutlineFilePath))
                {
                    return PublishResult.Fail(
                        "补全 AI 大纲失败：账号未启用大纲上传，或项目中不存在 AI剧本大纲.pdf。");
                }
                L("本次为 AI 剧本大纲补传，将强制重新处理“AI 生成过程截图”材料栏。");
            }
            var classificationChanged = await TikTokBrowserActions
                .EnsureCopyrightProofClassificationAsync(page, options, ct)
                .ConfigureAwait(false);
            if (classificationChanged)
            {
                L("TikTok 版权证明分类已按账号配置补全：是否原始权利人、内容原创类型。");
            }
            var coverage = await TikTokBrowserActions
                .ProbeConfiguredCopyrightProofMaterialsAsync(
                    page,
                    options.CopyrightMaterialTypes,
                    ct)
                .ConfigureAwait(false);
            if (coverage.FormAvailable)
            {
                foreach (var detail in coverage.Details)
                    L($"TikTok 版权材料逐项检查：{detail}。");

                if (coverage.Plan.IsComplete &&
                    !forceAiOutlineSupplement &&
                    !classificationChanged)
                {
                    L("账号配置的版权材料均已上传，跳过重复编辑。");
                    return PublishResult.Success("TikTok 账号配置的版权材料均已上传，已跳过重复编辑");
                }

                if (coverage.Plan.IsComplete && classificationChanged)
                {
                    L("账号配置的版权材料均已上传，但版权分类已补选；将继续保存或提交使配置生效。");
                }

                var missingLabels = coverage.Plan.MissingMaterialTypes
                    .Select(key => TikTokPublishConstants.CopyrightMaterialLabels[key]);
                if (!coverage.Plan.IsComplete)
                    L($"TikTok 版权材料将继续补全：{string.Join("、", missingLabels)}。");
            }
            else
            {
                L("未能在预检阶段识别版权材料字段，将按账号配置执行完整填写。");
            }

            var existingMaterialTypes = coverage.Plan.ExistingMaterialTypes;
            if (forceAiOutlineSupplement)
            {
                existingMaterialTypes = existingMaterialTypes
                    .Where(key => !string.Equals(
                        key,
                        TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
                        StringComparison.Ordinal))
                    .ToArray();
            }

            await TikTokBrowserActions.ConfigureCopyrightProofAsync(
                    page,
                    options,
                    existingMaterialTypes,
                    L,
                    ct,
                    uploadAiScriptOutlineOnly: forceAiOutlineSupplement)
                .ConfigureAwait(false);

            if (finalAction == FinalAction.None)
                return PublishResult.Success("版权证明页已填写完成（账号配置为只填不提交）");

            if (finalAction == FinalAction.Draft)
                await TikTokBrowserActions.SaveAsync(page, L, ct).ConfigureAwait(false);
            else
                await TikTokBrowserActions.SubmitAsync(
                        page,
                        L,
                        ct,
                        [item.Title],
                        verifySeriesListStatus: false)
                    .ConfigureAwait(false);

            await VerifyPersistedCopyrightProofMaterialsAsync(
                    page,
                    detailUrl,
                    options.CopyrightMaterialTypes,
                    L,
                    ct)
                .ConfigureAwait(false);

            return PublishResult.Success(
                finalAction == FinalAction.Draft
                    ? "版权证明已保存并通过落库复查"
                    : "版权证明已提交并通过落库复查");
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
            .SearchExactAsync(page, newTitle, ct, log)
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

    private static async Task VerifyPersistedCopyrightProofMaterialsAsync(
        IPage page,
        string detailUrl,
        IEnumerable<string>? configuredMaterialTypes,
        Action<string>? log,
        CancellationToken ct)
    {
        var configured = TikTokPublishConstants
            .NormalizeCopyrightMaterialTypes(configuredMaterialTypes)
            .ToArray();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        var attempt = 0;
        var lastFailure = "版权证明表单尚未完成复查";

        // Give TikTok a short moment to persist the edit before the first reload. The following
        // retries handle eventual consistency without ever treating the already-published series
        // status as proof that the copyright files were saved.
        await Task.Delay(2000, ct).ConfigureAwait(false);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                log?.Invoke($"提交后第 {attempt} 次复查版权证明材料：重新打开当前剧集。");
                await page.GotoAsync(detailUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 90000,
                }).ConfigureAwait(false);
                try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }); }
                catch { /* SPA or background polling */ }
                await TikTokBrowserActions.DismissFloatingAssistantAsync(page, log).ConfigureAwait(false);

                if (IsLoginPage(page.Url))
                    throw new InvalidOperationException("TikTok 登录态失效，无法完成版权材料落库复查。");

                var copyrightTab = page.GetByText("版权证明", new() { Exact = true }).Last;
                await copyrightTab.WaitForAsync(new()
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 30000,
                }).ConfigureAwait(false);
                await copyrightTab.ClickAsync(new() { Timeout = 15000 }).ConfigureAwait(false);

                var coverage = await TikTokBrowserActions
                    .ProbeConfiguredCopyrightProofMaterialsAsync(page, configured, ct)
                    .ConfigureAwait(false);
                foreach (var detail in coverage.Details)
                    log?.Invoke($"提交后版权材料复查：{detail}。");

                if (coverage.FormAvailable && coverage.Plan.IsComplete)
                {
                    log?.Invoke("TikTok 版权证明提交后复查通过：账号配置的材料均已实际保存。");
                    return;
                }

                if (!coverage.FormAvailable)
                {
                    lastFailure = "版权证明表单未加载或无法识别";
                }
                else
                {
                    var missingLabels = coverage.Plan.MissingMaterialTypes
                        .Select(key => TikTokPublishConstants.CopyrightMaterialLabels[key])
                        .ToArray();
                    lastFailure = missingLabels.Length == 0
                        ? "版权材料状态尚未稳定"
                        : $"缺少：{string.Join("、", missingLabels)}";
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = ex.Message;
            }

            if (DateTime.UtcNow >= deadline)
                break;

            log?.Invoke($"版权证明提交后复查尚未通过（{lastFailure}），5 秒后重试。");
            await Task.Delay(5000, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"TikTok 版权证明提交后复查未通过，不能标记为成功：{lastFailure}");
    }
}
