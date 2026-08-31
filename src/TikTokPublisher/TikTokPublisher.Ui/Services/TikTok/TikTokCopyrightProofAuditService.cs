using System.Collections.Concurrent;
using Microsoft.Playwright;
using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services.TikTok;

public sealed record TikTokCopyrightProofAuditProgress(
    int Completed,
    int Total,
    string CurrentTitle,
    TikTokCopyrightProofAuditItem? Result,
    string Stage);

public sealed record TikTokCopyrightProofAuditSelection(
    bool IncludePublished,
    bool IncludeVideoReviewing,
    int Concurrency)
{
    public int NormalizedConcurrency => Math.Clamp(Concurrency, 2, 8);

    public IReadOnlyList<string> SelectedPlatformStatuses()
    {
        var statuses = new List<string>();
        if (IncludePublished) statuses.Add("已发布");
        if (IncludeVideoReviewing) statuses.Add("视频检测中");
        return statuses;
    }
}

internal enum TikTokCopyrightProofPageAccessState
{
    Editable,
    Approved,
    Uneditable,
}

public static class TikTokCopyrightProofAuditService
{
    public const string VideoReviewUneditableMessage =
        "剧集正片部分集数视频文件审核中，审核期间暂不支持编辑，请耐心等待审核结果。";
    public const string CopyrightApprovedMessage =
        "版权审核已通过，平台不再允许编辑版权证明。";
    public const string CopyrightUneditableMessage =
        "版权证明页面当前不可编辑，已跳过检查。";

    public static async Task<IReadOnlyList<TikTokCopyrightProofAuditItem>> AuditAsync(
        TikTokAccountProfile account,
        IEmbeddedBrowser? browser,
        TikTokCopyrightProofAuditSelection selection,
        IProgress<TikTokCopyrightProofAuditProgress>? progress,
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

            IPage listPage;
            if (useLaunch)
            {
                var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(account);
                (playwright, chromium, listPage) = await EmbeddedBrowserAutomationBridge
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
                (playwright, chromium, listPage) = await EmbeddedBrowserAutomationBridge
                    .ConnectPageAsync(browser, TikTokUrls.DefaultSeriesListUrl, log, ct)
                    .ConfigureAwait(false);
            }

            var selectedStatuses = selection.SelectedPlatformStatuses();
            if (selectedStatuses.Count == 0)
                throw new InvalidOperationException("请至少选择一种需要检查的剧集状态。");
            var concurrency = selection.NormalizedConcurrency;

            log?.Invoke(
                $"开始按状态读取当前账号原创管理剧集：{string.Join("、", selectedStatuses)}；" +
                $"版权证明检测并发数：{concurrency}。");
            await TikTokSeriesListLookupService.OpenAsync(listPage, log, ct).ConfigureAwait(false);
            var selectedRows = new List<TikTokSeriesListRow>();
            foreach (var status in selectedStatuses)
            {
                ct.ThrowIfCancellationRequested();
                var pageProgress = new Progress<TikTokSeriesListEnumerationProgress>(update =>
                {
                    var totalPages = update.TotalPages ?? Math.Max(1, update.CurrentPage);
                    var retryLabel = update.AttemptNumber > 1 ? "（重试）" : string.Empty;
                    var detail = update.CurrentPageRowCount.HasValue
                        ? $"当前页 {update.CurrentPageRowCount.Value} 条，" +
                          $"累计 {update.CollectedUniqueCount} 个剧集"
                        : $"正在等待第 {update.CurrentPage} 页稳定加载…";
                    progress?.Report(new TikTokCopyrightProofAuditProgress(
                        update.CurrentPage,
                        totalPages,
                        detail,
                        null,
                        $"读取“{update.PlatformStatus}”列表{retryLabel}"));
                });
                var rows = await TikTokSeriesListLookupService
                    .EnumerateAllAsync(
                        listPage,
                        log,
                        ct,
                        statusFilter: status,
                        preferredPageSize: 50,
                        progress: pageProgress)
                    .ConfigureAwait(false);
                selectedRows.AddRange(rows);
            }

            var auditRows = selectedRows
                .DistinctBy(row =>
                    !string.IsNullOrWhiteSpace(row.SeriesId)
                        ? $"id:{row.SeriesId}"
                        : $"url:{row.DetailUrl}|title:{row.Title}")
                .ToArray();

            log?.Invoke(
                $"原创管理读取完成：所选状态共 {auditRows.Length} 个唯一剧集；" +
                "开始只读检查版权证明页面。");
            progress?.Report(new TikTokCopyrightProofAuditProgress(
                0,
                auditRows.Length,
                string.Empty,
                null,
                $"已完成列表读取，并发 {concurrency}"));

            if (auditRows.Length == 0)
                return [];

            var results = new ConcurrentDictionary<int, TikTokCopyrightProofAuditItem>();
            var completed = 0;
            var indexedRows = auditRows
                .Select((row, index) => (Row: row, Order: index + 1))
                .ToArray();
            var configuredMaterialTypes = TikTokPublishConstants
                .NormalizeCopyrightMaterialTypes(account.TiktokCopyrightMaterialTypes)
                .ToArray();

            await Parallel.ForEachAsync(
                indexedRows,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = concurrency,
                    CancellationToken = ct,
                },
                async (entry, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var result = await AuditOneAsync(
                            listPage.Context,
                            entry.Row,
                            entry.Order,
                            configuredMaterialTypes,
                            log,
                            token)
                        .ConfigureAwait(false);
                    results[entry.Order] = result;
                    var currentCompleted = Interlocked.Increment(ref completed);
                    progress?.Report(new TikTokCopyrightProofAuditProgress(
                        currentCompleted,
                        auditRows.Length,
                        entry.Row.Title,
                        result,
                        $"检查版权证明（并发 {concurrency}）"));
                }).ConfigureAwait(false);

            return results
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value)
                .ToArray();
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
                // 外部浏览器由任务关闭；CDP 模式只断开自动化连接。
            }

            playwright?.Dispose();
        }
    }

    private static async Task<TikTokCopyrightProofAuditItem> AuditOneAsync(
        IBrowserContext context,
        TikTokSeriesListRow row,
        int order,
        IReadOnlyList<string> configuredMaterialTypes,
        Action<string>? log,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(row.DetailUrl))
        {
            return Failed(
                order,
                row,
                "平台列表未提供可用的详情地址");
        }

        IPage? page = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            log?.Invoke($"版权证明检查 {order}：{row.Title}");
            page = await context.NewPageAsync().ConfigureAwait(false);
            page.Dialog += (_, dialog) => _ = dialog.DismissAsync();
            await page.GotoAsync(row.DetailUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 90000,
            }).ConfigureAwait(false);
            try
            {
                await page.WaitForLoadStateAsync(
                    LoadState.NetworkIdle,
                    new PageWaitForLoadStateOptions { Timeout = 12000 }).ConfigureAwait(false);
            }
            catch
            {
                // 原创中心为 SPA，持续网络请求不影响只读检查。
            }

            if (IsLoginPage(page.Url))
                return Failed(order, row, "登录状态失效");

            await TikTokBrowserActions.DismissFloatingAssistantAsync(page, log).ConfigureAwait(false);
            if (await IsUneditableDuringVideoReviewAsync(page, ct).ConfigureAwait(false))
            {
                var skipped = SkippedUneditable(order, row, VideoReviewUneditableMessage);
                log?.Invoke($"版权证明检查跳过：{row.Title}，{VideoReviewUneditableMessage}");
                return skipped;
            }
            if (!await OpenCopyrightProofTabAsync(page, ct).ConfigureAwait(false))
                return Failed(order, row, "未找到版权证明标签页");

            var pageAccess = await ProbeCopyrightProofPageAccessAsync(page, ct)
                .ConfigureAwait(false);
            if (pageAccess == TikTokCopyrightProofPageAccessState.Approved)
            {
                log?.Invoke($"版权证明检查跳过：{row.Title}，{CopyrightApprovedMessage}");
                return SkippedApproved(order, row);
            }
            if (pageAccess == TikTokCopyrightProofPageAccessState.Uneditable)
            {
                log?.Invoke($"版权证明检查跳过：{row.Title}，{CopyrightUneditableMessage}");
                return SkippedUneditable(order, row, CopyrightUneditableMessage);
            }

            var probe = await TikTokBrowserActions
                .ProbeConfiguredCopyrightProofMaterialsAsync(
                    page,
                    configuredMaterialTypes,
                    ct)
                .ConfigureAwait(false);
            if (!probe.FormAvailable)
                return Failed(order, row, string.Join("；", probe.Details));

            var state = Classify(probe.Plan);
            var result = new TikTokCopyrightProofAuditItem(
                order,
                row.Title,
                row.SeriesId,
                row.DetailUrl,
                state,
                BuildCoverageDetail(probe),
                DateTimeOffset.Now)
            {
                PlatformStatus = row.PlatformStatus,
            };

            log?.Invoke(
                $"版权证明检查完成：{row.Title}，" +
                $"{StateText(result.State)}。");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"版权证明检查失败：{row.Title}，{ex.Message}");
            return Failed(order, row, ex.Message);
        }
        finally
        {
            if (page is not null)
            {
                try { await page.CloseAsync().ConfigureAwait(false); }
                catch { /* 页面可能已经随浏览器关闭。 */ }
            }
        }
    }

    private static async Task<bool> OpenCopyrightProofTabAsync(
        IPage page,
        CancellationToken ct)
    {
        var existingField = page.Locator("[x-field-id^='copyrightProof.']").First;
        if (await existingField.CountAsync().ConfigureAwait(false) > 0 &&
            await existingField.IsVisibleAsync().ConfigureAwait(false))
        {
            return true;
        }

        foreach (var text in new[]
                 {
                     "版权证明",
                     "Copyright proof",
                     "Copyright Proof",
                     "contentPartnerHub_seriesEditPage_copyrightProof",
                 })
        {
            var candidates = page.GetByText(text, new() { Exact = true });
            var count = await candidates.CountAsync().ConfigureAwait(false);
            for (var index = count - 1; index >= 0; index--)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = candidates.Nth(index);
                try
                {
                    if (!await candidate.IsVisibleAsync().ConfigureAwait(false))
                        continue;
                    await candidate.ClickAsync(new() { Timeout = 15000 }).ConfigureAwait(false);
                    await page.WaitForTimeoutAsync(300).ConfigureAwait(false);
                    return true;
                }
                catch
                {
                    // 尝试下一个本地化文本候选。
                }
            }
        }

        var tabs = page.Locator("[role='tab'], .semi-tabs-tab");
        var tabCount = await tabs.CountAsync().ConfigureAwait(false);
        for (var index = 0; index < tabCount; index++)
        {
            ct.ThrowIfCancellationRequested();
            var tab = tabs.Nth(index);
            string text;
            try
            {
                if (!await tab.IsVisibleAsync().ConfigureAwait(false))
                    continue;
                text = await tab.InnerTextAsync(new() { Timeout = 1500 }).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            if (!text.Contains("版权", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("copyright", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await tab.ClickAsync(new() { Timeout = 15000 }).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(300).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private static TikTokCopyrightProofAuditItem Failed(
        int order,
        TikTokSeriesListRow row,
        string detail) =>
        new(
            order,
            row.Title,
            row.SeriesId,
            row.DetailUrl,
            TikTokCopyrightProofAuditState.Failed,
            detail,
            DateTimeOffset.Now)
        {
            PlatformStatus = row.PlatformStatus,
        };

    private static TikTokCopyrightProofAuditItem SkippedUneditable(
        int order,
        TikTokSeriesListRow row,
        string detail) =>
        new(
            order,
            row.Title,
            row.SeriesId,
            row.DetailUrl,
            TikTokCopyrightProofAuditState.SkippedUneditable,
            detail,
            DateTimeOffset.Now)
        {
            PlatformStatus = row.PlatformStatus,
        };

    private static TikTokCopyrightProofAuditItem SkippedApproved(
        int order,
        TikTokSeriesListRow row) =>
        new(
            order,
            row.Title,
            row.SeriesId,
            row.DetailUrl,
            TikTokCopyrightProofAuditState.SkippedApproved,
            CopyrightApprovedMessage,
            DateTimeOffset.Now)
        {
            PlatformStatus = row.PlatformStatus,
        };

    internal static bool IsUneditableDuringVideoReviewText(string? text)
    {
        var value = (text ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal);
        return value.Contains("剧集正片部分集数视频文件审核中", StringComparison.Ordinal) &&
               value.Contains("审核期间暂不支持编辑", StringComparison.Ordinal);
    }

    internal static bool IsCopyrightReviewPassedText(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Contains(
                   "contentPartnerHub_seriesEditPage_copyrightReview_passed",
                   StringComparison.OrdinalIgnoreCase) ||
               value.Contains("版权审核通过", StringComparison.Ordinal) ||
               value.Contains("版权证明审核通过", StringComparison.Ordinal) ||
               value.Contains("Copyright review passed", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsUneditableDuringVideoReviewAsync(
        IPage page,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var candidates = page.GetByText("审核期间暂不支持编辑", new() { Exact = false });
        var count = await candidates.CountAsync().ConfigureAwait(false);
        for (var index = 0; index < count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = candidates.Nth(index);
            try
            {
                if (!await candidate.IsVisibleAsync().ConfigureAwait(false))
                    continue;
                var text = await candidate.InnerTextAsync(new() { Timeout = 1500 })
                    .ConfigureAwait(false);
                if (IsUneditableDuringVideoReviewText(text))
                    return true;
            }
            catch
            {
                // 审核提示可能随详情页加载刷新，继续检查其他候选。
            }
        }

        try
        {
            var bodyText = await page.Locator("body").InnerTextAsync(new() { Timeout = 3000 })
                .ConfigureAwait(false);
            return IsUneditableDuringVideoReviewText(bodyText);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<TikTokCopyrightProofPageAccessState>
        ProbeCopyrightProofPageAccessAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string bodyText;
        try
        {
            bodyText = await page.Locator("body").InnerTextAsync(new() { Timeout = 3000 })
                .ConfigureAwait(false);
        }
        catch
        {
            bodyText = string.Empty;
        }

        if (IsCopyrightReviewPassedText(bodyText))
            return TikTokCopyrightProofPageAccessState.Approved;

        try
        {
            var state = await page.EvaluateAsync<string>(
                """
                () => {
                  const visible = element => {
                    if (!(element instanceof HTMLElement)) return false;
                    const style = getComputedStyle(element);
                    const rect = element.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' &&
                      Number(style.opacity || '1') > 0 && rect.width > 0 && rect.height > 0;
                  };
                  const disabled = element => {
                    if (element.disabled) return true;
                    if (element.getAttribute('aria-disabled') === 'true') return true;
                    return Boolean(element.closest(
                      '[aria-disabled="true"], .semi-disabled, [class*="-disabled"]'));
                  };
                  const fields = [...document.querySelectorAll(
                    '[x-field-id^="copyrightProof."]')].filter(visible);
                  if (fields.length === 0) return 'missing';
                  const controls = fields.flatMap(field => [...field.querySelectorAll(
                    'input, textarea, select, button, [role="radio"], [role="checkbox"], ' +
                    '[role="combobox"], [contenteditable="true"]')]);
                  return controls.some(control => visible(control) && !disabled(control))
                    ? 'editable'
                    : 'uneditable';
                }
                """).ConfigureAwait(false);
            return string.Equals(state, "uneditable", StringComparison.Ordinal)
                ? TikTokCopyrightProofPageAccessState.Uneditable
                : TikTokCopyrightProofPageAccessState.Editable;
        }
        catch
        {
            // DOM 仍在重绘时由后续材料探测负责给出明确失败原因。
            return TikTokCopyrightProofPageAccessState.Editable;
        }
    }

    private static TikTokCopyrightProofAuditState Classify(
        TikTokCopyrightMaterialCompletionPlan plan)
    {
        if (plan.IsComplete)
            return TikTokCopyrightProofAuditState.HasMaterial;
        if (plan.ExistingMaterialTypes.Count == 0)
            return TikTokCopyrightProofAuditState.MissingMaterial;
        if (plan.ExistingMaterialTypes.Count == 1 &&
            plan.ExistingMaterialTypes.Contains(
                TikTokPublishConstants.ProductionAgreementMaterialType,
                StringComparer.Ordinal))
        {
            return TikTokCopyrightProofAuditState.ProductionAgreementOnly;
        }

        return TikTokCopyrightProofAuditState.PartialMaterial;
    }

    private static string BuildCoverageDetail(CopyrightProofMaterialCoverageProbe probe)
    {
        var missingLabels = probe.Plan.MissingMaterialTypes
            .Select(materialType =>
                TikTokPublishConstants.CopyrightMaterialLabels.TryGetValue(materialType, out var label)
                    ? label
                    : materialType)
            .ToArray();
        return missingLabels.Length == 0
            ? "账号配置的版权证明材料均已上传"
            : $"缺少：{string.Join("、", missingLabels)}";
    }

    private static string StateText(TikTokCopyrightProofAuditState state) =>
        state switch
        {
            TikTokCopyrightProofAuditState.HasMaterial => "版权证明材料齐全",
            TikTokCopyrightProofAuditState.ProductionAgreementOnly => "仅上传版权证明 PDF",
            TikTokCopyrightProofAuditState.PartialMaterial => "部分版权证明材料缺失",
            TikTokCopyrightProofAuditState.MissingMaterial => "所有版权证明均未填写",
            TikTokCopyrightProofAuditState.SkippedApproved => "版权审核通过，已跳过",
            TikTokCopyrightProofAuditState.SkippedUneditable => "暂不可编辑，已跳过",
            _ => "检查失败",
        };

    private static bool IsLoginPage(string? url) =>
        (url ?? string.Empty).Contains("/login", StringComparison.OrdinalIgnoreCase);
}
