using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Ui.Services.TikTok;

/// <summary>移植自 Python <c>browser_actions.py</c> 的 TikTok 短剧中心表单自动化。</summary>
public static partial class TikTokBrowserActions
{
    private static readonly string[] DailyLimitMarkers =
    {
        "当前创建剧集已达上限",
        "创建剧集已达上限",
        "已达上限",
    };

    public static async Task FillCreatePublishFormAsync(
        IPage page,
        TikTokPublishPayload payload,
        TikTokPublishOptions options,
        TikTokPublishRecommendation recommendation,
        string coverPath,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await FillCreateInitialFieldsAsync(page, payload, options, log, ct);
        await UploadCoverAsync(page, coverPath, log, ct);
        await FillCreateRemainingFieldsAsync(
            page, payload, options, recommendation, coverPath, coverAlreadyUploaded: true, log, ct);
    }

    public static async Task FillCreateInitialFieldsAsync(
        IPage page,
        TikTokPublishPayload payload,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await SelectContractAsync(page, options, log, ct);
        await FillTextAsync(page, "#title", payload.Title, ct);
        await FillTextAsync(page, "#description", payload.Description, ct);
        await BlurActiveElementAsync(page);
        await page.WaitForTimeoutAsync(800);
        Log(log, "TikTok 新建流程已填写合同、剧名和简介。");
    }

    public static async Task FillCreateRemainingFieldsAsync(
        IPage page,
        TikTokPublishPayload payload,
        TikTokPublishOptions options,
        TikTokPublishRecommendation recommendation,
        string coverPath,
        bool coverAlreadyUploaded,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (options.UseBatchUpload)
        {
            await TikTokBatchUploadService.FillRemainingWithBatchedUploadAsync(
                page, payload, options, recommendation, coverPath, coverAlreadyUploaded, log, ct);
            return;
        }

        if (!coverAlreadyUploaded)
            await UploadCoverAsync(page, coverPath, log, ct);

        await UploadLocalVideosAsync(page, payload.VideoPaths.ToList(), waitForFinish: false, log, ct);
        await FillSharedPublishFieldsAsync(page, payload, options, recommendation, log, ct);
        Log(log, "TikTok 其余表单已填写完成，开始检查视频是否上传完成。");
        await WaitVideoUploadFinishedAsync(
            page,
            expectedCount: payload.VideoPaths.Count,
            titleCandidates: PayloadTitleCandidates(payload),
            stallSeconds: options.UploadStallSeconds,
            log,
            ct);
    }

    public static async Task SubmitAsync(IPage page, Action<string>? log, CancellationToken ct)
    {
        await DismissFloatingAssistantAsync(page, log);
        var button = page.Locator("button").Filter(new() { HasText = "提交" }).First;
        await WaitSubmitEnabledAsync(button, ct);
        await button.ClickAsync(new() { Timeout = 15000 });
        await ConfirmSubmitDialogIfPresentAsync(page, log, ct);
        Log(log, "TikTok 表单已提交。");
    }

    public static async Task SaveAsync(IPage page, Action<string>? log, CancellationToken ct)
    {
        await DismissFloatingAssistantAsync(page, log);
        var button = page.Locator("button").Filter(new() { HasText = "保存" }).First;
        await button.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        await button.ClickAsync(new() { Timeout = 15000 });
        await page.WaitForTimeoutAsync(800);
        Log(log, "TikTok 表单已保存，未执行最终提交。");
    }

    public static async Task<string?> DetectDailyEpisodeLimitAsync(IPage page)
    {
        string body;
        try { body = await page.Locator("body").InnerTextAsync(new() { Timeout = 5000 }); }
        catch { return null; }

        foreach (var marker in DailyLimitMarkers)
        {
            if (body.Contains(marker, StringComparison.Ordinal))
                return marker;
        }
        return null;
    }

    public static async Task UploadCoverAsync(IPage page, string coverPath, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var resolved = Path.GetFullPath(coverPath);
        if (!File.Exists(resolved))
            throw new InvalidOperationException($"封面不存在：{resolved}");

        await DismissLeavePageDialogIfPresentAsync(page, log);
        var trigger = page.Locator(".semi-upload-picture-add").First;
        if (await trigger.CountAsync() > 0)
        {
            try
            {
                var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
                {
                    await ClickWithFallbackAsync(trigger, ct);
                }, new() { Timeout = 15000 });
                await chooser.SetFilesAsync(resolved);
            }
            catch
            {
                if (!await DismissLeavePageDialogIfPresentAsync(page, log))
                    throw;
                var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
                {
                    await ClickWithFallbackAsync(trigger, ct);
                }, new() { Timeout = 15000 });
                await chooser.SetFilesAsync(resolved);
            }
        }
        else
        {
            var input = await FindCoverFileInputAsync(page);
            if (input is null)
                throw new InvalidOperationException("未找到 TikTok 封面上传控件。");
            await input.SetInputFilesAsync(resolved, new() { Timeout = 15000 });
        }

        Log(log, $"已选择 TikTok 封面: {Path.GetFileName(resolved)}");
        await page.WaitForTimeoutAsync(1500);
    }

    public static async Task FillSharedPublishFieldsAsync(
        IPage page,
        TikTokPublishPayload payload,
        TikTokPublishOptions options,
        TikTokPublishRecommendation recommendation,
        Action<string>? log,
        CancellationToken ct)
    {
        await SetSwitchAsync(page, "#anchorPromotionStatus", options.AnchorPromotionEnabled, ct);
        await SelectTargetAudienceAsync(page, recommendation.TargetAudience, log, ct);
        await SelectGenresAsync(page, recommendation.Genres, log, ct);
        await SelectTuxOptionByFieldAsync(page, ["源语言"], options.SourceLanguageLabels, fallbackIndex: 3, log, ct);
        await FillTextAsync(page, "#totalVideoNum", payload.EpisodeCount.ToString(), ct);
        await SelectTuxOptionByFieldAsync(
            page,
            ["是否 AI 短剧", "是否AI短剧"],
            new[] { options.IsAiDrama ? "是" : "否" },
            fallbackIndex: 4,
            log,
            ct);
        await AcceptPromiseAsync(page, log, ct);
        await SelectPublishModeAsync(page, options.PublishModeLabel, ct);
        await SetSwitchAsync(page, "#consignmentStatus", options.ConsignmentEnabled, ct);
        await ApplyCommercialModeAsync(page, options, log, ct);
    }

    private static async Task SelectContractAsync(
        IPage page,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        var combo = page.Locator(".semi-select").First;
        await combo.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });

        if (options.ContractIdMode == TikTokPublishConstants.ContractIdModeFirstAvailable)
        {
            await combo.ClickAsync(new() { Timeout = 10000 });
            await page.WaitForTimeoutAsync(500);
            var option = await FindFirstContractOptionAsync(page);
            if (option is null)
            {
                var visible = await CollectContractOptionTextsAsync(page);
                throw new InvalidOperationException($"未找到可用合同选项，当前可见选项: {string.Join(" | ", visible)}");
            }
            var text = await SafeInnerTextAsync(option);
            await option.ClickAsync(new() { Timeout = 10000 });
            Log(log, $"TikTok 合同模式：已使用默认第一个合同{(string.IsNullOrEmpty(text) ? "" : $"：{text}")}");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.ContractId))
        {
            Log(log, "TikTok 合同 ID 未配置，保留页面默认合同选择。");
            return;
        }

        await combo.ClickAsync(new() { Timeout = 10000 });
        await page.WaitForTimeoutAsync(500);
        var matched = await FindContractOptionAsync(page, options.ContractId);
        if (matched is null)
        {
            var visible = await CollectContractOptionTextsAsync(page);
            throw new InvalidOperationException(
                $"未找到匹配合同 ID 的选项: {options.ContractId}；当前可见选项: {string.Join(" | ", visible)}");
        }
        await matched.ClickAsync(new() { Timeout = 10000 });
        Log(log, $"已选择合同: {options.ContractId}");
    }

    private static async Task FillTextAsync(IPage page, string selector, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var locator = page.Locator(selector).First;
        await locator.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        await locator.FillAsync(value ?? "");
    }

    private static async Task SetSwitchAsync(IPage page, string selector, bool enabled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var locator = page.Locator(selector).First;
        await locator.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        var checkedState = await locator.IsCheckedAsync();
        if (checkedState != enabled)
            await locator.ClickAsync(new() { Force = true });
    }

    private static async Task ApplyCommercialModeAsync(
        IPage page,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        await FillTextAsync(page, "#previewVideoNumOnProfile", options.ProfilePreviewEpisodes.ToString(), ct);
        if (!options.PaidEnabled)
        {
            Log(log, $"商业模式已按付费=否填写个人页剧集展示集数：{options.ProfilePreviewEpisodes}");
            return;
        }

        await FillTextAsync(page, "#previewVideoNum", options.FreePreviewEpisodes.ToString(), ct);
        if (options.ExpectedFullPriceMode == "manual" && string.IsNullOrWhiteSpace(options.ExpectedFullPriceValue))
            throw new InvalidOperationException("是否付费=是 时，必须配置“预期全集价格设置”。");

        await SelectExpectedFullPriceAsync(page, options, ct);
        var label = options.ExpectedFullPriceMode == "option_index"
            ? $"第 {options.ExpectedFullPriceOptionIndex} 个价格选项"
            : options.ExpectedFullPriceLabel ?? options.ExpectedFullPriceValue;
        Log(log,
            $"商业模式已按付费=是填写个人页剧集展示集数：{options.ProfilePreviewEpisodes}，" +
            $"免费预览集数：{options.FreePreviewEpisodes}，预期全集价格：{label}");
    }

    private static async Task SelectTargetAudienceAsync(
        IPage page,
        string targetAudience,
        Action<string>? log,
        CancellationToken ct)
    {
        var key = string.IsNullOrWhiteSpace(targetAudience) ? "female" : targetAudience;
        var labels = TikTokPublishConstants.TargetAudienceAliases.TryGetValue(key, out var aliases)
            ? aliases
            : TikTokPublishConstants.TargetAudienceAliases["female"];
        await SelectTuxOptionByFieldAsync(page, ["目标观众", "目标受众"], labels, fallbackIndex: 1, log, ct);
    }

    private static async Task SelectGenresAsync(
        IPage page,
        IReadOnlyList<string> genres,
        Action<string>? log,
        CancellationToken ct)
    {
        var desired = genres.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct().ToList();
        if (desired.Count == 0) return;

        var combo = await FindComboboxByFieldLabelAsync(page, ["题材类型", "题材"])
                    ?? page.Locator("button[role='combobox']").Nth(2);
        await OpenComboboxAsync(page, combo, ct);

        var selected = await CollectSelectedGenreTextsAsync(page);
        foreach (var genre in selected.Where(g => !desired.Contains(g)).ToList())
        {
            var option = await FindGenreOptionAsync(page, genre);
            if (option is not null)
            {
                await ClickWithFallbackAsync(option, ct);
                await page.WaitForTimeoutAsync(250);
            }
        }

        selected = await CollectSelectedGenreTextsAsync(page);
        var unmatched = new List<string>();
        foreach (var genre in desired.Where(g => !selected.Contains(g)))
        {
            try
            {
                if (!await HasVisiblePopupOptionsAsync(page))
                    await OpenComboboxAsync(page, combo, ct);
                var option = await FindGenreOptionAsync(page, genre);
                if (option is null) { unmatched.Add(genre); continue; }
                await ClickWithFallbackAsync(option, ct);
                await page.WaitForTimeoutAsync(250);
            }
            catch (Exception ex)
            {
                unmatched.Add(genre);
                Log(log, $"TikTok 题材类型点击“{genre}”失败，已跳过：{ex.Message}");
            }
        }

        var finalSelected = await CollectSelectedGenreTextsAsync(page);
        await ClosePopupIfOpenAsync(page);
        if (finalSelected.Count == 0)
        {
            var visible = await CollectVisiblePopupTextsAsync(page);
            throw new InvalidOperationException(
                $"TikTok 题材类型未匹配到任何推荐选项（要求：{string.Join(" / ", desired)}）。当前可见选项: {string.Join(" | ", visible.Take(20))}");
        }
        Log(log, $"TikTok 题材类型已选择 {finalSelected.Count} 个：{string.Join(" / ", finalSelected)}");
        if (unmatched.Count > 0)
            Log(log, $"TikTok 题材类型未匹配成功：{string.Join(" / ", unmatched.Distinct())}");
    }

    private static async Task AcceptPromiseAsync(IPage page, Action<string>? log, CancellationToken ct)
    {
        await DismissFloatingAssistantAsync(page, log);
        var candidates = new[]
        {
            page.Locator("label").Filter(new() { HasText = "本人承诺" }).First,
            page.Locator("span").Filter(new() { HasText = "本人承诺" }).First,
            page.Locator(".semi-checkbox").First,
            page.Locator("input.semi-checkbox-input").First,
        };
        foreach (var candidate in candidates)
        {
            try
            {
                if (await candidate.CountAsync() == 0) continue;
                await candidate.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
                await ClickWithFallbackAsync(candidate, ct);
                await page.WaitForTimeoutAsync(600);
            }
            catch { /* 尝试下一个 */ }

            if (await HandlePromiseDrawerAsync(page, log, ct)) return;
            if (await IsMainPromiseCheckedAsync(page)) { Log(log, "已勾选本人承诺。"); return; }
        }

        if (await HandlePromiseDrawerAsync(page, log, ct)) return;
        if (await IsMainPromiseCheckedAsync(page)) { Log(log, "已勾选本人承诺。"); return; }
        throw new InvalidOperationException("TikTok 本人承诺未能勾选成功。");
    }

    private static async Task SelectPublishModeAsync(IPage page, string label, CancellationToken ct)
    {
        var radio = page.Locator("label").Filter(new() { HasText = label }).First;
        await radio.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        try
        {
            var cls = await radio.GetAttributeAsync("class") ?? "";
            if (cls.Contains("checked", StringComparison.OrdinalIgnoreCase)) return;
        }
        catch { /* ignore */ }

        await ClickWithFallbackAsync(radio, ct);
        await page.WaitForTimeoutAsync(300);
        var after = await radio.GetAttributeAsync("class") ?? "";
        if (!after.Contains("checked", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"TikTok 发布模式“{label}”点击后仍未选中。");
    }

    public static async Task DismissFloatingAssistantAsync(IPage page, Action<string>? log)
    {
        try
        {
            var hidden = await page.EvaluateAsync<int>(
                """
                () => {
                  const viewportWidth = window.innerWidth || document.documentElement.clientWidth || 0;
                  const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 0;
                  const protectText = /提交|保存|放弃更改|不同意|同意|确认|确定/;
                  let hidden = 0;
                  for (const node of Array.from(document.querySelectorAll('body *'))) {
                    if (!(node instanceof HTMLElement)) continue;
                    const style = window.getComputedStyle(node);
                    if (!['fixed', 'sticky'].includes(style.position)) continue;
                    const rect = node.getBoundingClientRect();
                    if (rect.width <= 0 || rect.height <= 0) continue;
                    const nearRight = viewportWidth - rect.right <= 80;
                    const nearBottom = viewportHeight - rect.bottom <= 80;
                    const small = rect.width <= 220 && rect.height <= 220;
                    const zIndex = Number.parseInt(style.zIndex || '0', 10) || 0;
                    const text = (node.innerText || '').trim();
                    if (!nearRight || !nearBottom || !small || zIndex < 1) continue;
                    if (protectText.test(text)) continue;
                    node.style.setProperty('display', 'none', 'important');
                    node.style.setProperty('visibility', 'hidden', 'important');
                    hidden += 1;
                  }
                  return hidden;
                }
                """);
            if (hidden > 0)
                Log(log, $"TikTok 页面已隐藏右下角浮动助手 {hidden} 个。");
        }
        catch { /* ignore */ }
    }

    public static IReadOnlyList<string> PayloadTitleCandidates(TikTokPublishPayload payload)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in new[] { payload.Title, payload.OriginalTitle })
        {
            var text = (value ?? "").Trim();
            if (string.IsNullOrEmpty(text) || !seen.Add(text)) continue;
            list.Add(text);
        }
        return list;
    }

    private static void Log(Action<string>? log, string message) => log?.Invoke(message);

    public static async Task ClickLocatorAsync(ILocator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { await locator.ClickAsync(new() { Timeout = 10000 }); }
        catch { await locator.ClickAsync(new() { Timeout = 10000, Force = true }); }
    }

    private static async Task ClickWithFallbackAsync(ILocator locator, CancellationToken ct)
        => await ClickLocatorAsync(locator, ct);

    private static async Task BlurActiveElementAsync(IPage page)
    {
        try { await page.EvaluateAsync("() => { if (document.activeElement) document.activeElement.blur(); }"); }
        catch { /* ignore */ }
    }

    private static async Task<bool> DismissLeavePageDialogIfPresentAsync(IPage page, Action<string>? log)
    {
        var buttons = new[] { "留在此页", "留在当前页", "取消", "关闭" };
        foreach (var text in buttons)
        {
            try
            {
                var btn = page.GetByRole(AriaRole.Button, new() { Name = text }).First;
                if (await btn.CountAsync() > 0 && await btn.IsVisibleAsync())
                {
                    await btn.ClickAsync(new() { Timeout = 3000 });
                    await page.WaitForTimeoutAsync(400);
                    return true;
                }
            }
            catch { /* try next */ }
        }
        return false;
    }

    private static async Task ConfirmSubmitDialogIfPresentAsync(IPage page, Action<string>? log, CancellationToken ct)
    {
        await page.WaitForTimeoutAsync(800);
        foreach (var text in new[] { "确认", "确定", "提交", "同意" })
        {
            try
            {
                var btn = page.Locator("button").Filter(new() { HasText = text }).First;
                if (await btn.CountAsync() > 0 && await btn.IsVisibleAsync())
                {
                    await btn.ClickAsync(new() { Timeout = 5000 });
                    Log(log, $"已确认提交对话框：{text}");
                    return;
                }
            }
            catch { /* try next */ }
        }
    }

    private static async Task WaitSubmitEnabledAsync(ILocator button, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddHours(2);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!await IsAriaDisabledAsync(button)) return;
            await Task.Delay(2000, ct);
        }
        throw new TimeoutException("TikTok 提交按钮一直不可点击。");
    }

    private static async Task<bool> IsAriaDisabledAsync(ILocator locator)
    {
        if (await locator.CountAsync() == 0) return true;
        var value = (await locator.GetAttributeAsync("aria-disabled") ?? "").Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(value)) return value == "true";
        try { return await locator.IsDisabledAsync(); }
        catch { return true; }
    }

    private static async Task<string> SafeInnerTextAsync(ILocator locator)
    {
        try { return (await locator.InnerTextAsync(new() { Timeout = 3000 })).Trim(); }
        catch { return ""; }
    }
}
