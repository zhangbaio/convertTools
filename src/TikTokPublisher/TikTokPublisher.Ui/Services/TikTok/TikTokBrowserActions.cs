using System.Globalization;
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
        "今日创建剧集已达上限",
        "今日创建剧集数量已达上限",
    };
    private static readonly string[] DailyLimitContextMarkers =
    {
        "请明天再进行操作",
    };
    private static readonly string[] DailyLimitVisibleSelectors =
    {
        ".semi-toast",
        ".semi-toast-content",
        "[role='alert']",
        ".semi-notification",
        ".semi-banner",
        ".semi-modal",
        ".semi-modal-content",
        ".semi-dialog",
        ".semi-dialog-content",
        "[role='dialog']",
    };
    private static readonly string[] LeavePageDialogMarkers =
    {
        "是否离开网站",
        "更改可能未保存",
        "离开网站",
        "Leave site",
        "Changes you made may not be saved",
        "unsaved changes",
    };
    private static readonly string[] ConfirmLeavePageButtonTexts =
    {
        "离开",
        "确定",
        "确认",
        "Leave",
        "Leave site",
        "Discard",
        "Discard changes",
        "OK",
    };

    internal static async Task<bool> LooksLikeTikTokCrashPageAsync(IPage page)
    {
        try
        {
            var text = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions
            {
                Timeout = 3000,
            }).ConfigureAwait(false);
            return ContainsTikTokCrashMarker(text);
        }
        catch
        {
            return false;
        }
    }

    internal static bool ContainsTikTokCrashMarker(string? text)
    {
        var value = text ?? "";
        return value.Contains("出了点问题", StringComparison.Ordinal) ||
               value.Contains("Minified React error", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("reactjs.org/docs/error-decoder", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("error-decoder.html", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("invariant=185", StringComparison.OrdinalIgnoreCase);
    }

    internal static void ThrowIfTikTokCrashText(string? text)
    {
        if (ContainsTikTokCrashMarker(text))
            throw new InvalidOperationException("TikTok 页面崩溃（出了点问题 / React error）");
    }

    internal static void ThrowIfDailyEpisodeLimitText(string? text)
    {
        var message = DetectDailyLimitText(text);
        if (message is not null)
            throw new TikTokDailyLimitException(message);
    }

    internal static async Task ThrowIfDailyEpisodeLimitAsync(IPage page)
    {
        var message = await DetectDailyEpisodeLimitAsync(page).ConfigureAwait(false);
        if (message is not null)
            throw new TikTokDailyLimitException(message);
    }

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

        var uploadPaths = payload.UploadVideoPaths.Count > 0
            ? payload.UploadVideoPaths.ToList()
            : payload.VideoPaths.ToList();
        await UploadLocalVideosAsync(page, uploadPaths, waitForFinish: false, log, ct);
        await FillSharedPublishFieldsAsync(page, payload, options, recommendation, log, ct);
        Log(log, "TikTok 其余表单已填写完成，开始检查视频是否上传完成。");
        await WaitVideoUploadFinishedAsync(
            page,
            expectedCount: uploadPaths.Count,
            titleCandidates: PayloadTitleCandidates(payload),
            stallSeconds: options.UploadStallSeconds,
            log,
            ct,
            videoPaths: uploadPaths);
    }

    public static async Task SubmitAsync(
        IPage page,
        Action<string>? log,
        CancellationToken ct,
        IReadOnlyList<string>? titleCandidates = null,
        bool verifySeriesListStatus = true)
    {
        await WaitBeforeSubmitAsync(log, ct).ConfigureAwait(false);
        await DismissFloatingAssistantAsync(page, log);
        var button = page.Locator("button:visible").Filter(new() { HasText = "提交" }).First;
        await WaitSubmitEnabledAsync(button, log, ct);
        await button.ClickAsync(new() { Timeout = 15000 });
        await ConfirmSubmitDialogIfPresentAsync(page, log, ct);
        var dailyLimit = await DetectDailyEpisodeLimitAsync(page).ConfigureAwait(false);
        if (dailyLimit is not null)
            throw new TikTokDailyLimitException(dailyLimit);
        if (verifySeriesListStatus)
        {
            await VerifySubmitAcceptedAsync(page, titleCandidates, log, ct).ConfigureAwait(false);
            Log(log, "TikTok 表单已提交并通过平台状态校验。");
        }
        else
        {
            Log(log, "TikTok 表单已提交，等待版权证明材料落库复查。");
        }
    }

    public static async Task WaitBeforeSubmitAsync(Action<string>? log, CancellationToken ct, double seconds = 10)
    {
        var totalSeconds = Math.Max(0.1, seconds);
        Log(log, $"点击提交前停留 {(int)totalSeconds} 秒，方便核对表单内容。");
        var deadline = DateTime.UtcNow.AddSeconds(totalSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;
            await Task.Delay(remaining > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining, ct)
                .ConfigureAwait(false);
        }
    }

    public static async Task SaveAsync(IPage page, Action<string>? log, CancellationToken ct)
    {
        await WaitBeforeSubmitAsync(log, ct).ConfigureAwait(false);
        await DismissFloatingAssistantAsync(page, log);
        var button = page.Locator("button").Filter(new() { HasText = "保存" }).First;
        await button.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        await button.ClickAsync(new() { Timeout = 15000 });
        await page.WaitForTimeoutAsync(800);
        var dailyLimit = await DetectDailyEpisodeLimitAsync(page).ConfigureAwait(false);
        if (dailyLimit is not null)
            throw new TikTokDailyLimitException(dailyLimit);
        Log(log, "TikTok 表单已保存，未执行最终提交。");
    }

    public static async Task<string?> DetectDailyEpisodeLimitAsync(IPage page)
    {
        // The limit toast may only live for a fraction of a second while CDP is injecting files.
        // Reading the rendered page text once is substantially faster than crossing the CDP
        // boundary for every candidate container, and includes portal-mounted toast content.
        // Callers start continuous detection only after entering the current create flow, so an
        // old page from the previous queue item cannot cause a false positive here.
        try
        {
            var bodyText = await page.Locator("body").InnerTextAsync(new()
            {
                Timeout = 1000,
            }).ConfigureAwait(false);
            var bodyDetected = DetectDailyLimitText(bodyText);
            if (bodyDetected is not null)
                return bodyDetected;
        }
        catch
        {
            // The page can be busy during file injection; fall back to focused containers.
        }

        foreach (var selector in DailyLimitVisibleSelectors)
        {
            try
            {
                var locator = page.Locator(selector);
                var count = Math.Min(await locator.CountAsync().ConfigureAwait(false), 12);
                for (var index = 0; index < count; index++)
                {
                    var item = locator.Nth(index);
                    if (!await item.IsVisibleAsync(new() { Timeout = 500 }).ConfigureAwait(false))
                        continue;

                    var text = await item.InnerTextAsync(new() { Timeout = 500 }).ConfigureAwait(false);
                    var detected = DetectDailyLimitText(text);
                    if (detected is not null)
                        return detected;
                }
            }
            catch { /* try next source */ }
        }

        return null;
    }

    private static string? DetectDailyLimitText(string? text)
    {
        var normalized = NormalizeWhitespace(text ?? "");
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        foreach (var marker in DailyLimitMarkers)
        {
            if (normalized.Contains(marker, StringComparison.Ordinal))
                return normalized.Length <= 160 ? normalized : marker;
        }

        foreach (var marker in DailyLimitContextMarkers)
        {
            if (normalized.Contains(marker, StringComparison.Ordinal) &&
                normalized.Contains("创建", StringComparison.Ordinal) &&
                normalized.Contains("剧集", StringComparison.Ordinal))
                return normalized.Length <= 160 ? normalized : marker;
        }

        return null;
    }

    public static async Task UploadCoverAsync(IPage page, string coverPath, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var resolved = Path.GetFullPath(coverPath);
        if (!File.Exists(resolved))
            throw new InvalidOperationException($"封面不存在：{resolved}");

        if (await IsCoverAlreadyUploadedAsync(page))
        {
            Log(log, "TikTok 封面已存在，跳过上传。");
            await VerifyCoverUploadCompleteAsync(page, log, ct);
            return;
        }

        await DismissLeavePageDialogIfPresentAsync(page, log);
        if (!await TryFeedCoverFileAsync(page, resolved, log, ct))
            throw new InvalidOperationException("未找到 TikTok 封面上传控件。");

        Log(log, $"已选择 TikTok 封面: {Path.GetFileName(resolved)}");
        await page.WaitForTimeoutAsync(1500);
        await ConfirmCoverCropDialogIfPresentAsync(page, log, ct);
        await VerifyCoverUploadCompleteAsync(page, log, ct);
    }

    private static async Task<bool> TryFeedCoverFileAsync(
        IPage page,
        string resolved,
        Action<string>? log,
        CancellationToken ct)
    {
        // 对齐 Python upload_cover：优先点击 semi-upload-picture-add + file chooser，失败再用 coverStruct 隐藏 input。
        foreach (var triggerSelector in new[]
                 {
                     "#coverStruct .semi-upload-picture-add",
                     "[x-field-id='coverStruct'] .semi-upload-picture-add",
                     ".semi-upload-picture-add",
                 })
        {
            var trigger = page.Locator(triggerSelector).First;
            if (await trigger.CountAsync() == 0) continue;
            if (await TryFeedCoverViaChooserAsync(page, trigger, resolved, log, ct))
                return true;
        }

        var input = await FindCoverFileInputAsync(page);
        if (input is not null)
        {
            await input.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
            await input.SetInputFilesAsync(resolved, new() { Timeout = 15000 });
            return true;
        }

        return false;
    }

    private static async Task<bool> TryFeedCoverViaChooserAsync(
        IPage page,
        ILocator trigger,
        string resolved,
        Action<string>? log,
        CancellationToken ct)
    {
        try
        {
            await trigger.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
            var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
            {
                await ClickWithFallbackAsync(trigger, ct);
            }, new() { Timeout = 15000 });
            await chooser.SetFilesAsync(resolved);
            return true;
        }
        catch
        {
            if (!await DismissLeavePageDialogIfPresentAsync(page, log)) return false;
            try
            {
                var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
                {
                    await ClickWithFallbackAsync(trigger, ct);
                }, new() { Timeout = 15000 });
                await chooser.SetFilesAsync(resolved);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static async Task<bool> IsCoverAlreadyUploadedAsync(IPage page)
    {
        var coverFieldFound = false;
        foreach (var selector in new[]
                 {
                     "#coverStruct",
                     "[x-field-id='coverStruct']",
                     ".uploadField-Xm2Vjl",
                 })
        {
            try
            {
                var root = page.Locator(selector).First;
                if (await root.CountAsync() == 0) continue;
                coverFieldFound = true;
                if (await HasCoverUploadPreviewAsync(root)) return true;

                var addButton = root.Locator(".semi-upload-picture-add").First;
                if (await addButton.CountAsync() > 0 && await addButton.IsVisibleAsync())
                    return false;
            }
            catch { /* try next */ }
        }

        try
        {
            var preview = page.Locator("#coverStruct img, [x-field-id='coverStruct'] img").First;
            if (await preview.CountAsync() > 0 && await preview.IsVisibleAsync()) return true;
        }
        catch { /* ignore */ }

        if (!coverFieldFound)
        {
            try
            {
                var card = page.Locator(".semi-upload-picture-file-card, .semi-upload-picture-file-card-preview")
                    .First;
                if (await card.CountAsync() > 0 && await card.IsVisibleAsync()) return true;
            }
            catch { /* ignore */ }
        }

        return false;
    }

    private static async Task<bool> HasCoverUploadPreviewAsync(ILocator root)
    {
        try
        {
            var hasImagePreview = await root.Locator("img").EvaluateAllAsync<bool>(
                """
                (imgs) => imgs.some((img) => {
                  const src = img.currentSrc || img.src || "";
                  if (!src) return false;
                  const rect = img.getBoundingClientRect();
                  const style = getComputedStyle(img);
                  return rect.width >= 40 &&
                    rect.height >= 40 &&
                    style.display !== "none" &&
                    style.visibility !== "hidden" &&
                    Number(style.opacity || "1") > 0;
                })
                """);
            if (hasImagePreview) return true;
        }
        catch { /* ignore */ }

        try
        {
            var replaceButton = root.Locator("button, span, div")
                .Filter(new() { HasText = "替换封面" })
                .First;
            if (await replaceButton.CountAsync() > 0 && await replaceButton.IsVisibleAsync())
                return true;
        }
        catch { /* ignore */ }

        try
        {
            var uploadedCard = root.Locator(".semi-upload-picture-file-card, .semi-upload-picture-file-card-preview")
                .First;
            if (await uploadedCard.CountAsync() > 0 && await uploadedCard.IsVisibleAsync())
                return true;
        }
        catch { /* ignore */ }

        return false;
    }

    private static async Task WaitForCoverUploadAppliedAsync(IPage page, Action<string>? log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsCoverAlreadyUploadedAsync(page))
            {
                Log(log, "TikTok 封面已上传并就绪。");
                return;
            }

            if (await ConfirmCoverCropDialogIfPresentAsync(page, log, ct))
                continue;

            await page.WaitForTimeoutAsync(800);
        }

        throw new InvalidOperationException("TikTok 封面上传后未检测到「替换封面」或封面预览，可能裁剪未确认成功。");
    }

    private static async Task<bool> ConfirmCoverCropDialogIfPresentAsync(IPage page, Action<string>? log, CancellationToken ct)
    {
        string body;
        try { body = await page.Locator("body").InnerTextAsync(new() { Timeout = 3000 }); }
        catch { return false; }

        if (!body.Contains("剪裁", StringComparison.Ordinal) && !body.Contains("裁剪", StringComparison.Ordinal))
            return false;

        var dialog = page.Locator("[role='dialog']").Filter(new() { HasText = "剪裁" }).First;
        if (await dialog.CountAsync() == 0)
            dialog = page.Locator("[role='dialog']").Filter(new() { HasText = "裁剪" }).First;

        foreach (var text in new[] { "Confirm", "确认", "确定" })
        {
            try
            {
                var btn = (await dialog.CountAsync() > 0
                        ? dialog.Locator("button").Filter(new() { HasText = text })
                        : page.Locator("button").Filter(new() { HasText = text }))
                    .First;
                if (await btn.CountAsync() == 0 || !await btn.IsVisibleAsync()) continue;
                await btn.ClickAsync(new() { Timeout = 5000 });
                await page.WaitForTimeoutAsync(1200);
                Log(log, $"已确认封面裁剪对话框：{text}");
                return true;
            }
            catch { /* try next */ }
        }

        return false;
    }

    public static async Task FillSharedPublishFieldsAsync(
        IPage page,
        TikTokPublishPayload payload,
        TikTokPublishOptions options,
        TikTokPublishRecommendation recommendation,
        Action<string>? log,
        CancellationToken ct,
        bool preserveExistingCopyrightMaterials = false)
    {
        await EnsureSeriesDetailsStepAsync(page, ct);
        await SetSwitchAsync(page, "#anchorPromotionStatus", options.AnchorPromotionEnabled, ct);
        await PauseBetweenFieldsAsync(page);

        await SelectTargetAudienceAsync(page, recommendation.TargetAudience, log, ct);
        await VerifyTargetAudienceAsync(page, recommendation.TargetAudience, log, ct);
        await PauseBetweenFieldsAsync(page);

        await SelectGenresAsync(page, recommendation.Genres, log, ct);
        await PauseBetweenFieldsAsync(page);

        await SelectTuxOptionByFieldAsync(page, ["源语言"], options.SourceLanguageLabels, fallbackIndex: 3, log, ct);
        await VerifyComboboxFieldAsync(page, ["源语言"], options.SourceLanguageLabels, "源语言", log, ct);
        await PauseBetweenFieldsAsync(page);

        await FillEpisodeCountAsync(page, payload.EpisodeCount, log, ct);
        await VerifyEpisodeCountAsync(page, payload.EpisodeCount, log, ct);
        await PauseBetweenFieldsAsync(page);

        await EnsureAllPublishAccountsSelectedAsync(page, log, ct);
        await PauseBetweenFieldsAsync(page);

        await SelectTuxOptionByFieldAsync(
            page,
            ["是否 AI 短剧", "是否AI短剧"],
            new[] { options.IsAiDrama ? "是" : "否" },
            fallbackIndex: 4,
            log,
            ct);
        await VerifyComboboxFieldAsync(
            page,
            ["是否 AI 短剧", "是否AI短剧"],
            new[] { options.IsAiDrama ? "是" : "否" },
            "是否 AI 短剧",
            log,
            ct);
        await PauseBetweenFieldsAsync(page);

        await SelectContentCreationTypeAsync(page, options, log, ct);
        await PauseBetweenFieldsAsync(page);

        if (preserveExistingCopyrightMaterials)
        {
            await ConfigureCopyrightProofForEditAsync(page, options, log, ct);
        }
        else
        {
            await ConfigureCopyrightProofAsync(page, options, log, ct);
        }
        await PauseBetweenFieldsAsync(page);

        await AcceptPromiseAsync(page, log, ct);
        await PauseBetweenFieldsAsync(page);

        await SelectPublishModeAsync(page, options.PublishModeLabel, ct);
        await PauseBetweenFieldsAsync(page);
        await SetSwitchAsync(page, "#consignmentStatus", options.ConsignmentEnabled, ct);
        await ApplyCommercialModeAsync(page, options, log, ct);
        await ApplyZeroCostAdsAsync(page, options, log, ct);
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
            await OpenContractDropdownAsync(page, combo, ct);
            var option = await WaitForFirstContractOptionAsync(page, ct);
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

        await OpenContractDropdownAsync(page, combo, ct);
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

    private static async Task OpenContractDropdownAsync(IPage page, ILocator combo, CancellationToken ct)
    {
        await combo.ClickAsync(new() { Timeout = 10000 });
        await page.WaitForTimeoutAsync(500);
        try
        {
            var input = combo.Locator("input").First;
            if (await input.CountAsync() > 0)
                await input.ClickAsync(new() { Timeout = 3000 });
        }
        catch { /* optional search input */ }
        await page.WaitForTimeoutAsync(300);
    }

    private static async Task EnsureSeriesDetailsStepAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var tab = page.GetByText("剧集详情", new() { Exact = false }).First;
            if (await tab.CountAsync() > 0)
            {
                await tab.ClickAsync(new() { Timeout = 5000 });
                await page.WaitForTimeoutAsync(1200);
            }
        }
        catch { /* ignore */ }

        try
        {
            await page.Locator("#totalVideoNum").First
                .ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        }
        catch { /* ignore */ }
    }

    private static async Task FillEpisodeCountAsync(
        IPage page,
        int episodeCount,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var value = Math.Max(1, episodeCount).ToString();
        var locator = page.Locator("#totalVideoNum").First;
        await locator.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        await locator.ClickAsync(new() { Timeout = 5000 });
        await locator.FillAsync(value);
        try
        {
            var actual = await locator.InputValueAsync();
            if (!string.Equals(actual, value, StringComparison.Ordinal))
            {
                await locator.PressAsync("Control+A");
                await locator.PressSequentiallyAsync(value, new() { Delay = 30 });
            }
        }
        catch { /* ignore */ }

        Log(log, $"TikTok 总集数已填写：{value}");
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

    private static async Task EnsureCommercialModeStepAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var tab = page.GetByText("商业模式", new() { Exact = false }).First;
            if (await tab.CountAsync() > 0)
            {
                await tab.ClickAsync(new() { Timeout = 5000 });
                await page.WaitForTimeoutAsync(1200);
            }
        }
        catch { /* ignore */ }

        try
        {
            await page.Locator("#business-mode-section, #previewVideoNum, #previewVideoNumOnProfile").First
                .ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        }
        catch { /* ignore */ }
    }

    private static async Task FillNumericFieldAsync(
        IPage page,
        string selector,
        int value,
        Action<string>? log,
        string fieldName,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = Math.Max(0, value).ToString();
        var locator = await ResolveVisibleInputAsync(page, selector, fieldName, ct);
        await locator.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        await locator.ClickAsync(new() { Timeout = 5000 });
        await locator.FillAsync(text);
        try
        {
            var actual = await locator.InputValueAsync();
            if (!string.Equals(actual?.Trim(), text, StringComparison.Ordinal))
            {
                await locator.PressAsync("Control+A");
                await locator.PressSequentiallyAsync(text, new() { Delay = 30 });
            }
        }
        catch { /* ignore */ }

        Log(log, $"TikTok {fieldName}已填写：{text}");
    }

    private static async Task<ILocator> ResolveVisibleInputAsync(
        IPage page,
        string selector,
        string fieldName,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var visible = page.Locator($"{selector}:visible").First;
            if (await IsReadyInputAsync(visible))
                return visible;

            var candidates = page.Locator(selector);
            var count = Math.Min(await candidates.CountAsync(), 12);
            for (var i = 0; i < count; i++)
            {
                var candidate = candidates.Nth(i);
                try
                {
                    if (await candidate.IsVisibleAsync(new() { Timeout = 500 }) &&
                        await candidate.IsEnabledAsync(new() { Timeout = 500 }))
                    {
                        return candidate;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            await page.WaitForTimeoutAsync(300);
        }

        throw new TimeoutException(
            $"TikTok 未找到可见的 {fieldName} 输入框（{selector}）。当前页面可能未切换到对应步骤，或 TikTok 表单结构已更新。",
            lastError);
    }

    private static async Task<bool> IsReadyInputAsync(ILocator locator)
    {
        try
        {
            return await locator.CountAsync() > 0 &&
                   await locator.IsVisibleAsync(new() { Timeout = 500 }) &&
                   await locator.IsEnabledAsync(new() { Timeout = 500 });
        }
        catch
        {
            return false;
        }
    }

    private static async Task ApplyCommercialModeAsync(
        IPage page,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        await EnsureCommercialModeStepAsync(page, ct);
        await PauseBetweenFieldsAsync(page);

        await FillNumericFieldAsync(
            page, "#previewVideoNumOnProfile", options.ProfilePreviewEpisodes, log, "个人页剧集展示集数", ct);
        await VerifyNumericFieldAsync(page, "#previewVideoNumOnProfile", options.ProfilePreviewEpisodes, "个人页剧集展示集数", log, ct);
        await PauseBetweenFieldsAsync(page);

        if (!options.PaidEnabled)
        {
            Log(log, $"商业模式已按付费=否填写个人页剧集展示集数：{options.ProfilePreviewEpisodes}");
            return;
        }

        await FillNumericFieldAsync(
            page, "#previewVideoNum", options.FreePreviewEpisodes, log, "免费预览集数", ct);
        await VerifyNumericFieldAsync(page, "#previewVideoNum", options.FreePreviewEpisodes, "免费预览集数", log, ct);
        await PauseBetweenFieldsAsync(page);

        if (options.PaidEnabled
            && options.ExpectedFullPriceMode == "manual"
            && string.IsNullOrWhiteSpace(options.ExpectedFullPriceValue))
        {
            throw new InvalidOperationException("是否付费=是 时，必须配置“预期全集价格设置”。");
        }

        await SelectExpectedFullPriceAsync(page, options, log, ct);
        await VerifyExpectedFullPriceAsync(page, options, log, ct);

        var label = options.ExpectedFullPriceMode == "option_index"
            ? $"第 {options.ExpectedFullPriceOptionIndex} 个价格选项"
            : options.ExpectedFullPriceLabel ?? options.ExpectedFullPriceValue;
        Log(log, options.PaidEnabled
            ? $"商业模式已按付费=是填写个人页剧集展示集数：{options.ProfilePreviewEpisodes}，免费预览集数：{options.FreePreviewEpisodes}，预期全集价格：{label}"
            : $"商业模式已按付费=否预填个人页剧集展示集数：{options.ProfilePreviewEpisodes}，免费预览集数：{options.FreePreviewEpisodes}，预期全集价格：{label}");
    }

    private static async Task ApplyZeroCostAdsAsync(
        IPage page,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var section = page.Locator("#ad-placement-section").First;
        if (await section.CountAsync() == 0 ||
            !await section.IsVisibleAsync())
        {
            Log(log, "TikTok 当前账号未开放“0 元投放”，已自动跳过。");
            return;
        }

        await section.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        var toggle = section.Locator("input[role='switch'][type='checkbox']").First;
        if (await toggle.CountAsync() == 0)
        {
            Log(log, "TikTok 当前账号未显示“0 元投放”开关，已自动跳过。");
            return;
        }

        if (!await toggle.IsEnabledAsync())
        {
            Log(log, "TikTok 当前账号的“0 元投放”暂不可配置，已自动跳过。");
            return;
        }

        var expectedEnabled = options.ZeroCostAdsEnabled;
        if (await toggle.IsCheckedAsync() != expectedEnabled)
        {
            await toggle.ClickAsync(new() { Force = true, Timeout = 5000 });
            await page.WaitForTimeoutAsync(400);
        }

        if (await toggle.IsCheckedAsync() != expectedEnabled)
            throw new InvalidOperationException("TikTok“0 元投放”开关设置后状态校验失败。");

        if (!expectedEnabled)
        {
            Log(log, "TikTok“0 元投放”已关闭，跳过第 0 天 ROI。");
            return;
        }

        var roiInput = await TryResolveDayZeroRoiInputAsync(page, ct);
        if (roiInput is null)
        {
            Log(log, "TikTok 当前账号未显示“第 0 天 ROI”输入框，已自动跳过。");
            return;
        }

        var normalizedRoi = TikTokPublishOptions.NormalizeDayZeroRoi(options.DayZeroRoi);
        var expectedText = normalizedRoi.ToString("0.00", CultureInfo.InvariantCulture);
        await roiInput.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        await roiInput.FillAsync(expectedText);
        await roiInput.PressAsync("Tab");

        var actualText = (await roiInput.InputValueAsync()).Trim();
        if (!double.TryParse(actualText, NumberStyles.Number, CultureInfo.InvariantCulture, out var actualRoi) ||
            Math.Abs(actualRoi - normalizedRoi) > 0.0001)
        {
            throw new InvalidOperationException(
                $"TikTok“第 0 天 ROI”设置后校验失败：期望 {expectedText}，实际 {actualText}。");
        }

        Log(log, $"TikTok“0 元投放”已开启，第 0 天 ROI：{expectedText}。");
    }

    private static async Task<ILocator?> TryResolveDayZeroRoiInputAsync(
        IPage page,
        CancellationToken ct)
    {
        const string selector =
            "#ad-placement-section [x-field-id='adsRoi'] input:visible, " +
            "#ad-placement-section input[placeholder='输入 (1.0~1.5)']:visible";
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var locator = page.Locator(selector).First;
            if (await IsReadyInputAsync(locator))
                return locator;
            await page.WaitForTimeoutAsync(200);
        }

        return null;
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

        var combo = await FindComboboxByFieldLabelAsync(page, ["题材类型", "题材"]);
        if (combo is null)
            throw new InvalidOperationException("未找到 TikTok「题材类型」下拉框，请确认已切换到剧集详情步骤。");
        await combo.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
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
        if (await IsMainPromiseCheckedAsync(page))
        {
            Log(log, "TikTok 本人承诺已勾选，跳过。");
            return;
        }

        var candidates = new[]
        {
            page.Locator("[x-field-id='signed'] label").First,
            page.Locator("[x-field-id='signed'] input[type='checkbox']").First,
            page.Locator("label").Filter(new() { HasText = "本人承诺" }).First,
            page.Locator("span").Filter(new() { HasText = "本人承诺" }).First,
            page.Locator("a").Filter(new() { HasText = "版权内容自查清单" }).First,
            page.Locator("span").Filter(new() { HasText = "版权内容自查清单" }).First,
        };

        for (var round = 0; round < 3; round++)
        {
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

                if (await HandlePromiseDrawerAsync(page, log, ct)
                    && await WaitForMainPromiseCheckedAsync(page, log, ct))
                    return;

                if (await IsMainPromiseCheckedAsync(page))
                {
                    Log(log, "已勾选本人承诺。");
                    return;
                }
            }

            if (await HandlePromiseDrawerAsync(page, log, ct)
                && await WaitForMainPromiseCheckedAsync(page, log, ct))
                return;
        }

        if (await IsMainPromiseCheckedAsync(page))
        {
            Log(log, "已勾选本人承诺。");
            return;
        }

        throw new InvalidOperationException("TikTok 本人承诺未能勾选成功（请检查版权内容自查清单是否已全部勾选并同意）。");
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

    /// <summary>上传/编辑开始前重置残留页面：上一轮失败可能停留在半填的表单，
    /// 且页面上挂着「是否离开网站」确认弹窗；此处选择「离开」丢弃残留更改，让流程从干净页面开始。</summary>
    public static async Task<bool> ResetLeftoverPageStateAsync(IPage page, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            string bodyText;
            try { bodyText = await page.Locator("body").InnerTextAsync(new() { Timeout = 2000 }); }
            catch { bodyText = ""; }

            if (!ContainsLeavePageDialogMarker(bodyText))
                return false;

            Log(log, "检测到上次残留的「是否离开网站」弹窗，准备丢弃旧表单更改。");
            foreach (var text in ConfirmLeavePageButtonTexts)
            {
                if (await TryClickVisibleButtonByTextAsync(page, text))
                {
                    Log(log, $"已自动点击「{text}」，离开旧表单页面。");
                    await page.WaitForTimeoutAsync(800);
                    return true;
                }
            }
        }
        catch
        {
            // 弹窗消失或页面切换中，忽略
        }

        return false;
    }

    private static bool ContainsLeavePageDialogMarker(string? text)
    {
        var value = text ?? "";
        return LeavePageDialogMarkers.Any(marker =>
            value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> TryClickVisibleButtonByTextAsync(IPage page, string text)
    {
        foreach (var locator in new[]
                 {
                     page.GetByRole(AriaRole.Button, new() { Name = text }).First,
                     page.Locator("button").Filter(new() { HasText = text }).First,
                     page.Locator("[role='button']").Filter(new() { HasText = text }).First,
                 })
        {
            try
            {
                if (await locator.CountAsync() == 0 || !await locator.IsVisibleAsync())
                    continue;

                await locator.ClickAsync(new() { Timeout = 3000 });
                return true;
            }
            catch
            {
                // 尝试下一个匹配方式
            }
        }

        return false;
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
        // Never fall back to a page-wide button lookup here. On an edit page that fallback can
        // click the main submit button a second time and incorrectly report it as a dialog
        // confirmation. Different TikTok deployments use either TUX or Semi modal containers,
        // so keep the selector broad enough for those implementations but always dialog-scoped.
        await page.WaitForTimeoutAsync(500);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var dialogs = page.Locator(
                "[role='dialog']:visible, " +
                "[aria-modal='true']:visible, " +
                "[data-testid='tux-web-modal']:visible, " +
                ".semi-modal:visible, " +
                ".semi-modal-content:visible");
            int dialogCount;
            try { dialogCount = await dialogs.CountAsync(); }
            catch { dialogCount = 0; }

            for (var dialogIndex = dialogCount - 1; dialogIndex >= 0; dialogIndex--)
            {
                var dialog = dialogs.Nth(dialogIndex);
                foreach (var text in new[] { "确认", "确定", "提交", "同意" })
                {
                    try
                    {
                        var modalBtn = dialog.Locator("button:visible")
                            .Filter(new() { HasText = text }).Last;
                        if (await modalBtn.CountAsync() == 0 || !await modalBtn.IsVisibleAsync())
                            continue;

                        await modalBtn.ClickAsync(new() { Timeout = 5000 });
                        Log(log, $"已确认提交对话框：{text}");
                        return;
                    }
                    catch { /* try next dialog/button */ }
                }
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        Log(log, "提交后未检测到确认对话框，继续执行提交结果校验。");
    }

    private static async Task VerifySubmitAcceptedAsync(
        IPage page,
        IReadOnlyList<string>? titleCandidates,
        Action<string>? log,
        CancellationToken ct)
    {
        // The confirm button click can succeed even when TikTok later keeps the item as a draft.
        // Wait briefly, then verify the list status before marking the local upload as complete.
        await page.WaitForTimeoutAsync(3000).ConfigureAwait(false);
        if (titleCandidates is null || titleCandidates.Count == 0)
        {
            Log(log, "未提供剧名，跳过提交后列表状态校验。");
            return;
        }

        var result = await TikTokEditFlowService.VerifySubmittedFromSeriesListAsync(
                page,
                titleCandidates,
                log,
                ct)
            .ConfigureAwait(false);

        if (!result.Accepted)
            throw new InvalidOperationException(result.Message);

        Log(log, result.Message);
    }

    private static async Task WaitSubmitEnabledAsync(
        ILocator button,
        Action<string>? log,
        CancellationToken ct,
        int timeoutSeconds = 300)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(30, timeoutSeconds));
        var nextLogAt = DateTime.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!await IsAriaDisabledAsync(button)) return;

            if (DateTime.UtcNow >= nextLogAt)
            {
                Log(log, "视频上传已完成，但 TikTok 提交按钮仍不可用，正在等待页面完成表单校验。");
                nextLogAt = DateTime.UtcNow.AddSeconds(30);
            }

            await Task.Delay(2000, ct);
        }
        throw new TimeoutException(
            $"视频上传已完成，但 TikTok 可见的提交按钮在 {Math.Max(30, timeoutSeconds)} 秒内仍不可用。" +
            "请检查页面必填项、平台提示或账号风控状态。");
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

internal sealed class TikTokDailyLimitException : InvalidOperationException
{
    public TikTokDailyLimitException(string limitText)
        : base($"TikTok 单日创建剧集上限：{limitText}")
    {
        LimitText = limitText;
    }

    public string LimitText { get; }
}
