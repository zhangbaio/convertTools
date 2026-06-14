using Microsoft.Playwright;
using ShortDrama.Core.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.Automation.Weixin.Pages;

public sealed class WeixinSeriesSubmissionPage
{
    private sealed record UploadFieldState(int FileCount, int PreviewCount, int SuccessHintCount, bool HasProcessingText, string Text);
    private sealed record UploadWaitProfile(int DeadlineSeconds, bool RequireProcessingToClear, bool RequireStableRounds, bool AcceptOnFileSelection, bool WaitForCompletion);
    private sealed record UploadMarkerProbe(string Text, string[] LinkTexts);
    private const int MaxProofMaterialUploadCount = 4;

    private sealed class FormGroupCache
    {
        private readonly Dictionary<string, ILocator> _groups = new(StringComparer.Ordinal);

        public bool TryGet(string key, out ILocator locator) => _groups.TryGetValue(key, out locator!);

        public void Set(string key, ILocator locator) => _groups[key] = locator;
    }

    private sealed record UploadRowStatusSummary(
        int MatchedRows,
        int SuccessRows,
        int ProcessingRows,
        IReadOnlyList<int> FailedRowIndexes);

    private static readonly IReadOnlyDictionary<string, string[]> PreferredFillSelectors = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["剧目名称"] = ["input[placeholder*='剧目名称']", "input[placeholder*='待提审剧目的名称']"],
        ["剧目简介"] = ["textarea[placeholder*='剧目简介']", "textarea[placeholder*='介绍相关剧情概要']"],
        ["推荐语"] = ["input[placeholder*='推荐语']", "textarea[placeholder*='推荐语']"],
        ["总集数"] = ["input[placeholder*='总集数']"],
        ["试看集数"] = ["input[placeholder*='试看集数']"],
        ["制作方名称"] = ["input[placeholder*='制作方名称']", "input[placeholder*='制作方主体名称']"]
    };

    private static readonly string[] PreferredChooseFields =
    [
        "变现类型",
        "剧目类型",
        "提审身份",
        "剧目资质"
    ];

    private static readonly Regex UploadSuccessSummaryRegex = new(@"已上传成功\s*(\d+)\s*/\s*(\d+)\s*集", RegexOptions.Compiled);
    private static readonly string[] KnownNavigationLabels =
    [
        "首页",
        "内容管理",
        "视频",
        "全部视频",
        "发表视频",
        "互动管理",
        "直播",
        "收入与服务",
        "收入权益",
        "原创保护记录",
        "加热工具",
        "带货中心",
        "剧集管理",
        "上架剧集"
    ];

    private static readonly string[] KnownAuxiliaryPageUrlParts =
    [
        "mp.weixin.qq.com/cgi-bin/announce",
        "action=getannouncement"
    ];

    private static readonly string[] KnownAuxiliaryPageTitleParts =
    [
        "微信公众平台",
        "服务使用须知"
    ];

    public async Task NavigateAsync(
        IPage page,
        WeixinNavigationOptions navigation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (await IsFirstPageReadyAsync(page, null, 800))
        {
            return;
        }

        if (await TryOpenDirectEntryAsync(page, cancellationToken))
        {
            return;
        }

        if (await TryEntryClickAsync(page, navigation.EntryButton, cancellationToken))
        {
            return;
        }

        var navigationSteps = new[]
        {
            navigation.Section,
            navigation.Item,
            "内容管理",
            "收入与服务",
            navigation.Item,
            "收入权益",
            navigation.Item,
            "视频",
            "全部视频",
            "内容管理",
            "收入与服务",
            navigation.Item
        };

        foreach (var candidate in navigationSteps)
        {
            var normalized = candidate?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!await HasVisibleTextAsync(page, normalized, false, 1_500))
            {
                continue;
            }

            if (!await MaybeClickTextAsync(page, normalized, false, 5_000))
            {
                continue;
            }

            await WaitBrieflyForLoadAsync(page);
            if (await TryEntryClickAsync(page, navigation.EntryButton, cancellationToken))
            {
                return;
            }
        }

        var visibleLabels = await CollectVisibleNavigationLabelsAsync(page);
        var visibleText = visibleLabels.Count == 0
            ? "无可识别入口"
            : string.Join(" / ", visibleLabels);
        throw new InvalidOperationException(
            $"未找到上架剧集入口，已尝试 section={navigation.Section}, item={navigation.Item}, entry={navigation.EntryButton}；当前页面可见入口: {visibleText}");
    }

    public async Task WaitForReadyAsync(
        IPage page,
        WeixinFirstPageOptions firstPage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var timeoutMs = 15_000;
        if (await IsFirstPageReadyAsync(page, firstPage, 1_200))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(firstPage.ReadyText))
        {
            await page.GetByText(firstPage.ReadyText, new PageGetByTextOptions
            {
                Exact = false
            }).WaitForAsync(new LocatorWaitForOptions
            {
                Timeout = timeoutMs
            });
            return;
        }

        foreach (var label in firstPage.ReadyLabels)
        {
            try
            {
                await FindGroupByLabelAsync(page, label, timeoutMs);
                return;
            }
            catch
            {
                // Continue to next label.
            }
        }

        await page.GetByText("剧目信息填写", new PageGetByTextOptions
        {
            Exact = false
        }).WaitForAsync(new LocatorWaitForOptions
        {
            Timeout = timeoutMs
        });
    }

    public async Task ExecuteFirstPageActionsAsync(
        IPage page,
        WeixinAutomationConfig config,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var cache = new FormGroupCache();
        var completedUploadActions = new List<WeixinFormAction>();
        var lastUploadCompletedAt = DateTimeOffset.MinValue;
        foreach (var action in OrderFirstPageActions(config.FirstPage.Actions))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var actionType = action.Type.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(actionType))
            {
                continue;
            }

            if (completedUploadActions.Count > 0 &&
                string.Equals(actionType, "set_checked", StringComparison.Ordinal) &&
                string.Equals(action.Label?.Trim(), "我已知悉并同意", StringComparison.Ordinal))
            {
                await WaitForFirstPageUploadsSettledAsync(page, completedUploadActions, lastUploadCompletedAt, progress, cancellationToken);
                completedUploadActions.Clear();
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var actionLabel = action.Label ?? action.FieldLabel ?? action.Selector ?? action.Text ?? actionType;
            progress?.Report($"[URL] before action={actionType} label={actionLabel} url={page.Url}");
            progress?.Report($"微信剧集上传：开始执行 {actionType} -> {actionLabel}");
            try
            {
                switch (actionType)
                {
                    case "fill":
                        await ExecuteFillAsync(page, action, progress, cancellationToken, cache);
                        break;
                    case "choose":
                        await ExecuteChooseAsync(page, action, progress, cancellationToken, cache);
                        break;
                    case "set_checked":
                        await ExecuteSetCheckedAsync(page, action, progress, cancellationToken);
                        break;
                    case "click":
                        await ExecuteClickAsync(page, action, progress, cancellationToken);
                        break;
                    case "upload":
                        await ExecuteUploadAsync(page, action, progress, cancellationToken, cache);
                        break;
                    case "screenshot":
                        await ExecuteScreenshotAsync(page, config.OutputDirectory, action, progress, cancellationToken);
                        break;
                    default:
                        progress?.Report($"微信剧集上传：暂未接入第一页动作 {action.Type}，已跳过。");
                        break;
                }
            }
            catch
            {
                await SaveFirstPageActionFailureArtifactsAsync(page, config.OutputDirectory, actionType, actionLabel, cancellationToken);
                throw;
            }

            if (string.Equals(actionType, "upload", StringComparison.Ordinal))
            {
                completedUploadActions.Add(action);
                lastUploadCompletedAt = DateTimeOffset.UtcNow;
            }

            sw.Stop();
            progress?.Report($"[URL] after action={actionType} label={actionLabel} url={page.Url}");
            await CloseAuxiliaryNoticePagesAsync(page, progress, cancellationToken);
            await ThrowIfRedirectedToPlatformHomeAsync(page, "第一页表单填写", cancellationToken);
            progress?.Report($"[TIMING] action={actionType} label={actionLabel} elapsed={sw.ElapsedMilliseconds}ms");
        }

        if (completedUploadActions.Count > 0)
        {
            await WaitForFirstPageUploadsSettledAsync(page, completedUploadActions, lastUploadCompletedAt, progress, cancellationToken);
            await ThrowIfRedirectedToPlatformHomeAsync(page, "第一页上传等待", cancellationToken);
        }
    }

    private static IEnumerable<WeixinFormAction> OrderFirstPageActions(IReadOnlyList<WeixinFormAction> actions)
    {
        return actions
            .Select((action, index) => new
            {
                Action = action,
                Index = index,
                Priority = GetFirstPageActionPriority(action)
            })
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.Index)
            .Select(item => item.Action);
    }

    private static int GetFirstPageActionPriority(WeixinFormAction action)
    {
        var type = action.Type?.Trim().ToLowerInvariant();
        if (string.Equals(type, "screenshot", StringComparison.Ordinal))
        {
            return 30;
        }

        if (string.Equals(type, "upload", StringComparison.Ordinal))
        {
            return 20;
        }

        if (string.Equals(type, "set_checked", StringComparison.Ordinal) &&
            string.Equals(action.Label?.Trim(), "我已知悉并同意", StringComparison.Ordinal))
        {
            return 25;
        }

        return 10;
    }

    public async Task MoveToSecondPageAsync(
        IPage page,
        WeixinFirstPageOptions firstPage,
        WeixinSecondPageOptions secondPage,
        string outputDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var buttonText = string.IsNullOrWhiteSpace(firstPage.NextButtonText)
            ? "下一步"
            : firstPage.NextButtonText.Trim();
        var consentHandledOnFirstPage = firstPage.Actions.Any(action =>
            string.Equals(action.Type?.Trim(), "set_checked", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(action.Label?.Trim(), "我已知悉并同意", StringComparison.Ordinal));
        progress?.Report($"微信剧集上传：点击 {buttonText}，进入第二页...");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(75);
        Exception? lastError = null;
        var consentPrepared = consentHandledOnFirstPage;
        var nextEnabled = false;
        var lastConsentAttemptAt = DateTimeOffset.MinValue;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CloseAuxiliaryNoticePagesAsync(page, progress, cancellationToken);
            await ThrowIfRedirectedToPlatformHomeAsync(page, "进入第二页", cancellationToken);
            var attemptedConsentThisRound = false;

            if (await IsSecondPageReadyAsync(page, secondPage, 1_500))
            {
                progress?.Report("微信剧集上传：已切换到第二页。");
                return;
            }

            nextEnabled = await HasVisibleEnabledButtonAsync(page, buttonText, 400);
            var shouldAttemptConsent =
                !consentHandledOnFirstPage &&
                (!consentPrepared ||
                 (!nextEnabled &&
                  DateTimeOffset.UtcNow - lastConsentAttemptAt >= TimeSpan.FromSeconds(2)));

            if (shouldAttemptConsent)
            {
                attemptedConsentThisRound = true;
                lastConsentAttemptAt = DateTimeOffset.UtcNow;
                var consentState = await EnsureConsentCheckboxStateAsync(page, null, "我已知悉并同意", true, cancellationToken);
                nextEnabled = await HasVisibleEnabledButtonAsync(page, buttonText, 800);
                if (consentState == true || nextEnabled)
                {
                    consentPrepared = true;
                }
                else
                {
                    progress?.Report("微信剧集上传：检测到“我已知悉并同意”未勾选，已在进入第二页前重试勾选。");
                }
            }

            if (attemptedConsentThisRound)
            {
                await Task.Delay(400, cancellationToken);
                continue;
            }

            if (!nextEnabled)
            {
                if (consentHandledOnFirstPage)
                {
                    await CloseAuxiliaryNoticePagesAsync(page, progress, cancellationToken);
                }
                await Task.Delay(300, cancellationToken);
                continue;
            }

            try
            {
                await SaveNextStepDiagnosticsAsync(page, outputDirectory, buttonText, "before-next-click", cancellationToken);
                var nextButton = await ResolveNextButtonAsync(page, buttonText, 5_000);
                await ScrollIntoViewIfNeededSafeAsync(nextButton);
                await nextButton.ClickAsync(new LocatorClickOptions
                {
                    Timeout = 5_000
                });
                await WaitBrieflyForLoadAsync(page);
                await CloseAuxiliaryNoticePagesAsync(page, progress, cancellationToken);

                if (!await IsSecondPageReadyAsync(page, secondPage, 1_500))
                {
                    try
                    {
                        await nextButton.EvaluateAsync("button => button instanceof HTMLElement && button.click()");
                        await WaitBrieflyForLoadAsync(page);
                        await CloseAuxiliaryNoticePagesAsync(page, progress, cancellationToken);
                    }
                    catch
                    {
                        // Ignore DOM click fallback failures and continue with existing checks.
                    }
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            if (await TryHandleFirstPageValidationDialogAsync(page, progress, cancellationToken))
            {
                await SaveNextStepDiagnosticsAsync(page, outputDirectory, buttonText, "validation-dialog", cancellationToken);
                lastError = null;
                consentPrepared = false;
                await Task.Delay(2_000, cancellationToken);
                continue;
            }

            if (await IsSecondPageReadyAsync(page, secondPage, 2_000))
            {
                progress?.Report("微信剧集上传：已切换到第二页。");
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"微信剧集上传：点击 {buttonText} 后未进入第二页。{lastError?.Message}");
    }

    private static async Task<bool> TryHandleFirstPageValidationDialogAsync(
        IPage page,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var validationMessages = new[]
        {
            "请上传《成本配置比例情况报告》",
            "请勾选同意",
            "请先勾选",
            "请阅读并同意",
            "请同意"
        };

        var topTipsCandidates = new List<ILocator>();
        foreach (var message in validationMessages)
        {
            var safe = EscapeSelectorText(message);
            topTipsCandidates.Add(page.Locator(".weui-toptips_error").GetByText(message, new LocatorGetByTextOptions { Exact = false }).First);
            topTipsCandidates.Add(page.Locator(".weui-toptips").GetByText(message, new LocatorGetByTextOptions { Exact = false }).First);
            topTipsCandidates.Add(page.Locator($".weui-toptips_error:has-text(\"{safe}\")").First);
            topTipsCandidates.Add(page.Locator($".weui-toptips:has-text(\"{safe}\")").First);
        }

        try
        {
            var topTip = await WaitForFirstCandidateAsync(
                topTipsCandidates,
                1_200,
                WaitForSelectorState.Visible,
                "not found",
                scrollOnSuccess: false);

            if (topTip is not null)
            {
                progress?.Report("微信剧集上传：检测到第一页校验提示，继续等待材料处理完成后重试下一步。");
                return true;
            }
        }
        catch
        {
            // Fall through to dialog/body checks below.
        }

        var dialogTextCandidates = new List<ILocator>();
        foreach (var message in validationMessages)
        {
            dialogTextCandidates.Add(page.GetByText(message, new PageGetByTextOptions { Exact = false }).First);
        }

        dialogTextCandidates.Add(page.GetByText("修改信息确认", new PageGetByTextOptions { Exact = false }).First);

        ILocator? dialogText = null;
        try
        {
            dialogText = await WaitForFirstCandidateAsync(
                dialogTextCandidates,
                500,
                WaitForSelectorState.Visible,
                "not found",
                scrollOnSuccess: false);
        }
        catch
        {
            return false;
        }

        progress?.Report("微信剧集上传：检测到第一页校验提示，继续等待材料处理完成后重试下一步。");

        var dialog = await ResolveValidationDialogContainerAsync(dialogText);
        var closeTargets = new List<ILocator>();
        if (dialog is not null)
        {
            closeTargets.Add(dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "确认修改", Exact = false }).First);
            closeTargets.Add(dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "返回", Exact = false }).First);
            closeTargets.Add(dialog.GetByText("确认修改", new LocatorGetByTextOptions { Exact = false }).First);
            closeTargets.Add(dialog.GetByText("返回", new LocatorGetByTextOptions { Exact = false }).First);
        }

        closeTargets.Add(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = "确认修改", Exact = false }).First);
        closeTargets.Add(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = "返回", Exact = false }).First);
        closeTargets.Add(page.GetByText("确认修改", new PageGetByTextOptions { Exact = false }).First);
        closeTargets.Add(page.GetByText("返回", new PageGetByTextOptions { Exact = false }).First);

        foreach (var target in closeTargets)
        {
            try
            {
                await target.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 300
                });
                await target.ClickAsync(new LocatorClickOptions
                {
                    Timeout = 1_000
                });
                await WaitBrieflyForLoadAsync(page);
                return true;
            }
            catch
            {
                // Try next close target.
            }
        }

        try
        {
            var hasValidationText = await page.EvaluateAsync<bool>(
                @"() => (document.body?.innerText || '').includes('请上传《成本配置比例情况报告》')");
            if (hasValidationText)
            {
                progress?.Report("微信剧集上传：检测到第一页校验提示，继续等待材料处理完成后重试下一步。");
                return true;
            }
        }
        catch
        {
            // Ignore and fall through.
        }

        return true;
    }

    private static async Task<ILocator?> ResolveValidationDialogContainerAsync(ILocator anchor)
    {
        var candidates = new[]
        {
            anchor.Locator("xpath=ancestor::*[@role='dialog'][1]").First,
            anchor.Locator("xpath=ancestor::*[contains(@class,'dialog') or contains(@class,'modal')][1]").First
        };

        foreach (var candidate in candidates)
        {
            try
            {
                await candidate.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = 200
                });
                return candidate;
            }
            catch
            {
                // Try next candidate.
            }
        }

        return null;
    }

    public async Task WaitForSecondPageReadyAsync(
        IPage page,
        WeixinSecondPageOptions secondPage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var timeoutMs = 30_000;
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ThrowIfRedirectedToPlatformHomeAsync(page, "等待第二页就绪", cancellationToken);
            if (await IsSecondPageReadyAsync(page, secondPage, 2_000))
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("微信剧集上传：等待第二页就绪超时。");
    }

    private static async Task ThrowIfRedirectedToPlatformHomeAsync(
        IPage page,
        string stage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var url = page.Url ?? string.Empty;
        if (!url.Contains("/platform", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/platform/native-drama-post", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var homepageMarkers = new[]
        {
            "昨日数据",
            "最近视频",
            "最近图文",
            "视频号 · 助手",
            "作品优化建议"
        };

        var markerCount = 0;
        foreach (var text in homepageMarkers)
        {
            if (await HasVisibleTextAsync(page, text, false, 250))
            {
                markerCount++;
            }
        }

        if (markerCount >= 2)
        {
            throw new InvalidOperationException($"微信剧集上传：{stage}过程中页面被重定向回首页（{url}）。");
        }
    }

    private static async Task SaveNextStepDiagnosticsAsync(
        IPage page,
        string outputDirectory,
        string buttonText,
        string stage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(outputDirectory);
        var stem = $"series-{SanitizeFileName(stage)}";

        try
        {
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(outputDirectory, $"{stem}.png"),
                FullPage = true
            });
        }
        catch
        {
            // Ignore screenshot failures.
        }

        try
        {
            var root = await ResolveDebugRootAsync(page);
            var html = await root.EvaluateAsync<string>("node => node.outerHTML ?? ''");
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, $"{stem}.html"), html, cancellationToken);
        }
        catch
        {
            // Ignore html failures.
        }

        try
        {
            var root = await ResolveDebugRootAsync(page);
            var text = await root.EvaluateAsync<string>("node => node.innerText ?? ''");
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, $"{stem}.txt"), text, cancellationToken);
        }
        catch
        {
            // Ignore text failures.
        }

        try
        {
            var report = await BuildNextStepDiagnosticsReportAsync(page, buttonText, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, $"{stem}.json"), report, cancellationToken);
        }
        catch
        {
            // Ignore diagnostics report failures.
        }
    }

    private static async Task<string> BuildNextStepDiagnosticsReportAsync(
        IPage page,
        string buttonText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var nextButton = await TryResolveVisibleNextButtonAsync(page, buttonText, 500);
        var nextButtonEnabled = nextButton is not null && await nextButton.IsEnabledAsync();
        var nextButtonDisabled = nextButton is not null && await nextButton.IsDisabledAsync();
        string? nextButtonAriaDisabled = null;
        string? nextButtonClass = null;
        string? nextButtonText = null;

        if (nextButton is not null)
        {
            try
            {
                nextButtonAriaDisabled = await nextButton.GetAttributeAsync("aria-disabled");
                nextButtonClass = await nextButton.GetAttributeAsync("class");
                nextButtonText = (await nextButton.InnerTextAsync()).Trim();
            }
            catch
            {
                // Ignore next button attribute failures.
            }
        }

        var consentSummary = await ReadConsentCheckboxDiagnosticsAsync(page, cancellationToken);
        var topTips = await ReadVisibleTopTipsAsync(page, cancellationToken);

        var report = new
        {
            url = page.Url,
            nextButton = new
            {
                text = nextButtonText,
                enabled = nextButtonEnabled,
                disabled = nextButtonDisabled,
                ariaDisabled = nextButtonAriaDisabled,
                @class = nextButtonClass
            },
            consent = consentSummary,
            topTips
        };

        return JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static async Task<ILocator?> TryResolveVisibleNextButtonAsync(IPage page, string buttonText, int timeoutMs)
    {
        try
        {
            return await ResolveNextButtonAsync(page, buttonText, timeoutMs);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<object> ReadConsentCheckboxDiagnosticsAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var locators = new ILocator[]
        {
            page.Locator(".form_footer label.weui-desktop-form__check-label input.weui-desktop-form__checkbox[type='checkbox']").First,
            page.Locator(".form_footer label.weui-desktop-form__check-label").First,
            page.Locator(".form_footer .weui-desktop-icon-checkbox").First
        };

        foreach (var locator in locators)
        {
            try
            {
                await locator.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 300
                });

                var tagName = await locator.EvaluateAsync<string>("node => node.tagName.toLowerCase()");
                var text = string.Empty;
                try
                {
                    text = (await locator.InnerTextAsync()).Trim();
                }
                catch
                {
                    // Ignore text failures.
                }

                var checkedValue = string.Empty;
                var ariaChecked = await locator.GetAttributeAsync("aria-checked");
                var @class = await locator.GetAttributeAsync("class");
                if (string.Equals(tagName, "input", StringComparison.Ordinal))
                {
                    checkedValue = await locator.EvaluateAsync<bool>("node => node instanceof HTMLInputElement && node.checked")
                        ? "true"
                        : "false";
                }

                return new
                {
                    tag = tagName,
                    text,
                    @checked = checkedValue,
                    ariaChecked,
                    @class
                };
            }
            catch
            {
                // Try next consent locator.
            }
        }

        return new
        {
            tag = string.Empty,
            text = string.Empty,
            @checked = string.Empty,
            ariaChecked = string.Empty,
            @class = string.Empty
        };
    }

    private static async Task<IReadOnlyList<string>> ReadVisibleTopTipsAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var selectors = new[]
        {
            ".weui-toptips",
            ".weui-toptips_error",
            ".weui-desktop-tips",
            ".weui-desktop-msg",
            ".ant-message",
            ".ant-notification"
        };

        var tips = new List<string>();
        foreach (var selector in selectors)
        {
            try
            {
                var locator = page.Locator(selector);
                var count = await locator.CountAsync();
                var limit = Math.Max(0, Math.Min(count, 6));
                for (var index = 0; index < limit; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = locator.Nth(index);
                    try
                    {
                        if (!await item.IsVisibleAsync())
                        {
                            continue;
                        }

                        var text = (await item.InnerTextAsync()).Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            tips.Add(text);
                        }
                    }
                    catch
                    {
                        // Ignore single tip failures.
                    }
                }
            }
            catch
            {
                // Ignore selector failures.
            }
        }

        return tips.Distinct(StringComparer.Ordinal).ToArray();
    }

    public async Task ExecuteSecondPageActionsBeforeUploadAsync(
        IPage page,
        WeixinAutomationConfig config,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var action in config.SecondPage.ActionsBeforeUpload)
        {
            await ExecuteGenericActionAsync(page, config, action, progress, cancellationToken);
        }
    }

    public async Task UploadSecondPageVideosAsync(
        IPage page,
        WeixinAutomationConfig config,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var queue = config.SecondPage.UploadQueue;
        var queueItems = queue.Items
            .Where(item => item.Enabled)
            .Select(item => new
            {
                item.Path,
                FullPath = File.Exists(item.Path) ? Path.GetFullPath(item.Path) : string.Empty
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.FullPath))
            .ToArray();
        if (queueItems.Length == 0)
        {
            throw new InvalidOperationException("第二页上传配置未找到可用视频文件。");
        }

        if (!await IsSecondPageReadyAsync(page, config.SecondPage, 3_000))
        {
            throw new InvalidOperationException("第二页尚未真正就绪，已阻止视频上传。");
        }

        var input = page.Locator(string.IsNullOrWhiteSpace(queue.Selector) ? config.SecondPage.Upload.InputSelector : queue.Selector).First;
        await input.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 10_000
        });

        var inputGroup = input.Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__control-group') or contains(@class,'weui-desktop-form__control-group_label-r') or contains(@class,'weui-desktop-uploader') or contains(@class,'upload')][1]").First;
        try
        {
            await inputGroup.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 5_000
            });
        }
        catch
        {
            throw new InvalidOperationException("第二页视频上传区域未显示，已阻止视频上传。");
        }

        var mode = string.IsNullOrWhiteSpace(queue.Mode) ? "batch" : queue.Mode.Trim().ToLowerInvariant();
        if (string.Equals(mode, "sequential", StringComparison.Ordinal))
        {
            progress?.Report($"微信剧集上传：按顺序上传第二页视频，共 {queueItems.Length} 个文件...");
            for (var index = 0; index < queueItems.Length; index++)
            {
                var item = queueItems[index];
                progress?.Report($"微信剧集上传：开始上传第 {index + 1}/{queueItems.Length} 个视频 {Path.GetFileName(item.FullPath)}...");
                await input.SetInputFilesAsync(item.FullPath);

                try
                {
                    await WaitForUploadCompletionAsync(page, queue, [item.FullPath], progress, cancellationToken);
                }
                catch (TimeoutException) when (string.Equals(queue.OnItemTimeout, "skip", StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report($"微信剧集上传：第 {index + 1} 个视频上传超时，按配置跳过。");
                }
                catch (InvalidOperationException) when (string.Equals(queue.OnItemError, "skip", StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report($"微信剧集上传：第 {index + 1} 个视频上传失败，按配置跳过。");
                }
            }

            return;
        }

        var videoPaths = queueItems.Select(item => item.FullPath).ToArray();
        progress?.Report($"微信剧集上传：开始上传第二页视频，共 {videoPaths.Length} 个文件...");
        await input.SetInputFilesAsync(videoPaths);
        await WaitForUploadCompletionAsync(page, queue, videoPaths, progress, cancellationToken);
    }

    public async Task ExecuteSecondPageActionsAfterUploadAsync(
        IPage page,
        WeixinAutomationConfig config,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var action in config.SecondPage.ActionsAfterUpload)
        {
            await ExecuteGenericActionAsync(page, config, action, progress, cancellationToken);
        }
    }

    public async Task EnterSubmitPageAsync(
        IPage page,
        WeixinSecondPageOptions secondPage,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!secondPage.EnterSubmitPage.Enabled)
        {
            progress?.Report("微信剧集上传：配置已禁用自动进入提审页。");
            return;
        }

        var text = string.IsNullOrWhiteSpace(secondPage.EnterSubmitPage.Text)
            ? "确认提审"
            : secondPage.EnterSubmitPage.Text.Trim();
        progress?.Report($"微信剧集上传：正在进入提审页，按钮 {text}...");
        var button = await FirstVisibleAsync(page, text, false, 15_000);
        await button.ClickAsync();
        await WaitBrieflyForLoadAsync(page);

        if (!string.IsNullOrWhiteSpace(secondPage.EnterSubmitPage.WaitText))
        {
            await WaitForVisibleCandidateAsync(
                page.GetByText(secondPage.EnterSubmitPage.WaitText, new PageGetByTextOptions
                {
                    Exact = false
                }),
                10_000);
        }
    }

    public async Task WaitForSubmitPageReadyAsync(
        IPage page,
        WeixinSubmitOptions submit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var timeoutMs = 30_000;
        var buttonText = string.IsNullOrWhiteSpace(submit.Text)
            ? "确认提审"
            : submit.Text.Trim();

        var candidates = new List<ILocator>();
        if (!string.IsNullOrWhiteSpace(submit.ReadyText))
        {
            candidates.Add(page.GetByText(submit.ReadyText, new PageGetByTextOptions { Exact = false }).First);
        }

        candidates.Add(page.GetByText("确认提审", new PageGetByTextOptions { Exact = false }).First);
        candidates.Add(page.GetByText("提交", new PageGetByTextOptions { Exact = false }).First);
        candidates.Add(page.GetByText(buttonText, new PageGetByTextOptions { Exact = false }).First);

        await FindFirstVisibleAsync(candidates, timeoutMs);
    }

    public async Task ExecuteFinalSubmitAsync(
        IPage page,
        WeixinSubmitOptions submit,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!submit.Enabled)
        {
            progress?.Report("微信剧集上传：最终提交已交由人工确认。");
            return;
        }

        var text = string.IsNullOrWhiteSpace(submit.Text)
            ? "确认提审"
            : submit.Text.Trim();
        progress?.Report($"微信剧集上传：正在执行最终提交 {text}...");
        var button = await FirstVisibleAsync(page, text, false, 15_000);
        await button.ClickAsync();
        await WaitBrieflyForLoadAsync(page);
    }

    private static async Task<bool> TryEntryClickAsync(
        IPage page,
        string entryButton,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await HasVisibleTextAsync(page, entryButton, false, 1_500))
        {
            return false;
        }

        await ClickTextAsync(page, entryButton, false, 10_000);
        await WaitBrieflyForLoadAsync(page);
        return true;
    }

    private static async Task<bool> TryOpenDirectEntryAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var currentUrl = page.Url;
            var origin = string.IsNullOrWhiteSpace(currentUrl)
                ? "https://channels.weixin.qq.com"
                : new Uri(currentUrl).GetLeftPart(UriPartial.Authority);
            await page.GotoAsync($"{origin}/platform/native-drama-post", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 15_000
            });
            await WaitBrieflyForLoadAsync(page);

            var url = page.Url ?? string.Empty;
            return url.Contains("/platform/native-drama-post", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<IReadOnlyList<string>> CollectVisibleNavigationLabelsAsync(IPage page)
    {
        var visible = new List<string>();
        foreach (var label in KnownNavigationLabels)
        {
            if (await HasVisibleTextAsync(page, label, false, 800))
            {
                visible.Add(label);
            }
        }

        return visible;
    }

    private static Task<ILocator> FindGroupByLabelAsync(IPage page, string label, int timeoutMs)
    {
        var candidates = EnumerateGroupCandidates(page, label).ToList();
        return WaitForFirstCandidateAsync(
            candidates,
            timeoutMs,
            WaitForSelectorState.Visible,
            $"未找到字段: {label}",
            scrollOnSuccess: true);
    }

    private static async Task<ILocator> FindGroupByLabelCachedAsync(
        IPage page,
        string cacheKey,
        string label,
        int timeoutMs,
        FormGroupCache? cache)
    {
        if (cache is not null &&
            cache.TryGet(cacheKey, out var cached))
        {
            try
            {
                await cached.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 300
                });
                return cached;
            }
            catch
            {
                // Fall through and rebuild cache.
            }
        }

        var group = await FindGroupByLabelAsync(page, label, timeoutMs);
        cache?.Set(cacheKey, group);
        return group;
    }

    private static IEnumerable<ILocator> EnumerateGroupCandidates(IPage page, string label)
    {
        var safe = EscapeSelectorText(label);
        yield return page.Locator($".weui-desktop-form__item:has(.weui-desktop-form__label:text-is(\"{safe}\"))").First;
        yield return page.Locator($".weui-desktop-form__item:has(.weui-desktop-form__label:has-text(\"{safe}\"))").First;
        yield return page.Locator($".weui-desktop-form__label:text-is(\"{safe}\")")
            .Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__control-group')][1]")
            .First;
        yield return page.Locator($".weui-desktop-form__label:text-is(\"{safe}\")")
            .Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__item')][1]")
            .First;
        yield return page.Locator($".weui-desktop-form__control-group:has(.weui-desktop-form__label:text-is(\"{safe}\"))").First;
        yield return page.Locator($".weui-desktop-form__item:has-text(\"{safe}\")").First;
        yield return page.Locator($".weui-desktop-form__control-group:has-text(\"{safe}\")").First;
        yield return page.Locator($".weui-desktop-form__label:has-text(\"{safe}\")").First;
        yield return page.GetByLabel(label, new PageGetByLabelOptions { Exact = false }).First;
    }

    private static async Task ExecuteFillAsync(
        IPage page,
        WeixinFormAction action,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        FormGroupCache? cache = null)
    {
        var value = action.Value?.Trim() ?? string.Empty;
        var target = await ResolveEditableLocatorAsync(page, action, cancellationToken, cache);
        await ScrollIntoViewIfNeededSafeAsync(target);
        if (RequiresInteractiveFill(action))
        {
            await FillEditableInteractivelyAsync(target, value, cancellationToken);
        }
        else
        {
            await FillEditableAsync(target, value, cancellationToken);
        }

        progress?.Report($"微信剧集上传：已填写 {action.Label ?? action.Selector ?? "字段"}");
    }

    private static async Task ExecuteChooseAsync(
        IPage page,
        WeixinFormAction action,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        FormGroupCache? cache = null)
    {
        var fieldLabel = action.FieldLabel?.Trim();
        var optionText = action.OptionText?.Trim();
        if (string.IsNullOrWhiteSpace(fieldLabel) || string.IsNullOrWhiteSpace(optionText))
        {
            return;
        }

        var directPageOption = await ResolvePreferredChoiceLocatorAsync(page, fieldLabel, optionText, cancellationToken);
        if (directPageOption is not null)
        {
            await ScrollIntoViewIfNeededSafeAsync(directPageOption);
            await directPageOption.ClickAsync();
            progress?.Report($"微信剧集上传：已选择 {fieldLabel} -> {optionText}");
            await WaitBrieflyForLoadAsync(page);
            return;
        }

        var (groupTimeoutMs, directOptionTimeoutMs, dropdownOptionTimeoutMs) = GetChooseTimeouts(fieldLabel);

        var group = await FindChooseGroupByLabelAsync(page, fieldLabel, groupTimeoutMs, cache);
        var directOption = await TryFindChoiceOptionWithinAsync(group, optionText, directOptionTimeoutMs);
        if (directOption is not null)
        {
            await ScrollIntoViewIfNeededSafeAsync(directOption);
            await directOption.ClickAsync();
            progress?.Report($"微信剧集上传：已选择 {fieldLabel} -> {optionText}");
            await WaitBrieflyForLoadAsync(page);
            return;
        }

        var dropdownTrigger = await FindFirstVisibleAsync(
            [
                group.Locator(".weui-desktop-form__dropdown, .weui-desktop-dropdown, .weui-desktop-select").First,
                group.GetByRole(AriaRole.Button).First
            ],
            800);
        await ScrollIntoViewIfNeededSafeAsync(dropdownTrigger);
        await dropdownTrigger.ClickAsync();

        var option = await FirstVisibleAsync(page, optionText, false, dropdownOptionTimeoutMs);
        await ScrollIntoViewIfNeededSafeAsync(option);
        await option.ClickAsync();
        progress?.Report($"微信剧集上传：已选择 {fieldLabel} -> {optionText}");
        await WaitBrieflyForLoadAsync(page);
    }

    private static async Task<ILocator?> ResolvePreferredChoiceLocatorAsync(
        IPage page,
        string fieldLabel,
        string optionText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsPreferredChooseField(fieldLabel))
        {
            return null;
        }

        var safeField = EscapeSelectorText(fieldLabel);
        var safeOption = EscapeSelectorText(optionText);
        var containerSelectors = new[]
        {
            $".weui-desktop-form__item:has(.weui-desktop-form__label:has-text(\"{safeField}\"))",
            $".weui-desktop-form__control-group:has(.weui-desktop-form__label:has-text(\"{safeField}\"))",
            $".weui-desktop-form__control-group_label-t:has(.weui-desktop-form__label:has-text(\"{safeField}\"))"
        };

        var candidates = new List<ILocator>();
        foreach (var containerSelector in containerSelectors)
        {
            candidates.Add(page.Locator($"{containerSelector} label:has(.weui-desktop-form__check-content:has-text(\"{safeOption}\"))").First);
            candidates.Add(page.Locator($"{containerSelector} .weui-desktop-form__check-content:has-text(\"{safeOption}\")").First);
            candidates.Add(page.Locator($"{containerSelector} label:has-text(\"{safeOption}\")").First);
            candidates.Add(page.Locator($"{containerSelector} [role='radio']:has-text(\"{safeOption}\")").First);
            candidates.Add(page.Locator($"{containerSelector} [role='checkbox']:has-text(\"{safeOption}\")").First);
        }

        try
        {
            return await WaitForFirstCandidateAsync(
                candidates,
                200,
                WaitForSelectorState.Visible,
                "not found",
                scrollOnSuccess: true);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ILocator?> TryFindChoiceOptionWithinAsync(ILocator scope, string text, int timeoutMs)
    {
        var safe = EscapeSelectorText(text);
        var locators = new ILocator[]
        {
            scope.Locator($"label:has(.weui-desktop-form__check-content:has-text(\"{safe}\"))"),
            scope.Locator($".weui-desktop-form__check-content:has-text(\"{safe}\")"),
            scope.GetByRole(AriaRole.Radio, new LocatorGetByRoleOptions { NameString = text, Exact = false }),
            scope.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { NameString = text, Exact = false }),
            scope.Locator($"[role='option']:has-text(\"{safe}\")"),
            scope.Locator($".weui-desktop-dropdown__menu-item:has-text(\"{safe}\"), .weui-desktop-select__option:has-text(\"{safe}\")")
        };

        try
        {
            return await WaitForFirstCandidateAsync(
                locators,
                timeoutMs,
                WaitForSelectorState.Visible,
                "not found",
                scrollOnSuccess: true);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ILocator> FindChooseGroupByLabelAsync(
        IPage page,
        string fieldLabel,
        int timeoutMs,
        FormGroupCache? cache = null)
    {
        var cacheKey = $"choose:{fieldLabel}";
        if (cache is not null &&
            cache.TryGet(cacheKey, out var cached))
        {
            try
            {
                await cached.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 300
                });
                return cached;
            }
            catch
            {
                // Re-resolve below.
            }
        }

        var preferredGroup = await ResolvePreferredChooseGroupAsync(page, fieldLabel);
        if (preferredGroup is not null)
        {
            cache?.Set(cacheKey, preferredGroup);
            return preferredGroup;
        }

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        const int GroupQuickPollMs = 200;
        foreach (var group in EnumerateGroupCandidates(page, fieldLabel))
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                var remainingMs = (int)(deadline - DateTimeOffset.UtcNow).TotalMilliseconds;
                if (remainingMs <= 0)
                {
                    break;
                }

                try
                {
                    await group.WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Attached,
                        Timeout = Math.Min(GroupQuickPollMs, remainingMs)
                    });

                    if (await group.Locator("input[type='file']").CountAsync() > 0)
                    {
                        break;
                    }

                    var hasDropdown = await group.Locator(".weui-desktop-form__dropdown, .weui-desktop-dropdown, .weui-desktop-select").CountAsync() > 0;
                    var hasRadio = await group.Locator("input[type='radio'], .weui-desktop-radio").CountAsync() > 0;
                    var hasCheckbox = await group.Locator("input[type='checkbox'], .weui-desktop-checkbox").CountAsync() > 0;
                    if (!hasDropdown && !hasRadio && !hasCheckbox)
                    {
                        break;
                    }

                    await ScrollIntoViewIfNeededSafeAsync(group);
                    cache?.Set(cacheKey, group);
                    return group;
                }
                catch
                {
                    // Try the same candidate again until the global deadline, then move on.
                }
            }
        }

        return await FindGroupByLabelCachedAsync(page, cacheKey, fieldLabel, timeoutMs, cache);
    }

    private static async Task<ILocator?> ResolvePreferredChooseGroupAsync(IPage page, string fieldLabel)
    {
        if (!IsPreferredChooseField(fieldLabel))
        {
            return null;
        }

        var candidates = EnumerateGroupCandidates(page, fieldLabel).ToList();
        try
        {
            var group = await WaitForFirstCandidateAsync(
                candidates,
                500,
                WaitForSelectorState.Attached,
                "not found",
                scrollOnSuccess: false);

            await ScrollIntoViewIfNeededSafeAsync(group);
            return group;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPreferredChooseField(string fieldLabel)
    {
        return PreferredChooseFields.Contains(fieldLabel, StringComparer.Ordinal);
    }

    private static (int GroupTimeoutMs, int DirectOptionTimeoutMs, int DropdownOptionTimeoutMs) GetChooseTimeouts(string fieldLabel)
    {
        if (!IsPreferredChooseField(fieldLabel))
        {
            return (4_000, 800, 4_000);
        }

        return fieldLabel switch
        {
            "剧目资质" => (2_000, 300, 1_500),
            "提审身份" => (1_200, 200, 1_000),
            _ => (800, 150, 900)
        };
    }

    private static async Task ExecuteSetCheckedAsync(
        IPage page,
        WeixinFormAction action,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var label = action.Label?.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        var desired = action.Enabled ?? true;
        if (string.Equals(label, "我已知悉并同意", StringComparison.Ordinal))
        {
            var current = await EnsureConsentCheckboxStateAsync(page, null, label, desired, cancellationToken);
            if (current != desired)
            {
                return;
            }

            progress?.Report($"微信剧集上传：已设置勾选项 {label}");
            return;
        }

        var group = await FindGroupByLabelAsyncSafe(page, label, 2_000);
        if (string.Equals(label, "AI内容声明", StringComparison.Ordinal) &&
            group is not null)
        {
            var switchLocator = await ResolveSwitchLocatorAsync(group);
            if (switchLocator is not null)
            {
                await ScrollIntoViewIfNeededSafeAsync(switchLocator);
                var current = await ReadCheckableStateAsync(switchLocator) ?? await ReadCheckableStateAsync(group);
                if (current == desired)
                {
                    progress?.Report($"微信剧集上传：已设置勾选项 {label}");
                    return;
                }

                await ToggleCheckableAsync(switchLocator);
                await Task.Delay(300, cancellationToken);

                current = await ReadCheckableStateAsync(switchLocator) ?? await ReadCheckableStateAsync(group);
                if (current == desired || current is null)
                {
                    if (current is null)
                    {
                        progress?.Report($"微信剧集上传：勾选项 {label} 已触发切换，但当前页面无法稳定回读状态，继续执行。");
                    }

                    progress?.Report($"微信剧集上传：已设置勾选项 {label}");
                    return;
                }

                throw new InvalidOperationException($"勾选项 {label} 未切换到期望状态。");
            }
        }

        if (string.Equals(label, "我已知悉并同意", StringComparison.Ordinal) &&
            group is not null)
        {
            var current = await EnsureConsentCheckboxStateAsync(page, group, label, desired, cancellationToken);

            if (current != desired)
            {
                return;
            }

            progress?.Report($"微信剧集上传：已设置勾选项 {label}");
            return;
        }

        var input = await ResolveCheckableInputLocatorAsync(page, group, label, cancellationToken);
        var checkable = await ResolveCheckableLocatorAsync(page, label, cancellationToken);

        if (input is not null)
        {
            await ScrollIntoViewIfNeededSafeAsync(input);

            var current = await ReadCheckableStateAsync(input);
            if (current != desired)
            {
                await TrySetInputCheckStateAsync(input, desired);
                current = await WaitForCheckableStateAsync(input, desired, 1_500);
            }

            if (current != desired)
            {
                foreach (var clickTarget in BuildCheckableClickTargets(input, checkable, group))
                {
                    try
                    {
                        if (await clickTarget.CountAsync() <= 0)
                        {
                            continue;
                        }

                        await ScrollIntoViewIfNeededSafeAsync(clickTarget);
                    }
                    catch
                    {
                        // Ignore scroll failures and still attempt interaction.
                    }

                    await ToggleCheckableAsync(clickTarget);
                    current = await WaitForCheckableStateAsync(input, desired, 1_500);
                    if (current == desired)
                    {
                        break;
                    }
                }
            }

            if (current != desired)
            {
                if (string.Equals(label, "我已知悉并同意", StringComparison.Ordinal))
                {
                    progress?.Report($"微信剧集上传：已设置勾选项 {label}");
                    return;
                }

                throw new InvalidOperationException($"勾选项 {label} 未切换到期望状态。");
            }
        }
        else if (checkable is not null)
        {
            await ScrollIntoViewIfNeededSafeAsync(checkable);

            var current = await ReadCheckableStateAsync(checkable);
            if (current != desired)
            {
                await ToggleCheckableAsync(checkable);
                current = await WaitForCheckableStateAsync(checkable, desired, 2_000);
            }

            if (current != desired)
            {
                if (string.Equals(label, "我已知悉并同意", StringComparison.Ordinal))
                {
                    progress?.Report($"微信剧集上传：已设置勾选项 {label}");
                    return;
                }

                throw new InvalidOperationException($"勾选项 {label} 未切换到期望状态。");
            }
        }
        else
        {
            var control = await FirstVisibleAsync(page, label, false, 10_000);
            await ScrollIntoViewIfNeededSafeAsync(control);
            await control.ClickAsync();
        }

        progress?.Report($"微信剧集上传：已设置勾选项 {label}");
    }

    private static async Task<ILocator?> ResolvePreferredConsentCheckboxInputAsync(
        IPage page,
        string label,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var safe = EscapeSelectorText(label);
        var candidates = new ILocator[]
        {
            page.Locator(".form_footer label.weui-desktop-form__check-label > input.weui-desktop-form__checkbox[type='checkbox']").First,
            page.Locator(".form_footer label.weui-desktop-form__check-label > input[type='checkbox']").First,
            page.Locator($"label.weui-desktop-form__check-label:has(.weui-desktop-form__check-content:has-text(\"{safe}\")) input[type='checkbox']").First,
            page.Locator($"label:has(input[type='checkbox']):has-text(\"{safe}\") input[type='checkbox']").First,
            page.Locator($".weui-desktop-form__check-content:has-text(\"{safe}\")")
                .Locator("xpath=ancestor::label[contains(@class,'weui-desktop-form__check-label')][1]//input[@type='checkbox']")
                .First,
            page.Locator($".weui-desktop-checkbox-label:has-text(\"{safe}\") input[type='checkbox']").First,
            page.Locator($"input[type='checkbox'][aria-label*=\"{safe}\"]").First
        };

        foreach (var candidate in candidates)
        {
            try
            {
                await candidate.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = 300
                });
                return candidate;
            }
            catch
            {
                // Try next candidate.
            }
        }

        return null;
    }

    private static async Task<bool?> EnsureConsentCheckboxStateAsync(
        IPage page,
        ILocator? group,
        string label,
        bool desired,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var row = await ResolveConsentRowLocatorAsync(page, label, cancellationToken);
        var consentInput = row is not null
            ? row.Locator("input[type='checkbox'], input[type='radio']").First
            : await ResolvePreferredConsentCheckboxInputAsync(page, label, cancellationToken)
              ?? (group is not null
                  ? await ResolveConsentCheckboxInputAsync(page, group, label, cancellationToken)
                  : null);
        if (consentInput is null)
        {
            return null;
        }

        if (row is null)
        {
            row = consentInput.Locator("xpath=ancestor::label[1]").First;
        }

        await ScrollIntoViewIfNeededSafeAsync(consentInput);
        var current = await ReadRawInputCheckedStateAsync(consentInput);
        if (current == desired)
        {
            return current;
        }

        var iconLocator = row is not null
            ? row.Locator(".weui-desktop-icon-checkbox, .weui-desktop-icon-radio").First
            : consentInput.Locator("xpath=ancestor::label[1]//*[contains(@class,'weui-desktop-icon-checkbox') or contains(@class,'weui-desktop-icon-radio')]").First;

        try
        {
            await iconLocator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = 500
            });
            await ScrollIntoViewIfNeededSafeAsync(iconLocator);
            await iconLocator.ClickAsync(new LocatorClickOptions
            {
                Force = true,
                Timeout = 1_000
            });
            await Task.Delay(300, cancellationToken);
            current = await WaitForRawConsentStateAsync(consentInput, desired, 800);
            if (current == desired)
            {
                return current;
            }
        }
        catch
        {
            // Fall through to next strategy.
        }

        try
        {
            if (desired)
            {
                await consentInput.CheckAsync(new LocatorCheckOptions
                {
                    Force = true,
                    Timeout = 1_000
                });
            }
            else
            {
                await consentInput.UncheckAsync(new LocatorUncheckOptions
                {
                    Force = true,
                    Timeout = 1_000
                });
            }
        }
        catch
        {
            // Fall through to click-based handling.
        }

        current = await WaitForRawConsentStateAsync(consentInput, desired, 800);
        if (current == desired)
        {
            return current;
        }

        return await ReadRawInputCheckedStateAsync(consentInput);
    }

    private static async Task<ILocator?> ResolveConsentRowLocatorAsync(
        IPage page,
        string label,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var safe = EscapeSelectorText(label);
        var candidates = new ILocator[]
        {
            page.Locator(".form_footer label.weui-desktop-form__check-label").First,
            page.Locator($"label.weui-desktop-form__check-label:has-text(\"{safe}\")").First,
            page.Locator($".weui-desktop-checkbox-label:has-text(\"{safe}\")").First,
            page.Locator($".weui-desktop-form__check-content:has-text(\"{safe}\")")
                .Locator("xpath=ancestor::label[contains(@class,'weui-desktop-form__check-label')][1]")
                .First,
            page.GetByText(label, new PageGetByTextOptions { Exact = false })
                .Locator("xpath=ancestor::label[1]")
                .First
        };

        foreach (var candidate in candidates)
        {
            try
            {
                await candidate.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 400
                });
                return candidate;
            }
            catch
            {
                // Try next candidate.
            }
        }

        return null;
    }

    private static async Task<bool?> TryClickConsentCheckboxVisualAsync(
        IPage page,
        ILocator row,
        ILocator consentInput,
        bool desired,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await ScrollIntoViewIfNeededSafeAsync(row);
            var box = await row.BoundingBoxAsync();
            if (box is null)
            {
                break;
            }

            var offsetX = Math.Min(24d, Math.Max(10d, box.Width * 0.04));
            await page.Mouse.ClickAsync((float)(box.X + offsetX), (float)(box.Y + (box.Height / 2)));

            var current = await WaitForCheckableStateAsync(consentInput, desired, 800);
            if (current == desired)
            {
                return current;
            }

            if (desired && await HasVisibleEnabledButtonAsync(page, "下一步", 250))
            {
                return true;
            }
        }

        return await ReadCheckableStateAsync(consentInput);
    }

    private static async Task<bool?> ReadRawInputCheckedStateAsync(ILocator input)
    {
        try
        {
            await input.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = 1_000
            });
            return await input.EvaluateAsync<bool?>("element => element instanceof HTMLInputElement ? !!element.checked : null");
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool?> WaitForRawConsentStateAsync(ILocator input, bool desired, int timeoutMs)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = await ReadRawInputCheckedStateAsync(input);
            if (current == desired)
            {
                return current;
            }

            await Task.Delay(100);
        }

        return await ReadRawInputCheckedStateAsync(input);
    }

    private static async Task<bool?> TryToggleCheckboxViaDomClickAsync(ILocator input, bool desired)
    {
        try
        {
            return await input.EvaluateAsync<bool?>(
                @"(node, nextState) => {
                    if (!(node instanceof HTMLInputElement)) {
                        return null;
                    }

                    if (node.checked === !!nextState) {
                        return node.checked;
                    }

                    node.click();
                    return node.checked;
                }",
                desired);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ILocator?> ResolveConsentCheckboxInputAsync(
        IPage page,
        ILocator group,
        string label,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = new[]
        {
            group.Locator("input[type='checkbox']").First,
            page.Locator($".weui-desktop-checkbox-label:has-text(\"{EscapeSelectorText(label)}\") input[type='checkbox']").First,
            page.Locator($"label:has-text(\"{EscapeSelectorText(label)}\") input[type='checkbox']").First
        };

        foreach (var candidate in candidates)
        {
            try
            {
                await candidate.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = 800
                });
                return candidate;
            }
            catch
            {
                // Try next candidate.
            }
        }

        return null;
    }

    private static async Task<bool?> TrySetConsentCheckboxStateAsync(ILocator group, bool desired)
    {
        try
        {
            return await group.EvaluateAsync<bool?>(
                @"(node, nextState) => {
                    if (!node || !node.querySelector) {
                        return null;
                    }

                    const checkbox = node.querySelector('input[type=""checkbox""]');
                    if (!(checkbox instanceof HTMLInputElement)) {
                        return null;
                    }

                    checkbox.checked = !!nextState;
                    checkbox.dispatchEvent(new Event('input', { bubbles: true }));
                    checkbox.dispatchEvent(new Event('change', { bubbles: true }));
                    return checkbox.checked;
                }",
                desired);
        }
        catch
        {
            return null;
        }
    }

    private static async Task CloseAuxiliaryNoticePagesAsync(
        IPage currentPage,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var closedCount = 0;
        foreach (var page in currentPage.Context.Pages.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ReferenceEquals(page, currentPage))
            {
                continue;
            }

            if (!await IsAuxiliaryNoticePageAsync(page))
            {
                continue;
            }

            try
            {
                await page.CloseAsync();
                closedCount++;
            }
            catch
            {
                // Ignore pages that are already closing.
            }
        }

        if (closedCount <= 0)
        {
            return;
        }

        try
        {
            await currentPage.BringToFrontAsync();
        }
        catch
        {
            // Ignore bring-to-front failures in headless or restricted environments.
        }

        progress?.Report($"微信剧集上传：已自动关闭 {closedCount} 个《微信小程序微短剧剧目审核服务使用须知》标签页。");
    }

    private static async Task<bool> IsAuxiliaryNoticePageAsync(IPage page)
    {
        try
        {
            var url = page.Url ?? string.Empty;
            if (KnownAuxiliaryPageUrlParts.Any(part => url.Contains(part, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        catch
        {
            // Ignore URL read failures.
        }

        try
        {
            var title = await page.TitleAsync();
            if (!string.IsNullOrWhiteSpace(title) &&
                KnownAuxiliaryPageTitleParts.Any(part => title.Contains(part, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        catch
        {
            // Ignore title read failures.
        }

        return false;
    }

    private static async Task<ILocator?> ResolveSwitchLocatorAsync(ILocator group)
    {
        var candidates = new[]
        {
            group.Locator(".weui-desktop-switch").First,
            group.Locator("[role='switch']").First,
            group.Locator(".ant-switch, .switch").First
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (await candidate.CountAsync() > 0)
                {
                    return candidate;
                }
            }
            catch
            {
                // Try next candidate.
            }
        }

        return null;
    }

    private static async Task ExecuteUploadAsync(
        IPage page,
        WeixinFormAction action,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        FormGroupCache? cache = null)
    {
        var existingPaths = action.Paths
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .ToArray();
        if (string.Equals(action.Label?.Trim(), "剧目制作证明材料", StringComparison.Ordinal))
        {
            existingPaths = existingPaths
                .Take(MaxProofMaterialUploadCount)
                .ToArray();
        }
        if (existingPaths.Length == 0)
        {
            progress?.Report($"微信剧集上传：上传动作 {action.Label ?? action.Selector ?? "未命名"} 缺少有效文件，已跳过。");
            return;
        }

        var profile = GetUploadWaitProfile(action);
        if (!profile.WaitForCompletion)
        {
            var trigger = await ResolveUploadTriggerAsync(page, action, cancellationToken, cache);
            if (trigger is not null &&
                await TrySetFilesViaChooserAsync(page, trigger, existingPaths, cancellationToken, progress, action))
            {
                await WaitForActionTextsAsync(page, action.WaitForTexts, cancellationToken);
                await ClearActiveElementFocusAsync(page);
                progress?.Report($"微信剧集上传：已上传 {action.Label ?? action.Selector ?? "文件"}");
                await Task.Delay(100, cancellationToken);
                return;
            }
        }

        var input = await ResolveUploadInputAsync(page, action, cancellationToken, cache);
        ILocator? uploadGroup = null;
        ILocator? uploadTrigger = null;
        var beforeState = new UploadFieldState(0, 0, 0, false, string.Empty);
        var isVisualUpload = !string.IsNullOrWhiteSpace(action.Label) && IsVisualUploadLabel(action.Label.Trim());
        var beforePageVisualCount = 0;
        if (profile.WaitForCompletion)
        {
            uploadGroup = isVisualUpload && !string.IsNullOrWhiteSpace(action.Label)
                ? await ResolveVisualUploadGroupAsync(page, action.Label.Trim(), cancellationToken)
                : await ResolveUploadGroupFromSelectorAsync(page, action, cancellationToken)
                  ?? await ResolveUploadGroupAsync(page, action, input, cancellationToken, cache);

            uploadGroup ??= await ResolveUploadGroupAsync(page, action, input, cancellationToken, cache);
            beforeState = await ReadUploadFieldStateAsync(uploadGroup);
            if (isVisualUpload && !string.IsNullOrWhiteSpace(action.Label))
            {
                beforePageVisualCount = await ReadPageUploadVisualCountAsync(page);
                progress?.Report($"微信剧集上传：{action.Label} 上传控件诊断 -> {await DescribeVisualUploadGroupAsync(uploadGroup)}");
            }
        }

        var uploadAssigned = false;
        if (profile.WaitForCompletion &&
            uploadGroup is not null &&
            !isVisualUpload)
        {
            if (IsCostReportUploadAction(action))
            {
                progress?.Report($"微信剧集上传：成本报告上传前状态 -> {await DescribeUploadGroupStateAsync(uploadGroup)}");
            }

            uploadTrigger = await ResolveUploadTriggerFromSelectorAsync(page, action, cancellationToken)
                            ?? await ResolveUploadTriggerWithinGroupAsync(uploadGroup, action, cancellationToken);
            if (uploadTrigger is not null)
            {
                if (IsCostReportUploadAction(action))
                {
                    progress?.Report("微信剧集上传：成本报告已命中上传按钮，尝试通过 filechooser/后续 input 设定文件。");
                }
                uploadAssigned = await TrySetFilesViaChooserAsync(page, uploadTrigger, existingPaths, cancellationToken, progress, action);
                if (IsCostReportUploadAction(action))
                {
                    progress?.Report(uploadAssigned
                        ? "微信剧集上传：成本报告文件已写入上传控件，等待页面状态更新。"
                        : "微信剧集上传：成本报告上传按钮未能直接写入文件，准备回退到 input[type=file]。");
                }
            }
            else if (IsCostReportUploadAction(action))
            {
                progress?.Report("微信剧集上传：成本报告未命中可用上传按钮，准备回退到 input[type=file]。");
            }
        }

        if (profile.WaitForCompletion &&
            uploadGroup is not null &&
            isVisualUpload &&
            !string.IsNullOrWhiteSpace(action.Label))
        {
            var visualTrigger = await ResolveVisualUploadTriggerAsync(uploadGroup, action, cancellationToken);
            if (visualTrigger is not null)
            {
                uploadAssigned = await TrySetFilesViaChooserAsync(page, visualTrigger, existingPaths, cancellationToken, progress, action);
                if (uploadAssigned)
                {
                    progress?.Report($"微信剧集上传：{action.Label} 上传分支 -> primary visual filechooser");
                }
            }
        }

        if (!uploadAssigned)
        {
            if (IsCostReportUploadAction(action))
            {
                progress?.Report("微信剧集上传：成本报告回退到直接设置 input[type=file]。");
            }
            else if (isVisualUpload)
            {
                progress?.Report($"微信剧集上传：{action.Label} 上传分支 -> direct input[type=file]");
            }
            await ScrollIntoViewIfNeededSafeAsync(input);
            await input.SetInputFilesAsync(existingPaths);
            if (IsCostReportUploadAction(action))
            {
                // Dispatch synthetic change/input events so React/jQuery upload widgets
                // (e.g. webuploader) pick up the file that was set directly on the input.
                try { await input.DispatchEventAsync("input"); } catch { }
                try { await input.DispatchEventAsync("change"); } catch { }
                progress?.Report("微信剧集上传：成本报告已在 input[type=file] 上派发 change/input 事件。");
            }
        }

        if (isVisualUpload &&
            !string.IsNullOrWhiteSpace(action.Label))
        {
            await Task.Delay(800, cancellationToken);
            if (!await HasVisualUploadSucceededOnPageAsync(page, action.Label.Trim(), existingPaths, cancellationToken))
            {
                progress?.Report($"微信剧集上传：{action.Label} 上传后诊断 -> {await DescribeVisualUploadGroupAsync(uploadGroup!)}");
                var visualGroup = await ResolveVisualUploadGroupAsync(page, action.Label.Trim(), cancellationToken);
                if (visualGroup is not null)
                {
                    var visualTrigger = await ResolveVisualUploadTriggerAsync(visualGroup, action, cancellationToken);
                    if (visualTrigger is not null)
                    {
                        var chooserAssigned = await TrySetFilesViaChooserAsync(page, visualTrigger, existingPaths, cancellationToken, progress, action);
                        if (chooserAssigned)
                        {
                            progress?.Report($"微信剧集上传：{action.Label} 上传分支 -> visual filechooser fallback");
                        }
                        else
                        {
                            progress?.Report($"微信剧集上传：{action.Label} visual filechooser fallback 未能写入文件。");
                        }
                    }
                    else
                    {
                        progress?.Report($"微信剧集上传：{action.Label} 未命中视觉上传按钮，无法执行 filechooser fallback。");
                    }
                }
            }
        }

        if (IsCostReportUploadAction(action) &&
            uploadGroup is not null)
        {
            await Task.Delay(800, cancellationToken);
            var immediateState = await ReadUploadFieldStateAsync(uploadGroup);
            if (IsSameUploadFieldState(beforeState, immediateState))
            {
                progress?.Report("微信剧集上传：成本报告 primary filechooser 后页面状态无变化，尝试 direct input 回退。");
                if (uploadTrigger is not null)
                {
                    var followUpInput = await ResolveFollowUpUploadInputAsync(uploadTrigger, cancellationToken);
                    if (followUpInput is not null)
                    {
                        try
                        {
                            await ScrollIntoViewIfNeededSafeAsync(followUpInput);
                            await followUpInput.SetInputFilesAsync(existingPaths);
                            try { await followUpInput.DispatchEventAsync("input"); } catch { }
                            try { await followUpInput.DispatchEventAsync("change"); } catch { }
                            progress?.Report("微信剧集上传：成本报告 direct input 回退 -> follow-up input[type=file]");
                        }
                        catch
                        {
                            progress?.Report("微信剧集上传：成本报告 follow-up input[type=file] 不可用，继续尝试成本报告专用 input。");
                        }
                    }
                }

                var directCostInput = await ResolveCostReportUploadInputAsync(uploadGroup, cancellationToken);
                if (directCostInput is not null)
                {
                    try
                    {
                        await ScrollIntoViewIfNeededSafeAsync(directCostInput);
                        await directCostInput.SetInputFilesAsync(existingPaths);
                        try { await directCostInput.DispatchEventAsync("input"); } catch { }
                        try { await directCostInput.DispatchEventAsync("change"); } catch { }
                        progress?.Report("微信剧集上传：成本报告 direct input 回退 -> dedicated cost-report input[type=file]");
                    }
                    catch
                    {
                        progress?.Report("微信剧集上传：成本报告专用 input[type=file] 仍不可用。");
                    }
                }

                var afterFallbackState = await ReadUploadFieldStateAsync(uploadGroup);
                if (IsSameUploadFieldState(beforeState, afterFallbackState))
                {
                    var recovered = await TrySetFilesViaAnyCostReportInputAsync(
                        page,
                        uploadGroup,
                        existingPaths,
                        beforeState,
                        progress,
                        cancellationToken);
                    if (!recovered)
                    {
                        progress?.Report("微信剧集上传：成本报告区域内所有 input[type=file] 轮询回退均未驱动页面状态变化。");
                    }
                }
            }
        }

        if (IsCostReportUploadAction(action))
        {
            var refreshedGroup = await ResolveCostReportUploadGroupAsync(page, cancellationToken);
            if (refreshedGroup is not null)
            {
                uploadGroup = refreshedGroup;
            }
        }

        await WaitForActionTextsAsync(page, action.WaitForTexts, cancellationToken);
        if (profile.WaitForCompletion && uploadGroup is not null)
        {
            if (isVisualUpload && !string.IsNullOrWhiteSpace(action.Label))
            {
                await WaitForVisualUploadCompletionAsync(page, action.Label.Trim(), existingPaths, beforePageVisualCount, progress, cancellationToken);
            }
            else if (IsCostReportUploadAction(action))
            {
                await WaitForCostReportUploadCompletionAsync(page, existingPaths, uploadGroup, progress, cancellationToken, timeoutSeconds: profile.DeadlineSeconds > 0 ? profile.DeadlineSeconds : 60);
            }
            else
            {
                await WaitForUploadFieldCompletionAsync(profile, uploadGroup, beforeState, existingPaths, cancellationToken);
            }

            if (IsCostReportUploadAction(action))
            {
                progress?.Report($"微信剧集上传：成本报告上传后状态 -> {await DescribeUploadGroupStateAsync(uploadGroup)}");
            }
        }

        await ClearActiveElementFocusAsync(page);
        progress?.Report($"微信剧集上传：已上传 {action.Label ?? action.Selector ?? "文件"}");
        await Task.Delay(100, cancellationToken);
    }

    private static async Task<bool> TrySetFilesViaChooserAsync(
        IPage page,
        ILocator trigger,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null,
        WeixinFormAction? action = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isCostReport = action is not null && IsCostReportUploadAction(action);

        try
        {
            await ScrollIntoViewIfNeededSafeAsync(trigger);
            var chooser = await page.RunAndWaitForFileChooserAsync(
                async () => await trigger.ClickAsync(),
                new PageRunAndWaitForFileChooserOptions
                {
                    Timeout = 1_500
                });
            await chooser.SetFilesAsync(paths);
            if (isCostReport)
            {
                progress?.Report("微信剧集上传：成本报告上传分支 -> primary filechooser");
            }
            return true;
        }
        catch
        {
            // Fall through and try the common two-step upload pattern:
            // "选择文件" reveals a nested "上传文件"/input[type=file] control.
        }

        try
        {
            await ScrollIntoViewIfNeededSafeAsync(trigger);
            await trigger.ClickAsync(new LocatorClickOptions
            {
                Timeout = 1_000
            });
            await Task.Delay(150, cancellationToken);
        }
        catch
        {
            // Ignore reveal failures and continue with fallback lookup.
        }

        var followUpTrigger = await ResolveFollowUpUploadTriggerAsync(trigger, cancellationToken);
        if (followUpTrigger is not null)
        {
            try
            {
                var chooser = await page.RunAndWaitForFileChooserAsync(
                    async () => await followUpTrigger.ClickAsync(),
                    new PageRunAndWaitForFileChooserOptions
                    {
                        Timeout = 1_500
                    });
                await chooser.SetFilesAsync(paths);
                if (isCostReport)
                {
                    progress?.Report("微信剧集上传：成本报告上传分支 -> follow-up filechooser");
                }
                return true;
            }
            catch
            {
                // Fall back to direct input below.
            }
        }

        var followUpInput = await ResolveFollowUpUploadInputAsync(trigger, cancellationToken);
        if (followUpInput is not null)
        {
            try
            {
                await ScrollIntoViewIfNeededSafeAsync(followUpInput);
                await followUpInput.SetInputFilesAsync(paths);
                if (isCostReport)
                {
                    progress?.Report("微信剧集上传：成本报告上传分支 -> follow-up input[type=file]");
                }
                return true;
            }
            catch
            {
                // Fall through.
            }
        }

        return false;
    }

    private static async Task ExecuteClickAsync(
        IPage page,
        WeixinFormAction action,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(action.Selector))
        {
            var locator = page.Locator(action.Selector).First;
            await locator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 4_000
            });
            await ScrollIntoViewIfNeededSafeAsync(locator);
            await locator.ClickAsync();
            progress?.Report($"微信剧集上传：已点击 {action.Selector}");
            await WaitBrieflyForLoadAsync(page);
            return;
        }

        var text = action.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var locatorByText = await FirstVisibleAsync(page, text, action.Exact, 4_000);
        await ScrollIntoViewIfNeededSafeAsync(locatorByText);
        await locatorByText.ClickAsync();
        progress?.Report($"微信剧集上传：已点击 {text}");
        await WaitBrieflyForLoadAsync(page);
    }

    private static async Task ExecuteScreenshotAsync(
        IPage page,
        string outputDirectory,
        WeixinFormAction action,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(action.Name) ? "first-page" : action.Name.Trim();
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"{name}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            FullPage = true
        });
        progress?.Report(action.Message?.Trim() ?? $"微信剧集上传：已保存截图 {path}");
    }

    private static async Task ExecuteGenericActionAsync(
        IPage page,
        WeixinAutomationConfig config,
        WeixinFormAction action,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var actionType = action.Type.Trim().ToLowerInvariant();
        switch (actionType)
        {
            case "fill":
                await ExecuteFillAsync(page, action, progress, cancellationToken);
                break;
            case "choose":
                await ExecuteChooseAsync(page, action, progress, cancellationToken);
                break;
            case "set_checked":
                await ExecuteSetCheckedAsync(page, action, progress, cancellationToken);
                break;
            case "click":
                await ExecuteClickAsync(page, action, progress, cancellationToken);
                break;
            case "upload":
                await ExecuteUploadAsync(page, action, progress, cancellationToken);
                break;
            case "screenshot":
                await ExecuteScreenshotAsync(page, config.OutputDirectory, action, progress, cancellationToken);
                break;
            default:
                progress?.Report($"微信剧集上传：暂未接入页面动作 {action.Type}，已跳过。");
                break;
        }

        await CloseAuxiliaryNoticePagesAsync(page, progress, cancellationToken);
    }

    private static async Task WaitForUploadCompletionAsync(
        IPage page,
        WeixinUploadQueueOptions uploadQueue,
        IReadOnlyList<string> expectedPaths,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var expectedFileNames = expectedPaths
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToArray();
        var successTexts = uploadQueue.SuccessTexts.Count == 0
            ? new[] { "已上传成功", "上传成功", "处理完成" }
            : uploadQueue.SuccessTexts.ToArray();
        var errorTexts = uploadQueue.ErrorTexts.Count == 0
            ? new[] { "上传失败", "未能上传", "上传异常", "不符合要求", "格式不支持", "超出限制" }
            : uploadQueue.ErrorTexts.ToArray();
        var processingTexts = new[]
        {
            "上传中",
            "处理中",
            "转码中",
            "校验中",
            "请稍候",
            "正在处理",
            "上传处理中"
        };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(uploadQueue.ItemTimeoutSeconds, 30));
        var heartbeatAt = DateTimeOffset.UtcNow;
        var retryRounds = 0;
        var stableFailureRounds = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CloseAuxiliaryNoticePagesAsync(page, progress, cancellationToken);

            var (bodyText, rowTexts) = await GetPageAndRowTextsAsync(page);
            var rowStatus = AnalyseRowTexts(
                rowTexts,
                expectedFileNames,
                successTexts,
                errorTexts,
                processingTexts);
            var hasErrorText = errorTexts.Any(text =>
                !string.IsNullOrWhiteSpace(text) &&
                bodyText.Contains(text, StringComparison.OrdinalIgnoreCase));

            if (rowStatus.FailedRowIndexes.Count > 0 || hasErrorText)
            {
                stableFailureRounds++;
                if (uploadQueue.RetryFailedUploads &&
                    retryRounds < Math.Max(uploadQueue.RetryMaxRounds, 1) &&
                    stableFailureRounds >= Math.Max(uploadQueue.RetryStableRounds, 1) &&
                    await TryClickRetryAsync(page, uploadQueue, rowStatus.FailedRowIndexes, progress, cancellationToken))
                {
                    retryRounds++;
                    stableFailureRounds = 0;
                    heartbeatAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(uploadQueue.RetryIntervalSeconds, 1));
                    continue;
                }

                throw new InvalidOperationException(
                    rowStatus.FailedRowIndexes.Count > 0
                        ? $"微信剧集上传：第二页存在 {rowStatus.FailedRowIndexes.Count} 个上传失败文件。"
                        : "微信剧集上传：第二页检测到上传失败提示。");
            }

            stableFailureRounds = 0;

            if (IsUploadSummaryComplete(bodyText, expectedFileNames.Length) &&
                rowStatus.MatchedRows >= expectedFileNames.Length &&
                rowStatus.SuccessRows >= expectedFileNames.Length &&
                rowStatus.ProcessingRows == 0 &&
                rowStatus.FailedRowIndexes.Count == 0)
            {
                progress?.Report($"微信剧集上传：第二页视频上传完成，成功 {rowStatus.SuccessRows}/{expectedFileNames.Length} 个文件。");
                return;
            }

            if (rowStatus.MatchedRows >= expectedFileNames.Length &&
                rowStatus.SuccessRows >= expectedFileNames.Length &&
                rowStatus.ProcessingRows == 0 &&
                rowStatus.FailedRowIndexes.Count == 0)
            {
                progress?.Report($"微信剧集上传：第二页视频上传完成，所有文件状态均为上传成功。");
                return;
            }

            if (DateTimeOffset.UtcNow >= heartbeatAt)
            {
                progress?.Report(
                    $"微信剧集上传：第二页视频仍在上传处理中，成功 {rowStatus.SuccessRows}/{Math.Max(expectedFileNames.Length, rowStatus.MatchedRows)}，处理中 {rowStatus.ProcessingRows}。");
                heartbeatAt = DateTimeOffset.UtcNow.AddSeconds(5);
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException("微信剧集上传：第二页视频上传超时。");
    }

    private static async Task<bool> TryClickRetryAsync(
        IPage page,
        WeixinUploadQueueOptions uploadQueue,
        IReadOnlyList<int> failedRowIndexes,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (failedRowIndexes.Count == 0)
        {
            return false;
        }

        var retryText = string.IsNullOrWhiteSpace(uploadQueue.RetryActionText)
            ? "重试"
            : uploadQueue.RetryActionText.Trim();

        var rows = page.Locator("tr, .weui-desktop-table__tr, .weui-desktop-table tbody tr");
        var retried = false;
        foreach (var index in failedRowIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var row = rows.Nth(index);
                var retryButton = await FindFirstVisibleAsync(
                    [
                        row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = retryText, Exact = false }).First,
                        row.GetByText(retryText, new LocatorGetByTextOptions { Exact = false }).First
                    ],
                    1_000);
                await ScrollIntoViewIfNeededSafeAsync(retryButton);
                await retryButton.ClickAsync();
                retried = true;
            }
            catch
            {
                // Continue trying other failed rows.
            }
        }

        if (!retried)
        {
            return false;
        }

        progress?.Report($"微信剧集上传：检测到失败项，已点击行内 {retryText} 自动重试。");
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(uploadQueue.RetryIntervalSeconds, 1)), cancellationToken);
        return true;
    }

    private static bool IsUploadSummaryComplete(string bodyText, int expectedCount)
    {
        if (expectedCount <= 0)
        {
            return false;
        }

        var match = UploadSuccessSummaryRegex.Match(bodyText);
        if (!match.Success)
        {
            return false;
        }

        return int.TryParse(match.Groups[1].Value, out var successCount) &&
               int.TryParse(match.Groups[2].Value, out var totalCount) &&
               successCount == totalCount &&
               successCount >= expectedCount;
    }

    private static UploadRowStatusSummary AnalyseRowTexts(
        IReadOnlyList<string> rowTexts,
        IReadOnlyList<string> expectedFileNames,
        IReadOnlyList<string> successTexts,
        IReadOnlyList<string> errorTexts,
        IReadOnlyList<string> processingTexts)
    {
        var matchedRows = 0;
        var successRows = 0;
        var processingRows = 0;
        var failedRowIndexes = new List<int>();

        var expectedMarkers = expectedFileNames
            .SelectMany(BuildFileNameMarkers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var limit = Math.Min(rowTexts.Count, 100);
        for (var index = 0; index < limit; index++)
        {
            var text = rowTexts[index];
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var matched = expectedMarkers.Length == 0 ||
                          expectedMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
            if (!matched)
            {
                continue;
            }

            matchedRows++;

            if (errorTexts.Any(error => !string.IsNullOrWhiteSpace(error) && text.Contains(error, StringComparison.OrdinalIgnoreCase)))
            {
                failedRowIndexes.Add(index);
                continue;
            }

            if (processingTexts.Any(state => !string.IsNullOrWhiteSpace(state) && text.Contains(state, StringComparison.OrdinalIgnoreCase)))
            {
                processingRows++;
                continue;
            }

            if (successTexts.Any(success => !string.IsNullOrWhiteSpace(success) && text.Contains(success, StringComparison.OrdinalIgnoreCase)))
            {
                successRows++;
            }
        }

        return new UploadRowStatusSummary(matchedRows, successRows, processingRows, failedRowIndexes);
    }

    private static IEnumerable<string> BuildFileNameMarkers(string fileName) => WeixinUploadMarkerMatcher.BuildMarkers(fileName);

    private static async Task WaitForActionTextsAsync(
        IPage page,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        foreach (var rawText in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = rawText?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            await WaitForVisibleCandidateAsync(
                page.GetByText(text, new PageGetByTextOptions
                {
                    Exact = false
                }),
                10_000);
        }
    }

    private static async Task<string> GetPageTextAsync(IPage page)
    {
        try
        {
            return await page.Locator("body").InnerTextAsync();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<(string BodyText, string[] RowTexts)> GetPageAndRowTextsAsync(IPage page)
    {
        try
        {
            var result = await page.EvaluateAsync<JsonElement>(
                @"() => {
                    const bodyText = document.body?.innerText ?? '';
                    const rows = document.querySelectorAll('tr, .weui-desktop-table__tr, .weui-desktop-table tbody tr');
                    const rowTexts = Array.from(rows).map(r => r.innerText ?? '');
                    return { bodyText, rowTexts };
                }");

            var bodyText = result.TryGetProperty("bodyText", out var bodyProperty)
                ? bodyProperty.GetString() ?? string.Empty
                : string.Empty;
            var rowTexts = result.TryGetProperty("rowTexts", out var rowsProperty)
                ? rowsProperty
                    .EnumerateArray()
                    .Select(element => element.GetString() ?? string.Empty)
                    .ToArray()
                : Array.Empty<string>();

            return (bodyText, rowTexts);
        }
        catch
        {
            return (string.Empty, Array.Empty<string>());
        }
    }

    private static async Task<ILocator> ResolveEditableLocatorAsync(
        IPage page,
        WeixinFormAction action,
        CancellationToken cancellationToken,
        FormGroupCache? cache = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(action.Selector))
        {
            var selectorLocator = page.Locator(action.Selector).First;
            await selectorLocator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 10_000
            });
            await ScrollIntoViewIfNeededSafeAsync(selectorLocator);
            return selectorLocator;
        }

        if (string.IsNullOrWhiteSpace(action.Label))
        {
            throw new InvalidOperationException("fill 动作缺少 label 或 selector。");
        }

        if (PreferredFillSelectors.TryGetValue(action.Label, out var preferredSelectors))
        {
            foreach (var selector in preferredSelectors)
            {
                try
                {
                    var direct = page.Locator($"{selector}:visible").First;
                    await direct.WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Visible,
                        Timeout = 250
                    });
                    return direct;
                }
                catch
                {
                    // Fall back to generic resolution below.
                }
            }
        }

        var group = await FindGroupByLabelCachedAsync(page, $"fill:{action.Label}", action.Label, 4_000, cache);
        var control = (action.Control ?? "input").Trim().ToLowerInvariant();
        var editableSelectors = control switch
        {
            "textarea" => new[]
            {
                "textarea",
                "[contenteditable='true']",
                "input"
            },
            _ => new[]
            {
                "input",
                "textarea",
                "[contenteditable='true']"
            }
        };

        var candidateScopes = new ILocator[]
        {
            group,
            group.Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__item')][1]").First,
            group.Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__control-group')][1]").First,
            group.Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__label')][1]/following-sibling::*[1]").First,
            group.Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__label')][1]/parent::*").First
        };

        var candidates = new List<ILocator>();
        foreach (var scope in candidateScopes)
        {
            foreach (var selector in editableSelectors)
            {
                candidates.Add(scope.Locator(selector).First);
            }
        }

        candidates.Add(group.Locator("xpath=following::*[self::input or self::textarea or @contenteditable='true'][1]").First);
        candidates.Add(page.GetByLabel(action.Label, new PageGetByLabelOptions { Exact = false }).First);

        return await FindFirstVisibleAsync(candidates, 4_000);
    }

    private static async Task FillEditableAsync(ILocator locator, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await SetEditableValueWithEventsAsync(locator, value);
        if (EditableValuesMatch(await ReadLocatorCurrentValueAsync(locator), value))
        {
            try
            {
                await locator.PressAsync("Tab");
            }
            catch
            {
                // Ignore focus transition failures.
            }

            return;
        }

        try
        {
            await locator.FillAsync(value);
        }
        catch
        {
            await locator.ClickAsync();
            await locator.PressAsync("Meta+A");
            await locator.PressAsync("Control+A");
            await locator.PressAsync("Backspace");
            await locator.PressSequentiallyAsync(value);
        }

        try
        {
            await locator.PressAsync("Tab");
        }
        catch
        {
            // Ignore focus transition failures.
        }
    }

    private static async Task FillEditableInteractivelyAsync(ILocator locator, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await locator.ClickAsync(new LocatorClickOptions
        {
            Timeout = 1_000
        });

        try
        {
            await locator.PressAsync("Meta+A");
        }
        catch
        {
            // Ignore and fall through.
        }

        try
        {
            await locator.PressAsync("Control+A");
        }
        catch
        {
            // Ignore and continue.
        }

        try
        {
            await locator.PressAsync("Backspace");
        }
        catch
        {
            // Ignore and continue.
        }

        try
        {
            await locator.FillAsync(value);
        }
        catch
        {
            await locator.PressSequentiallyAsync(value);
        }

        try
        {
            await locator.PressAsync("Tab");
        }
        catch
        {
            // Ignore focus transition failures.
        }
    }

    private static async Task<ILocator?> ResolveCheckableInputLocatorAsync(
        IPage page,
        ILocator? group,
        string label,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = new List<ILocator>
        {
            page.GetByLabel(label, new PageGetByLabelOptions { Exact = false }).First,
            page.Locator($".weui-desktop-checkbox-label:has-text(\"{EscapeSelectorText(label)}\") input[type='checkbox']").First,
            page.Locator($"label:has-text(\"{EscapeSelectorText(label)}\") input[type='checkbox'], label:has-text(\"{EscapeSelectorText(label)}\") input[type='radio']").First
        };

        if (group is not null)
        {
            candidates.Insert(0, group.Locator("input[type='checkbox'], input[type='radio']").First);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                await candidate.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = 800
                });
                return candidate;
            }
            catch
            {
                // Try next candidate.
            }
        }

        return null;
    }

    private static async Task TrySetInputCheckStateAsync(ILocator input, bool desired)
    {
        try
        {
            if (desired)
            {
                await input.CheckAsync(new LocatorCheckOptions
                {
                    Force = true,
                    Timeout = 1_000
                });
            }
            else
            {
                await input.UncheckAsync(new LocatorUncheckOptions
                {
                    Force = true,
                    Timeout = 1_000
                });
            }
        }
        catch
        {
            try
            {
                await input.EvaluateAsync(
                    @"(node, nextState) => {
                        if (!(node instanceof HTMLInputElement)) {
                            return;
                        }

                        node.checked = !!nextState;
                        node.dispatchEvent(new Event('input', { bubbles: true }));
                        node.dispatchEvent(new Event('change', { bubbles: true }));
                    }",
                    desired);
            }
            catch
            {
                // Fall back to click-based handling.
            }
        }
    }

    private static IEnumerable<ILocator> BuildCheckableClickTargets(
        ILocator input,
        ILocator? checkable,
        ILocator? group)
    {
        yield return input;

        if (checkable is not null)
        {
            yield return checkable;
        }

        yield return input.Locator("xpath=ancestor::label[1]").First;
        yield return input.Locator("xpath=ancestor::label[1]//*[contains(@class,'weui-desktop-icon-checkbox') or contains(@class,'weui-desktop-icon-radio')]").First;

        if (group is not null)
        {
            yield return group.Locator(".weui-desktop-switch, .ant-switch, .switch").First;
            yield return group.Locator(".weui-desktop-icon-checkbox, .weui-desktop-icon-radio").First;
            yield return group.Locator("[role='switch'], [role='checkbox'], [aria-checked]").First;
        }
    }

    private static async Task ForceFillEditableAsync(
        IPage page,
        WeixinFormAction action,
        ILocator primaryLocator,
        string value,
        CancellationToken cancellationToken,
        FormGroupCache? cache = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = new List<ILocator> { primaryLocator };
        if (!string.IsNullOrWhiteSpace(action.Label) &&
            PreferredFillSelectors.TryGetValue(action.Label, out var preferredSelectors))
        {
            foreach (var selector in preferredSelectors)
            {
                candidates.Add(page.Locator($"{selector}:visible"));
            }
        }

        if (!string.IsNullOrWhiteSpace(action.Label))
        {
            var group = await FindGroupByLabelAsyncSafe(page, action.Label, 800);
            if (group is null && cache is not null && cache.TryGet($"fill:{action.Label}", out var cachedGroup))
            {
                group = cachedGroup;
            }

            if (group is not null)
            {
                candidates.Add(group.Locator("input:visible, textarea:visible, [contenteditable='true']:visible"));
            }
        }

        foreach (var candidate in candidates)
        {
            await ForceFillLocatorCollectionAsync(candidate, value);
        }
    }

    private static async Task ForceFillLocatorCollectionAsync(ILocator locator, string value)
    {
        try
        {
            var count = await locator.CountAsync();
            if (count <= 0)
            {
                return;
            }

            var limit = Math.Max(1, Math.Min(count, 5));
            for (var index = 0; index < limit; index++)
            {
                await SetEditableValueWithEventsAsync(locator.Nth(index), value);
            }
        }
        catch
        {
            await SetEditableValueWithEventsAsync(locator, value);
        }
    }

    private static async Task SetEditableValueWithEventsAsync(ILocator locator, string value)
    {
        try
        {
            if (!await locator.IsVisibleAsync())
            {
                return;
            }

            await locator.EvaluateAsync(
                @"(node, nextValue) => {
                    if (!node) {
                        return;
                    }

                    const dispatch = (target, type) => {
                        target.dispatchEvent(new Event(type, { bubbles: true }));
                    };

                    if (node instanceof HTMLInputElement || node instanceof HTMLTextAreaElement) {
                        node.focus();
                        node.value = nextValue ?? '';
                        dispatch(node, 'input');
                        dispatch(node, 'change');
                        node.blur();
                        dispatch(node, 'blur');
                        return;
                    }

                    if (node instanceof HTMLElement && node.isContentEditable) {
                        node.focus();
                        node.textContent = nextValue ?? '';
                        dispatch(node, 'input');
                        dispatch(node, 'change');
                        node.blur();
                        dispatch(node, 'blur');
                    }
                }",
                value);
        }
        catch
        {
            // Ignore force-fill failures and let follow-up verification decide.
        }
    }

    private static async Task<string?> ReadEditableValueAsync(
        IPage page,
        WeixinFormAction action,
        ILocator locator,
        CancellationToken cancellationToken,
        FormGroupCache? cache = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(action.Label) &&
            PreferredFillSelectors.TryGetValue(action.Label, out var preferredSelectors))
        {
            foreach (var selector in preferredSelectors)
            {
                var values = await ReadEditableValuesAsync(page.Locator(selector));
                var value = values.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
                if (value is not null)
                {
                    return value;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(action.Label))
        {
            var group = await FindGroupByLabelAsyncSafe(page, action.Label, 1_000);
            if (group is null && cache is not null && cache.TryGet($"fill:{action.Label}", out var cachedGroup))
            {
                group = cachedGroup;
            }
            if (group is not null)
            {
                var values = await ReadEditableValuesAsync(group.Locator("input, textarea, [contenteditable='true']"));
                var groupValue = values.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
                if (groupValue is not null)
                {
                    return groupValue;
                }
            }
        }

        try
        {
            return await ReadLocatorCurrentValueAsync(locator);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<string?>> ReadEditableValuesAsync(ILocator locator)
    {
        var values = new List<string?>();
        try
        {
            var count = await locator.CountAsync();
            var limit = Math.Max(1, Math.Min(count, 5));
            for (var index = 0; index < limit; index++)
            {
                var candidate = locator.Nth(index);
                var value = await ReadLocatorCurrentValueAsync(candidate);
                values.Add(value);
            }
        }
        catch
        {
            // Ignore and let caller fall back.
        }

        return values;
    }

    private static async Task<string?> ReadLocatorCurrentValueAsync(ILocator locator)
    {
        try
        {
            var value = await locator.InputValueAsync();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        catch
        {
            // Ignore and continue.
        }

        foreach (var attr in new[] { "value", "title", "aria-label", "placeholder" })
        {
            try
            {
                var attrValue = await locator.GetAttributeAsync(attr);
                if (!string.IsNullOrWhiteSpace(attrValue))
                {
                    return attrValue;
                }
            }
            catch
            {
                // Ignore and continue.
            }
        }

        try
        {
            var innerText = await locator.InnerTextAsync();
            if (!string.IsNullOrWhiteSpace(innerText))
            {
                return innerText;
            }
        }
        catch
        {
            // Ignore and continue.
        }

        try
        {
            var textContent = await locator.TextContentAsync();
            if (!string.IsNullOrWhiteSpace(textContent))
            {
                return textContent;
            }
        }
        catch
        {
            // Ignore and continue.
        }

        return null;
    }

    private static string NormalizeEditableCompareText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Concat(value.Where(ch => !char.IsWhiteSpace(ch))).Trim().ToLowerInvariant();
    }

    private static bool EditableValuesMatch(string? actualValue, string expectedValue)
    {
        var actual = NormalizeEditableCompareText(actualValue);
        var expected = NormalizeEditableCompareText(expectedValue);
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        return string.Equals(actual, expected, StringComparison.Ordinal) ||
               actual.Contains(expected, StringComparison.Ordinal) ||
               expected.Contains(actual, StringComparison.Ordinal);
    }

    private static bool RequiresInteractiveFill(WeixinFormAction action)
    {
        var selector = action.Selector?.Trim();
        if (!string.IsNullOrWhiteSpace(selector) &&
            (selector.Contains("制作成本", StringComparison.OrdinalIgnoreCase) ||
             selector.Contains("placeholder*='成本'", StringComparison.OrdinalIgnoreCase) ||
             selector.Contains("placeholder*=\"成本\"", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var label = action.Label?.Trim();
        return string.Equals(label, "剧目制作成本", StringComparison.Ordinal) ||
               string.Equals(label, "制作成本", StringComparison.Ordinal);
    }

    private static async Task<string?> WaitForEditableValueAsync(
        IPage page,
        WeixinFormAction action,
        ILocator locator,
        string expectedValue,
        CancellationToken cancellationToken,
        FormGroupCache? cache = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        string? lastValue = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(action.Label) &&
                PreferredFillSelectors.TryGetValue(action.Label, out var preferredSelectors))
            {
                foreach (var selector in preferredSelectors)
                {
                    var values = await ReadEditableValuesAsync(page.Locator(selector));
                    var matched = values.FirstOrDefault(value => EditableValuesMatch(value, expectedValue));
                    if (matched is not null)
                    {
                        return matched;
                    }
                }
            }

            lastValue = await ReadEditableValueAsync(page, action, locator, cancellationToken, cache);
            if (EditableValuesMatch(lastValue, expectedValue))
            {
                return lastValue;
            }

            await Task.Delay(120, cancellationToken);
        }

        return lastValue;
    }

    private static async Task<ILocator?> ResolveCheckableLocatorAsync(
        IPage page,
        string label,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var group = await FindGroupByLabelAsyncSafe(page, label, 2_000);
        var candidates = new List<ILocator>
        {
            page.GetByRole(AriaRole.Checkbox, new PageGetByRoleOptions { NameString = label, Exact = false }).First,
            page.GetByLabel(label, new PageGetByLabelOptions { Exact = false }).First,
            page.Locator($".weui-desktop-checkbox-label:has-text(\"{EscapeSelectorText(label)}\") input[type='checkbox']").First,
            page.Locator($"[role='switch']:has-text(\"{EscapeSelectorText(label)}\")").First,
            page.Locator($"[role='checkbox']:has-text(\"{EscapeSelectorText(label)}\")").First
        };

        if (group is not null)
        {
            candidates.Add(group.Locator("input[type='checkbox']").First);
            candidates.Add(group.Locator("[role='switch']").First);
            candidates.Add(group.Locator("[role='checkbox']").First);
            candidates.Add(group.Locator("[aria-checked]").First);
            candidates.Add(group.Locator(".weui-desktop-switch, .ant-switch, .switch").First);
        }

        try
        {
            return await WaitForFirstCandidateAsync(
                candidates,
                1_500,
                WaitForSelectorState.Attached,
                "not found",
                scrollOnSuccess: false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ILocator?> FindGroupByLabelAsyncSafe(IPage page, string label, int timeoutMs)
    {
        try
        {
            return await FindGroupByLabelAsync(page, label, timeoutMs);
        }
        catch
        {
            return null;
        }
    }

    private static async Task ToggleCheckableAsync(ILocator locator)
    {
        try
        {
            if (await locator.CountAsync() <= 0)
            {
                return;
            }

            await locator.ClickAsync(new LocatorClickOptions
            {
                Timeout = 1_000,
                Force = true
            });
            return;
        }
        catch
        {
            // Fall through to DOM click.
        }

        await locator.EvaluateAsync(
            @"node => {
                const target = node instanceof HTMLElement ? node : node?.parentElement;
                if (!target) {
                    return;
                }

                target.click();
            }");
    }

    private static async Task<bool?> WaitForCheckableStateAsync(ILocator locator, bool desired, int timeoutMs)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = await ReadCheckableStateAsync(locator);
            if (current == desired)
            {
                return current;
            }

            await Task.Delay(100);
        }

        return await ReadCheckableStateAsync(locator);
    }

    private static async Task<bool?> ReadCheckableStateAsync(ILocator locator)
    {
        try
        {
            return await locator.EvaluateAsync<bool?>(
                @"node => {
                    const candidates = [];
                    if (node) {
                        candidates.push(node);
                        if (node.querySelectorAll) {
                            candidates.push(...node.querySelectorAll('input[type=""checkbox""], input[type=""radio""], [role=""switch""], [role=""checkbox""], [aria-checked], .weui-desktop-switch, .ant-switch, .switch'));
                        }
                    }

                    for (const item of candidates) {
                        if (!item) {
                            continue;
                        }

                        if (item instanceof HTMLInputElement) {
                            if (item.checked) {
                                return true;
                            }

                            if (item.hasAttribute?.('checking') || item.hasAttribute?.('checked')) {
                                return true;
                            }

                            return false;
                        }

                        const ariaChecked = item.getAttribute?.('aria-checked');
                        if (ariaChecked === 'true') {
                            return true;
                        }

                        if (ariaChecked === 'false') {
                            return false;
                        }

                        const cls = typeof item.className === 'string' ? item.className.toLowerCase() : '';
                        if (cls.includes('checked') || cls.includes('is-checked') || cls.includes('switch-on') || cls.includes('switch-checked')) {
                            return true;
                        }

                        if (cls.includes('switch-off') || cls.includes('uncheck')) {
                            return false;
                        }
                    }

                    return null;
                }");
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> HasVisibleEnabledButtonAsync(IPage page, string text, int timeoutMs)
    {
        try
        {
            var directNextButton = await ResolveNextButtonAsync(page, text, timeoutMs);
            if (await directNextButton.IsEnabledAsync())
            {
                return true;
            }
        }
        catch
        {
            // Fall back to generic role lookup below.
        }

        var locator = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            NameString = text,
            Exact = false
        });

        var count = await locator.CountAsync();
        var limit = Math.Max(1, Math.Min(count, 10));
        for (var index = 0; index < limit; index++)
        {
            var candidate = locator.Nth(index);
            try
            {
                await candidate.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = timeoutMs
                });

                if (await candidate.IsEnabledAsync())
                {
                    return true;
                }
            }
            catch
            {
                // Ignore and continue.
            }
        }

        return false;
    }

    private static async Task<ILocator> ResolveNextButtonAsync(IPage page, string text, int timeoutMs)
    {
        var nextText = text.Trim();
        var scopedButtons = page.Locator(".next_btn button");

        try
        {
            var count = await scopedButtons.CountAsync();
            var limit = Math.Max(0, Math.Min(count, 8));
            for (var index = 0; index < limit; index++)
            {
                var candidate = scopedButtons.Nth(index);
                try
                {
                    await candidate.WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Visible,
                        Timeout = timeoutMs
                    });

                    var candidateText = (await candidate.InnerTextAsync()).Trim();
                    if (candidateText.Contains(nextText, StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Try next button.
                }
            }
        }
        catch
        {
            // Fall through to generic candidates below.
        }

        var candidates = new ILocator[]
        {
            page.Locator($".next_btn button.weui-desktop-btn.weui-desktop-btn_default.weui-desktop-btn_mini:has-text(\"{EscapeSelectorText(nextText)}\")").First,
            page.Locator($".next_btn button:has-text(\"{EscapeSelectorText(nextText)}\")").First,
            page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                NameString = nextText,
                Exact = false
            }).First
        };

        var nextButton = await WaitForFirstCandidateAsync(
            candidates,
            timeoutMs,
            WaitForSelectorState.Visible,
            "not found",
            scrollOnSuccess: false);

        return nextButton ?? throw new TimeoutException($"未找到按钮：{text}");
    }

    private static async Task<ILocator> ResolveUploadInputAsync(
        IPage page,
        WeixinFormAction action,
        CancellationToken cancellationToken,
        FormGroupCache? cache = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsCostReportUploadAction(action))
        {
            var costReportGroup = await ResolveCostReportUploadGroupAsync(page, cancellationToken);
            if (costReportGroup is not null)
            {
                var costReportInput = await ResolveCostReportUploadInputAsync(costReportGroup, cancellationToken);
                if (costReportInput is not null)
                {
                    return costReportInput;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(action.Selector))
        {
            try
            {
                var direct = page.Locator(action.Selector).First;
                await direct.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = 1_000
                });
                return direct;
            }
            catch
            {
                var fallback = await ResolveUploadInputFromSelectorFallbackAsync(page, action.Selector, cancellationToken);
                if (fallback is not null)
                {
                    return fallback;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(action.Label))
        {
            throw new InvalidOperationException("upload 动作缺少 label 或 selector。");
        }

        var preferredInput = await ResolvePreferredUploadInputAsync(page, action, cancellationToken);
        if (preferredInput is not null)
        {
            return preferredInput;
        }

        var group = await FindUploadGroupByLabelAsync(page, action, 4_000, cache);
        var candidates = new[]
        {
            group.Locator("input[type='file']").First,
            page.Locator($"input[type='file'][accept*='image'], input[type='file'][accept*='pdf'], input[type='file']").First
        };

        return await FindFirstVisibleAsync(candidates, 4_000, WaitForSelectorState.Attached);
    }

    private static async Task<ILocator?> ResolvePreferredUploadInputAsync(
        IPage page,
        WeixinFormAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var label = action.Label?.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        if (IsVisualUploadLabel(label))
        {
            var visualGroup = await ResolveVisualUploadGroupAsync(page, label, cancellationToken);
            if (visualGroup is not null)
            {
                var visualInputs = new ILocator[]
                {
                    visualGroup.Locator("input[type='file']").First,
                    visualGroup.Locator(".upload-input, .weui-desktop-upload__input, .weui-desktop-form__file-upload__input").First
                };

                try
                {
                    return await WaitForFirstCandidateAsync(
                        visualInputs,
                        600,
                        WaitForSelectorState.Attached,
                        "not found",
                        scrollOnSuccess: false);
                }
                catch
                {
                    // Fall through to generic candidates below.
                }
            }
        }

        var safe = EscapeSelectorText(label);
        var candidates = new ILocator[]
        {
            page.Locator($".weui-desktop-form__control-group:has(.weui-desktop-form__label:has-text(\"{safe}\")) input[type='file']").First,
            page.Locator($".weui-desktop-form__control-group_label-t:has(.weui-desktop-form__label:has-text(\"{safe}\")) input[type='file']").First,
            page.Locator($".weui-desktop-form__item:has(.weui-desktop-form__label:has-text(\"{safe}\")) input[type='file']").First,
            page.Locator($"xpath=//*[contains(@class,'weui-desktop-form__label')][contains(normalize-space(.), \"{safe}\")]/ancestor::*[contains(@class,'weui-desktop-form__control-group') or contains(@class,'weui-desktop-form__item')][1]//input[@type='file']").First
        };

        try
        {
            return await WaitForFirstCandidateAsync(
                candidates,
                400,
                WaitForSelectorState.Attached,
                "not found",
                scrollOnSuccess: false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ILocator?> ResolveUploadTriggerAsync(
        IPage page,
        WeixinFormAction action,
        CancellationToken cancellationToken,
        FormGroupCache? cache = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsCostReportUploadAction(action))
        {
            var costReportGroup = await ResolveCostReportUploadGroupAsync(page, cancellationToken);
            if (costReportGroup is not null)
            {
                return await ResolveCostReportUploadTriggerAsync(costReportGroup, cancellationToken);
            }
        }

        if (!string.IsNullOrWhiteSpace(action.Label) &&
            IsVisualUploadLabel(action.Label.Trim()))
        {
            var visualGroup = await ResolveVisualUploadGroupAsync(page, action.Label.Trim(), cancellationToken);
            if (visualGroup is not null)
            {
                return await ResolveVisualUploadTriggerAsync(visualGroup, action, cancellationToken);
            }
        }

        if (string.IsNullOrWhiteSpace(action.Label) || string.IsNullOrWhiteSpace(action.Text))
        {
            return null;
        }

        try
        {
            var group = await FindUploadGroupByLabelAsync(page, action, 1_000, cache);
            var triggerText = action.Text.Trim();
            var candidates = new ILocator[]
            {
                group.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = triggerText, Exact = false }).First,
                group.GetByText(triggerText, new LocatorGetByTextOptions { Exact = false }).First,
                group.Locator("button:has-text('选择'), .weui-desktop-btn:has-text('选择')").First
            };

            return await WaitForFirstCandidateAsync(
                candidates,
                500,
                WaitForSelectorState.Visible,
                "not found",
                scrollOnSuccess: false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ILocator?> ResolveUploadTriggerWithinGroupAsync(
        ILocator group,
        WeixinFormAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(action.Label) &&
            IsVisualUploadLabel(action.Label.Trim()))
        {
            var visualTrigger = await ResolveVisualUploadTriggerAsync(group, action, cancellationToken);
            if (visualTrigger is not null)
            {
                return visualTrigger;
            }
        }

        var triggerText = string.IsNullOrWhiteSpace(action.Text)
            ? "选择"
            : action.Text.Trim();
        var candidates = new ILocator[]
        {
            group.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = triggerText, Exact = false }).First,
            group.GetByText(triggerText, new LocatorGetByTextOptions { Exact = false }).First,
            group.Locator("button:has-text('选择'), .weui-desktop-btn:has-text('选择')").First
        };

        try
        {
            return await WaitForFirstCandidateAsync(
                candidates,
                500,
                WaitForSelectorState.Visible,
                "not found",
                scrollOnSuccess: false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ILocator?> ResolveVisualUploadTriggerAsync(
        ILocator group,
        WeixinFormAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var configuredText = action.Text?.Trim();
        var candidates = new List<ILocator>();
        if (!string.IsNullOrWhiteSpace(configuredText))
        {
            candidates.Add(group.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = configuredText, Exact = false }).First);
            candidates.Add(group.GetByText(configuredText, new LocatorGetByTextOptions { Exact = false }).First);
        }

        candidates.AddRange(
        [
            group.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "选择图片", Exact = false }).First,
            group.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "选择文件", Exact = false }).First,
            group.Locator(".upload-button-wrapper").First,
            group.Locator(".weui-desktop-upload__img__btn").First,
            group.Locator(".webuploader-pick").First,
            group.Locator("button:has-text('选择图片'), button:has-text('选择文件')").First,
            group.Locator(".weui-desktop-btn:has-text('选择图片'), .weui-desktop-btn:has-text('选择文件')").First
        ]);

        try
        {
            return await WaitForFirstCandidateAsync(
                candidates,
                800,
                WaitForSelectorState.Visible,
                "not found",
                scrollOnSuccess: false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ILocator?> ResolveUploadGroupFromSelectorAsync(
        IPage page,
        WeixinFormAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsCostReportUploadAction(action))
        {
            return await ResolveCostReportUploadGroupAsync(page, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(action.Selector))
        {
            return null;
        }

        var anchorText = ExtractContainsText(action.Selector);
        if (string.IsNullOrWhiteSpace(anchorText))
        {
            return null;
        }

        var safe = EscapeSelectorText(anchorText);
        var candidates = new ILocator[]
        {
            page.Locator($"xpath=//p[contains(normalize-space(.), \"{safe}\")]/following-sibling::div[.//button[contains(normalize-space(.), '选择文件') or contains(normalize-space(.), '上传文件')]][1]").First,
            page.Locator($"xpath=//p[contains(normalize-space(.), \"{safe}\")]/following-sibling::div[contains(@class,'weui-desktop-form__control-group')][1]").First,
            page.GetByText(anchorText, new PageGetByTextOptions { Exact = false })
                .Locator("xpath=following-sibling::div[contains(@class,'weui-desktop-form__control-group')][1]")
                .First
        };

        try
        {
            return await WaitForFirstCandidateAsync(
                candidates,
                800,
                WaitForSelectorState.Visible,
                "not found",
                scrollOnSuccess: false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ILocator?> ResolveUploadTriggerFromSelectorAsync(
        IPage page,
        WeixinFormAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsCostReportUploadAction(action))
        {
            var costReportGroup = await ResolveCostReportUploadGroupAsync(page, cancellationToken);
            if (costReportGroup is null)
            {
                return null;
            }

            return await ResolveCostReportUploadTriggerAsync(costReportGroup, cancellationToken);
        }

        var anchorText = string.IsNullOrWhiteSpace(action.Selector)
            ? null
            : ExtractContainsText(action.Selector);
        var safe = string.IsNullOrWhiteSpace(anchorText)
            ? null
            : EscapeSelectorText(anchorText);

        var group = await ResolveUploadGroupFromSelectorAsync(page, action, cancellationToken);
        if (group is null)
        {
            return null;
        }

        var candidates = new List<ILocator>();
        if (!string.IsNullOrWhiteSpace(safe))
        {
            candidates.Add(page.Locator($"xpath=//p[contains(normalize-space(.), \"{safe}\")]/following-sibling::div//button[contains(normalize-space(.), '选择文件')]").First);
            candidates.Add(page.Locator($"xpath=//p[contains(normalize-space(.), \"{safe}\")]/following-sibling::div//button[contains(normalize-space(.), '上传文件')]").First);
        }

        candidates.AddRange(
        [
            group.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "选择文件", Exact = false }).First,
            group.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "选择", Exact = false }).First,
            group.Locator("button:has-text('选择文件')").First,
            group.Locator("button:has-text('选择')").First,
            group.Locator(".weui-desktop-btn:has-text('选择文件'), .weui-desktop-btn:has-text('选择')").First
        ]);

        try
        {
            return await WaitForFirstCandidateAsync(
                candidates,
                800,
                WaitForSelectorState.Visible,
                "not found",
                scrollOnSuccess: false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ILocator?> ResolveFollowUpUploadTriggerAsync(
        ILocator trigger,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scopeCandidates = new[]
        {
            trigger.Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__control-group')][1]").First,
            trigger.Locator("xpath=ancestor::*[contains(@class,'weui-desktop-btn_wrp')][1]/following-sibling::*").First,
            trigger.Locator("xpath=ancestor::*[contains(@class,'custom-file-upload')][1]").First
        };

        foreach (var scope in scopeCandidates)
        {
            try
            {
                if (await scope.CountAsync() <= 0)
                {
                    continue;
                }

                var candidates = new ILocator[]
                {
                    scope.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "上传文件", Exact = false }).First,
                    scope.Locator("button:has-text('上传文件')").First,
                    scope.Locator(".weui-desktop-btn:has-text('上传文件')").First
                };

                return await WaitForFirstCandidateAsync(
                    candidates,
                    600,
                    WaitForSelectorState.Visible,
                    "not found",
                    scrollOnSuccess: false);
            }
            catch
            {
                // Try next scope.
            }
        }

        return null;
    }

    private static async Task<ILocator?> ResolveFollowUpUploadInputAsync(
        ILocator trigger,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scopeCandidates = new[]
        {
            trigger.Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__control-group')][1]").First,
            trigger.Locator("xpath=ancestor::*[contains(@class,'custom-file-upload')][1]").First,
            trigger.Locator("xpath=ancestor::*[contains(@class,'weui-desktop-btn_wrp')][1]/following-sibling::*").First
        };

        foreach (var scope in scopeCandidates)
        {
            try
            {
                if (await scope.CountAsync() <= 0)
                {
                    continue;
                }

                var candidates = new ILocator[]
                {
                    scope.Locator(".custom-file-upload:not([style*='display: none']) input[type='file']").First,
                    scope.Locator("xpath=.//*[contains(@class,'custom-file-upload') and not(contains(@style,'display: none'))]//input[@type='file']").First,
                    scope.Locator(".upload-button-wrapper input[type='file']").First
                };

                return await WaitForFirstCandidateAsync(
                    candidates,
                    600,
                    WaitForSelectorState.Attached,
                    "not found",
                    scrollOnSuccess: false);
            }
            catch
            {
                // Try next scope.
            }
        }

        return null;
    }

    private static async Task<ILocator?> ResolveCostReportUploadGroupAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = new ILocator[]
        {
            page.Locator(".weui-desktop-form__item:has-text('成本配置比例情况报告'):has(button:has-text('选择文件'))").First,
            page.Locator(".weui-desktop-form__item:has-text('成本配置比例情况报告'):has(button:has-text('重新选择'))").First,
            page.Locator(".weui-desktop-form__item:has-text('成本配置比例情况报告'):has(input[type='file'])").First,
            page.Locator(".weui-desktop-form__control-group:has-text('成本配置比例情况报告'):has(button:has-text('选择文件'))").First,
            page.Locator(".weui-desktop-form__control-group:has-text('成本配置比例情况报告'):has(button:has-text('重新选择'))").First,
            page.Locator(".weui-desktop-form__control-group:has-text('成本配置比例情况报告'):has(input[type='file'])").First,
            page.Locator(".weui-desktop-form__item:has-text('成本配置比例情况报告')").First,
            page.Locator(".weui-desktop-form__control-group:has-text('成本配置比例情况报告')").First,
            page.Locator("xpath=//*[contains(normalize-space(.), '成本配置比例情况报告')]/ancestor::*[contains(@class,'weui-desktop-form__item')][1]").First,
            page.Locator("xpath=//*[contains(normalize-space(.), '成本配置比例情况报告')]/ancestor::*[contains(@class,'weui-desktop-form__control-group')][1]").First,
            page.Locator("xpath=//p[contains(normalize-space(.), '成本配置比例情况报告')]/following-sibling::div[.//button[contains(normalize-space(.), '选择文件') or contains(normalize-space(.), '上传文件')]][1]").First,
            page.Locator("xpath=//*[contains(normalize-space(.), '成本配置比例情况报告')]/following-sibling::*[.//button[contains(normalize-space(.), '选择文件') or contains(normalize-space(.), '上传文件')]][1]").First,
            page.GetByText("成本配置比例情况报告", new PageGetByTextOptions { Exact = false })
                .Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__control-group') or contains(@class,'weui-desktop-form__item')][1]")
                .First
        };

        try
        {
            return await WaitForFirstCandidateAsync(
                candidates,
                1_000,
                WaitForSelectorState.Visible,
                "not found",
                scrollOnSuccess: false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ILocator?> ResolveCostReportUploadTriggerAsync(
        ILocator group,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = new ILocator[]
        {
            group.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "重新选择", Exact = false }).First,
            group.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "选择文件", Exact = false }).First,
            group.Locator("button:has-text('重新选择')").First,
            group.Locator("button:has-text('选择文件')").First,
            group.Locator(".weui-desktop-btn:has-text('重新选择')").First,
            group.Locator(".weui-desktop-btn:has-text('选择文件')").First,
            group.GetByText("选择文件", new LocatorGetByTextOptions { Exact = false }).First
        };

        try
        {
            return await WaitForFirstCandidateAsync(
                candidates,
                800,
                WaitForSelectorState.Visible,
                "not found",
                scrollOnSuccess: false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ILocator?> ResolveCostReportUploadInputAsync(
        ILocator group,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = new ILocator[]
        {
            group.Locator(".custom-file-upload input[type='file']").First,
            group.Locator("xpath=.//*[contains(@class,'custom-file-upload')]//input[@type='file']").First,
            group.Locator("input[type='file']").First
        };

        try
        {
            return await WaitForFirstCandidateAsync(
                candidates,
                800,
                WaitForSelectorState.Attached,
                "not found",
                scrollOnSuccess: false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> TrySetFilesViaAnyCostReportInputAsync(
        IPage page,
        ILocator group,
        IReadOnlyList<string> paths,
        UploadFieldState beforeState,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = new ILocator[]
        {
            group.Locator("input[type='file']"),
            group.Locator("xpath=.//input[@type='file']"),
            page.GetByText("成本配置比例情况报告", new PageGetByTextOptions { Exact = false })
                .Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__control-group') or contains(@class,'weui-desktop-form__item')][1]//input[@type='file']")
                ,
            page.Locator("xpath=//*[contains(normalize-space(.), '成本配置比例情况报告')]/ancestor::*[contains(@class,'weui-desktop-form__control-group') or contains(@class,'weui-desktop-form__item')][1]//input[@type='file']")
        };

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var count = await candidate.CountAsync();
                var limit = Math.Max(1, Math.Min(count, 5));
                for (var index = 0; index < limit; index++)
                {
                    var input = candidate.Nth(index);
                    try
                    {
                        await input.WaitForAsync(new LocatorWaitForOptions
                        {
                            State = WaitForSelectorState.Attached,
                            Timeout = 500
                        });
                        await ScrollIntoViewIfNeededSafeAsync(input);
                        await input.SetInputFilesAsync(paths);
                        try { await input.DispatchEventAsync("input"); } catch { }
                        try { await input.DispatchEventAsync("change"); } catch { }
                        await Task.Delay(500, cancellationToken);

                        var currentState = await ReadUploadFieldStateAsync(group);
                        if (!IsSameUploadFieldState(beforeState, currentState))
                        {
                            progress?.Report($"微信剧集上传：成本报告 multi-input 回退命中第 {index + 1} 个 input[type=file]。");
                            return true;
                        }
                    }
                    catch
                    {
                        // Try next input candidate.
                    }
                }
            }
            catch
            {
                // Try next locator collection.
            }
        }

        return false;
    }

    private static bool IsCostReportUploadAction(WeixinFormAction action)
    {
        if (!string.Equals(action.Type, "upload", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(action.Selector) &&
            action.Selector.Contains("成本配置比例情况报告", StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(action.Label?.Trim(), "剧目资质", StringComparison.Ordinal);
    }

    private static bool IsSameUploadFieldState(UploadFieldState left, UploadFieldState right)
    {
        return left.FileCount == right.FileCount &&
               left.PreviewCount == right.PreviewCount &&
               left.SuccessHintCount == right.SuccessHintCount &&
               left.HasProcessingText == right.HasProcessingText &&
               string.Equals(left.Text, right.Text, StringComparison.Ordinal);
    }

    private static async Task<string> DescribeUploadGroupStateAsync(ILocator group)
    {
        var state = await ReadUploadFieldStateAsync(group);
        var text = state.Text;
        if (text.Length > 180)
        {
            text = text[..180];
        }

        return $"files={state.FileCount}, previews={state.PreviewCount}, successHints={state.SuccessHintCount}, processing={state.HasProcessingText}, text={text}";
    }

    private static async Task SaveFirstPageActionFailureArtifactsAsync(
        IPage page,
        string outputDirectory,
        string actionType,
        string actionLabel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(outputDirectory);
        var safeLabel = SanitizeFileName(actionLabel);
        var stem = $"first-page-action-failed-{actionType}-{safeLabel}";

        try
        {
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(outputDirectory, $"{stem}.png"),
                FullPage = true
            });
        }
        catch
        {
            // Ignore screenshot failures.
        }

        try
        {
            var root = await ResolveDebugRootAsync(page);
            var html = await root.EvaluateAsync<string>("node => node.outerHTML ?? ''");
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, $"{stem}.html"), html, cancellationToken);
        }
        catch
        {
            // Ignore html failures.
        }

        try
        {
            var root = await ResolveDebugRootAsync(page);
            var text = await root.EvaluateAsync<string>("node => node.innerText ?? ''");
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, $"{stem}.txt"), text, cancellationToken);
        }
        catch
        {
            // Ignore text failures.
        }
    }

    private static async Task<ILocator> ResolveDebugRootAsync(IPage page)
    {
        var candidates = new[]
        {
            page.Locator("wujie-app").First,
            page.Locator(".micro-drama-post").First,
            page.Locator(".main-body-wrap").First,
            page.Locator("body").First
        };

        foreach (var candidate in candidates)
        {
            try
            {
                await candidate.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = 500
                });
                return candidate;
            }
            catch
            {
                // Try next root.
            }
        }

        return page.Locator("body").First;
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unnamed";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch)
            .ToArray();
        return new string(chars).Trim('-');
    }

    private static async Task<ILocator> ResolveUploadGroupAsync(
        IPage page,
        WeixinFormAction action,
        ILocator input,
        CancellationToken cancellationToken,
        FormGroupCache? cache = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(action.Label))
        {
            try
            {
                return await FindUploadGroupByLabelAsync(page, action, 2_000, cache);
            }
            catch
            {
                // Fall back to input ancestry below.
            }
        }

        var candidates = new[]
        {
            input.Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__control-group')][1]").First,
            input.Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__item')][1]").First,
            input.Locator("xpath=ancestor::*[contains(@class,'upload') or contains(@class,'uploader')][1]").First
        };

        try
        {
            return await FindFirstVisibleAsync(candidates, 1_500, WaitForSelectorState.Attached);
        }
        catch
        {
            return input;
        }
    }

    private static async Task WaitForUploadFieldCompletionAsync(
        UploadWaitProfile profile,
        ILocator group,
        UploadFieldState beforeState,
        IReadOnlyList<string> expectedPaths,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(profile.DeadlineSeconds);
        var observedProcessing = beforeState.HasProcessingText;
        var stableReadyRounds = 0;
        var lastState = beforeState;
        var expectedMarkers = expectedPaths
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .SelectMany(name => BuildFileNameMarkers(name!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = await ReadUploadFieldStateAsync(group);
            var markerStatus = await ReadExpectedUploadMarkerStatusAsync(group, expectedPaths);
            var hasExpectedMarkersInGroup = IsMarkerStatusCompleted(markerStatus);
            lastState = state;
            observedProcessing |= state.HasProcessingText;
            var expectedCount = markerStatus.ExpectedCount;
            var matchedExpectedFileName = expectedMarkers.Length > 0 &&
                                          expectedMarkers.Any(marker => state.Text.Contains(marker, StringComparison.OrdinalIgnoreCase));
            var previewTarget = expectedCount > 0
                ? Math.Max(expectedCount, beforeState.PreviewCount + expectedCount)
                : 0;

            if (expectedCount > 1)
            {
                var multiFileReady =
                    hasExpectedMarkersInGroup ||
                    (!state.HasProcessingText && state.PreviewCount >= previewTarget);
                if (multiFileReady)
                {
                    stableReadyRounds++;
                    if (!profile.RequireStableRounds || stableReadyRounds >= 2)
                    {
                        return;
                    }
                }
                else
                {
                    stableReadyRounds = 0;
                }

                await Task.Delay(250, cancellationToken);
                continue;
            }

            var showsUploadedFileUi =
                hasExpectedMarkersInGroup ||
                (state.SuccessHintCount > beforeState.SuccessHintCount &&
                 (markerStatus.HasUploadedUi ||
                  state.Text.Contains("重新选择", StringComparison.OrdinalIgnoreCase) ||
                  state.Text.Contains("删除", StringComparison.OrdinalIgnoreCase) ||
                  state.Text.Contains("移除", StringComparison.OrdinalIgnoreCase) ||
                  matchedExpectedFileName));

            var changed = state.FileCount > beforeState.FileCount ||
                          state.PreviewCount > beforeState.PreviewCount ||
                          state.SuccessHintCount > beforeState.SuccessHintCount ||
                          !string.Equals(state.Text, beforeState.Text, StringComparison.Ordinal);
            var ready = changed &&
                        (!profile.RequireProcessingToClear || !state.HasProcessingText);

            if (profile.AcceptOnFileSelection &&
                state.FileCount > beforeState.FileCount)
            {
                return;
            }

            if (ready)
            {
                stableReadyRounds++;
                if (!profile.RequireStableRounds || stableReadyRounds >= 2)
                {
                    return;
                }
            }
            else if (matchedExpectedFileName &&
                     state.SuccessHintCount > beforeState.SuccessHintCount)
            {
                stableReadyRounds++;
                if (!profile.RequireStableRounds || stableReadyRounds >= 2)
                {
                    return;
                }
            }
            else if (showsUploadedFileUi)
            {
                stableReadyRounds++;
                if (!profile.RequireStableRounds || stableReadyRounds >= 2)
                {
                    return;
                }
            }
            else
            {
                stableReadyRounds = 0;
            }

            if (!observedProcessing &&
                changed &&
                (state.PreviewCount > beforeState.PreviewCount || state.SuccessHintCount > beforeState.SuccessHintCount))
            {
                stableReadyRounds++;
                if (!profile.RequireStableRounds || stableReadyRounds >= 2)
                {
                    return;
                }
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException(
            $"上传分组状态在等待 {profile.DeadlineSeconds} 秒后仍未完成。files={lastState.FileCount}, previews={lastState.PreviewCount}, successHints={lastState.SuccessHintCount}, processing={lastState.HasProcessingText}, text={lastState.Text}");
    }

    private static async Task<bool> HasExpectedUploadMarkersAsync(
        ILocator group,
        IReadOnlyList<string> expectedPaths)
    {
        try
        {
            var markerStatus = await ReadExpectedUploadMarkerStatusAsync(group, expectedPaths);
            return IsMarkerStatusCompleted(markerStatus);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMarkerStatusCompleted(UploadExpectedMarkerStatus markerStatus)
    {
        return markerStatus.HasAllMatches ||
               (markerStatus.ExpectedCount <= 1 && markerStatus.HasUploadedUi);
    }

    private static async Task<UploadExpectedMarkerStatus> ReadExpectedUploadMarkerStatusAsync(
        ILocator group,
        IReadOnlyList<string> expectedPaths)
    {
        try
        {
            var probe = await group.EvaluateAsync<UploadMarkerProbe>(
                @"node => {
                    const root = node instanceof HTMLElement ? node : document.body;
                    const text = (root?.innerText ?? '').toLowerCase();
                    const links = Array.from(root?.querySelectorAll('a, [class*=""file-name""], [class*=""filename""], [class*=""upload-file-name""]') ?? [])
                        .map(item => (item.textContent ?? '').toLowerCase())
                        .filter(Boolean);
                    return { text, linkTexts: links };
                }");

            return WeixinUploadMarkerMatcher.Evaluate(probe.Text, probe.LinkTexts, expectedPaths);
        }
        catch
        {
            return WeixinUploadMarkerMatcher.Evaluate(string.Empty, Array.Empty<string>(), expectedPaths);
        }
    }

    private static async Task<bool> HasCostReportUploadSucceededOnPageAsync(
        IPage page,
        IReadOnlyList<string> expectedPaths,
        CancellationToken cancellationToken,
        ILocator? knownGroup = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // First try the group we already have (avoids re-scanning for "成本配置比例情况报告" text
        // when the upload container was resolved via a different label path).
        if (knownGroup is not null)
        {
            try
            {
                if (await HasExpectedUploadMarkersAsync(knownGroup, expectedPaths))
                {
                    return true;
                }
            }
            catch
            {
                // Group may have become stale; fall through to page-level re-resolve.
            }
        }

        var row = await ResolveCostReportUploadGroupAsync(page, cancellationToken);
        if (row is null)
        {
            return false;
        }

        return await HasExpectedUploadMarkersAsync(row, expectedPaths);
    }

    private static async Task WaitForCostReportUploadCompletionAsync(
        IPage page,
        IReadOnlyList<string> expectedPaths,
        ILocator? knownGroup,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        int timeoutSeconds = 60)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await HasCostReportUploadSucceededOnPageAsync(page, expectedPaths, cancellationToken, knownGroup))
            {
                progress?.Report("微信剧集上传：成本报告已在页面上显示文件名/重新选择，按上传完成继续。");
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("成本报告文件未在页面上显示上传完成状态。");
    }

    private static async Task WaitForVisualUploadCompletionAsync(
        IPage page,
        string label,
        IReadOnlyList<string> expectedPaths,
        int beforePageVisualCount,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var group = await ResolveVisualUploadGroupAsync(page, label, cancellationToken);
            var pageVisualCount = await ReadPageUploadVisualCountAsync(page);
            if ((group is not null && await HasExpectedUploadMarkersAsync(group, expectedPaths)) ||
                pageVisualCount > beforePageVisualCount)
            {
                progress?.Report($"微信剧集上传：{label} 已在页面上显示预览/上传完成状态。");
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"{label} 未在页面上显示预览或上传完成状态。");
    }

    private static async Task<bool> HasVisualUploadSucceededOnPageAsync(
        IPage page,
        string label,
        IReadOnlyList<string> expectedPaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var group = await ResolveVisualUploadGroupAsync(page, label, cancellationToken);
        if (group is null)
        {
            return false;
        }

        return await HasExpectedUploadMarkersAsync(group, expectedPaths);
    }

    private static async Task<ILocator?> ResolveProofMaterialUploadGroupAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        const string label = "剧目制作证明材料";
        var safe = EscapeSelectorText(label);
        var candidates = new ILocator[]
        {
            page.GetByText(label, new PageGetByTextOptions { Exact = false })
                .Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__item')][1]")
                .First,
            page.GetByText(label, new PageGetByTextOptions { Exact = false })
                .Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__control-group')][1]")
                .First,
            page.Locator($"xpath=//*[contains(normalize-space(.), \"{safe}\")]/ancestor::*[contains(@class,'weui-desktop-form__item')][1][.//input[@type='file'] or .//button or .//a]").First,
            page.Locator($"xpath=//*[contains(normalize-space(.), \"{safe}\")]/ancestor::*[contains(@class,'weui-desktop-form__control-group')][1][.//input[@type='file'] or .//button or .//a]").First
        };

        try
        {
            return await WaitForFirstCandidateAsync(
                candidates,
                800,
                WaitForSelectorState.Attached,
                "not found",
                scrollOnSuccess: false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WaitForFirstPageUploadsSettledAsync(
        IPage page,
        IReadOnlyList<WeixinFormAction> uploadActions,
        DateTimeOffset lastUploadCompletedAt,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var pending = uploadActions
            .Where(action => string.Equals(action.Type?.Trim(), "upload", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        var lastReportAt = DateTimeOffset.MinValue;
        var stableIdleRounds = 0;
        var minSettleWindow = TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var uploadStates = await ReadFirstPageUploadActionStatesAsync(page, pending, cancellationToken);
            var hasBusyVisuals = uploadStates.Any(state => state.LoadingCount > 0 || state.ProgressCount > 0);
            var settleWindowReached = lastUploadCompletedAt == DateTimeOffset.MinValue ||
                                      DateTimeOffset.UtcNow - lastUploadCompletedAt >= minSettleWindow;
            var allCompleted = uploadStates.All(state => state.Completed);

            if (settleWindowReached && !hasBusyVisuals && allCompleted)
            {
                stableIdleRounds++;
                if (stableIdleRounds >= 3)
                {
                    return;
                }
            }
            else
            {
                stableIdleRounds = 0;
            }

            if (DateTimeOffset.UtcNow - lastReportAt >= TimeSpan.FromSeconds(3))
            {
                var statusDetails = string.Join(" | ", uploadStates.Select(state => state.ToLogString()));
                progress?.Report(
                    $"微信剧集上传：等待第一页上传完成，" +
                    $"距最后上传已过={(lastUploadCompletedAt == DateTimeOffset.MinValue ? 0 : (int)(DateTimeOffset.UtcNow - lastUploadCompletedAt).TotalSeconds)}秒。 " +
                    $"详情：{statusDetails}");
                lastReportAt = DateTimeOffset.UtcNow;
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException("第一页上传项在预期时间内未全部完成。");
    }

    private static async Task<string> DescribeFirstPageUploadStatusesAsync(
        IPage page,
        IReadOnlyList<WeixinFormAction> uploadActions,
        CancellationToken cancellationToken)
    {
        var states = await ReadFirstPageUploadActionStatesAsync(page, uploadActions, cancellationToken);
        return states.Count == 0 ? "无" : string.Join(" | ", states.Select(state => state.ToLogString()));
    }

    private sealed record UploadActionRuntimeState(string Label, int LoadingCount, int ProgressCount, int PreviewCount, int SuccessHintCount, bool GroupMissing, bool Completed)
    {
        public string ToLogString()
            => GroupMissing
                ? $"{Label}=group-missing"
                : $"{Label}=loading={LoadingCount},progress={ProgressCount},preview={PreviewCount},successHints={SuccessHintCount},completed={(Completed ? 1 : 0)}";
    }

    private static async Task<IReadOnlyList<UploadActionRuntimeState>> ReadFirstPageUploadActionStatesAsync(
        IPage page,
        IReadOnlyList<WeixinFormAction> uploadActions,
        CancellationToken cancellationToken)
    {
        var results = new List<UploadActionRuntimeState>(uploadActions.Count);
        foreach (var action in uploadActions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var label = action.Label?.Trim();
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            ILocator? group = null;
            if (IsVisualUploadLabel(label))
            {
                group = await ResolveVisualUploadGroupAsync(page, label, cancellationToken);
            }
            else if (string.Equals(label, "剧目制作证明材料", StringComparison.Ordinal))
            {
                group = await ResolveProofMaterialUploadGroupAsync(page, cancellationToken);
            }
            else if (IsCostReportUploadAction(action))
            {
                group = await ResolveCostReportUploadGroupAsync(page, cancellationToken);
            }
            else
            {
                group = await FindUploadGroupByLabelAsyncSafe(page, action, 1_000);
            }

            if (group is null)
            {
                results.Add(new UploadActionRuntimeState(label, 0, 0, 0, 0, true, false));
                continue;
            }

            results.Add(await ReadUploadActionRuntimeStateAsync(label, group, action.Paths));
        }

        return results;
    }

    private static async Task<ILocator?> FindUploadGroupByLabelAsyncSafe(
        IPage page,
        WeixinFormAction action,
        int timeoutMs)
    {
        try
        {
            return await FindUploadGroupByLabelAsync(page, action, timeoutMs);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> DescribeUploadGroupRuntimeStateAsync(ILocator group)
    {
        var state = await ReadUploadActionRuntimeStateAsync("group", group, Array.Empty<string>());
        return state.ToLogString();
    }

    private static async Task<UploadActionRuntimeState> ReadUploadActionRuntimeStateAsync(string label, ILocator group, IReadOnlyList<string> expectedPaths)
    {
        try
        {
            var uploadState = await ReadUploadFieldStateAsync(group);
            var loading = await group.Locator(
                ".upload-loading:visible, .weui-desktop-loading:visible, [class*='uploading']:visible, [class*='loading']:visible").CountAsync();
            var progress = await group.Locator(
                "[class*='progress']:visible, .weui-desktop-upload__file__progress__wrp:visible, .weui-desktop-form__file-upload__progress__wrp:visible").CountAsync();
            var preview = await group.Locator(
                ".upload-image-item:visible, .image-preview:visible, .weui-desktop-upload__img:visible, .weui-desktop-upload__file:visible, .weui-desktop-upload__file__title:visible, [class*='preview'] img:visible, [class*='uploaded'] img:visible").CountAsync();
            var markerStatus = await ReadExpectedUploadMarkerStatusAsync(group, expectedPaths);
            var hasExpectedMarkers = IsMarkerStatusCompleted(markerStatus);
            var expectedCount = markerStatus.ExpectedCount;
            var completed =
                hasExpectedMarkers ||
                (!uploadState.HasProcessingText &&
                 (expectedCount > 1
                     ? uploadState.PreviewCount >= expectedCount
                     : uploadState.SuccessHintCount > 0 ||
                       uploadState.PreviewCount > 0 ||
                       uploadState.FileCount > 0 ||
                       markerStatus.HasUploadedUi));
            return new UploadActionRuntimeState(label, loading, progress, preview, uploadState.SuccessHintCount, false, completed);
        }
        catch
        {
            return new UploadActionRuntimeState(label, 0, 0, 0, 0, true, false);
        }
    }

    private static async Task<int> ReadPageUploadVisualCountAsync(IPage page)
    {
        try
        {
            return await page.Locator(
                ".upload-image-item:visible, .image-preview:visible, .weui-desktop-upload__img:visible, .weui-desktop-upload__file:visible, .weui-desktop-upload__file__title:visible, [class*='preview'] img:visible, [class*='uploaded'] img:visible").CountAsync();
        }
        catch
        {
            return 0;
        }
    }

    private static UploadWaitProfile GetUploadWaitProfile(WeixinFormAction action)
    {
        var label = action.Label?.Trim();
        var selector = action.Selector?.Trim();

        if (!string.IsNullOrWhiteSpace(label))
        {
            if (string.Equals(label, "剧目海报", StringComparison.Ordinal) ||
                string.Equals(label, "推广海报", StringComparison.Ordinal))
            {
                return new UploadWaitProfile(0, RequireProcessingToClear: false, RequireStableRounds: false, AcceptOnFileSelection: true, WaitForCompletion: false);
            }

            if (string.Equals(label, "剧目制作证明材料", StringComparison.Ordinal))
            {
                return new UploadWaitProfile(90, RequireProcessingToClear: false, RequireStableRounds: true, AcceptOnFileSelection: false, WaitForCompletion: true);
            }
        }

        if (IsCostReportUploadAction(action))
        {
            return new UploadWaitProfile(60, RequireProcessingToClear: false, RequireStableRounds: true, AcceptOnFileSelection: false, WaitForCompletion: true);
        }

        return new UploadWaitProfile(6, RequireProcessingToClear: false, RequireStableRounds: true, AcceptOnFileSelection: false, WaitForCompletion: true);
    }

    private static async Task<UploadFieldState> ReadUploadFieldStateAsync(ILocator group)
    {
        try
        {
            return await group.EvaluateAsync<UploadFieldState>(
                @"node => {
                    const root = node instanceof HTMLElement ? node : document.body;
                    const text = (root?.innerText ?? '').trim();
                    const lower = text.toLowerCase();
                    const fileCount = (() => {
                        const input = root?.querySelector('input[type=""file""]');
                        return input instanceof HTMLInputElement ? (input.files?.length ?? 0) : 0;
                    })();
                    const previewCount = root?.querySelectorAll(
                        'img, canvas, [class*=""preview""], [class*=""uploaded""], [class*=""success""], [class*=""file-item""], [class*=""uploader__file""], .upload-image-item, .image-preview, .weui-desktop-upload__img, .weui-desktop-upload__file, .weui-desktop-upload__file__title'
                    ).length ?? 0;
                    const fileNameCount = root?.querySelectorAll(
                        'a[href], [class*=""file-name""], [class*=""filename""], [class*=""upload-file-name""]'
                    ).length ?? 0;
                    const successHintCount = [
                        '上传成功',
                        '已上传',
                        '处理完成',
                        '重新选择',
                        '重新上传',
                        '删除',
                        '移除',
                        '预览'
                    ].reduce((count, keyword) => count + (text.includes(keyword) ? 1 : 0), 0);
                    const hasProcessingText = [
                        '上传中',
                        '处理中',
                        '上传处理中',
                        '校验中',
                        '请稍候'
                    ].some(keyword => text.includes(keyword));
                    return { fileCount, previewCount: previewCount + fileNameCount, successHintCount, hasProcessingText, text: lower };
                }");
        }
        catch
        {
            return new UploadFieldState(0, 0, 0, false, string.Empty);
        }
    }

    private static async Task<string> DescribeFileInputsAsync(ILocator group)
    {
        try
        {
            return await group.EvaluateAsync<string>(
                @"node => {
                    const root = node instanceof HTMLElement ? node : document.body;
                    const inputs = Array.from(root?.querySelectorAll('input[type=""file""]') ?? []);
                    const summary = inputs.slice(0, 5).map((input, index) => {
                        const accept = input.getAttribute('accept') || '';
                        const cls = input.getAttribute('class') || '';
                        const style = input.getAttribute('style') || '';
                        return `#${index}:class=${cls};accept=${accept};disabled=${input.disabled};style=${style}`;
                    }).join(' | ');
                    return `count=${inputs.length}${summary ? '; ' + summary : ''}`;
                }");
        }
        catch
        {
            return "count=unknown";
        }
    }

    private static async Task<string> DescribeVisualUploadGroupAsync(ILocator group)
    {
        try
        {
            return await group.EvaluateAsync<string>(
                @"node => {
                    const root = node instanceof HTMLElement ? node : document.body;
                    const inputs = Array.from(root?.querySelectorAll('input[type=""file""]') ?? []);
                    const buttons = Array.from(root?.querySelectorAll('button, .weui-desktop-btn, .webuploader-pick') ?? [])
                        .map(item => (item.textContent ?? '').trim())
                        .filter(Boolean)
                        .slice(0, 10);
                    const previews = root?.querySelectorAll('.upload-image-item, .image-preview, .weui-desktop-upload__img, .weui-desktop-upload__file').length ?? 0;
                    const text = (root?.innerText ?? '').trim().replace(/\s+/g, ' ').slice(0, 200);
                    const inputSummary = inputs.slice(0, 5).map((input, index) => {
                        const accept = input.getAttribute('accept') || '';
                        const cls = input.getAttribute('class') || '';
                        const style = input.getAttribute('style') || '';
                        return `#${index}:class=${cls};accept=${accept};disabled=${input.disabled};style=${style}`;
                    }).join(' | ');
                    return `count=${inputs.length}; previews=${previews}; buttons=${buttons.join('/')}; text=${text}; inputs=${inputSummary}`;
                }");
        }
        catch
        {
            return "visual-upload-diagnostics-unavailable";
        }
    }

    private static async Task<ILocator?> ResolveUploadInputFromSelectorFallbackAsync(
        IPage page,
        string selector,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var anchorText = ExtractContainsText(selector);
        if (string.IsNullOrWhiteSpace(anchorText))
        {
            return null;
        }

        var safe = EscapeSelectorText(anchorText);
        var candidates = new List<ILocator>
        {
            page.Locator($"xpath=//*[contains(normalize-space(.), \"{safe}\")]/following-sibling::*//input[@type='file']").First,
            page.Locator($"xpath=//*[contains(normalize-space(.), \"{safe}\")]/ancestor::*[contains(@class,'weui-desktop-form__control-group')][1]//input[@type='file']").First,
            page.Locator($"xpath=//*[contains(normalize-space(.), \"{safe}\")]/ancestor::*[contains(@class,'weui-desktop-form__item')][1]//input[@type='file']").First,
            page.GetByText(anchorText, new PageGetByTextOptions { Exact = false })
                .Locator("xpath=following-sibling::*//input[@type='file']").First,
            page.GetByText(anchorText, new PageGetByTextOptions { Exact = false })
                .Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__control-group')][1]//input[@type='file']").First,
            page.GetByText(anchorText, new PageGetByTextOptions { Exact = false })
                .Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__item')][1]//input[@type='file']").First
        };

        try
        {
            return await FindFirstVisibleAsync(candidates, 1_500, WaitForSelectorState.Attached);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractContainsText(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return null;
        }

        var singleQuoteMatch = Regex.Match(selector, @"contains\(\.,\s*'([^']+)'\)");
        if (singleQuoteMatch.Success)
        {
            return singleQuoteMatch.Groups[1].Value.Trim();
        }

        var doubleQuoteMatch = Regex.Match(selector, "contains\\(\\.,\\s*\\\"([^\\\"]+)\\\"\\)");
        if (doubleQuoteMatch.Success)
        {
            return doubleQuoteMatch.Groups[1].Value.Trim();
        }

        return null;
    }

    private static async Task<ILocator> FindUploadGroupByLabelAsync(
        IPage page,
        WeixinFormAction action,
        int timeoutMs,
        FormGroupCache? cache = null)
    {
        var label = action.Label?.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new InvalidOperationException("upload 动作缺少 label。");
        }

        var cacheKey = $"upload:{label}";
        if (cache is not null &&
            cache.TryGet(cacheKey, out var cached))
        {
            try
            {
                await cached.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 300
                });
                return cached;
            }
            catch
            {
                // Re-resolve below.
            }
        }

        Exception? lastError = null;
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        const int GroupQuickPollMs = 200;

        foreach (var group in EnumerateUploadGroupCandidates(page, label))
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                var remainingMs = (int)(deadline - DateTimeOffset.UtcNow).TotalMilliseconds;
                if (remainingMs <= 0)
                {
                    break;
                }

                try
                {
                    await group.WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Attached,
                        Timeout = Math.Min(GroupQuickPollMs, remainingMs)
                    });

                    var fileInputs = group.Locator("input[type='file']");
                    if (await fileInputs.CountAsync() == 0)
                    {
                        break;
                    }

                    if (!await HasUploadUiWithinAsync(group, action.Text, Math.Min(GroupQuickPollMs, remainingMs)))
                    {
                        break;
                    }

                    await ScrollIntoViewIfNeededSafeAsync(group);
                    cache?.Set(cacheKey, group);
                    return group;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }
        }

        throw lastError ?? new TimeoutException($"未找到上传字段: {label}");
    }

    private static IEnumerable<ILocator> EnumerateUploadGroupCandidates(IPage page, string label)
    {
        var safe = EscapeSelectorText(label);

        if (IsVisualUploadLabel(label))
        {
            yield return page.Locator($".weui-desktop-form__item:has(.weui-desktop-form__label:text-is(\"{safe}\")):has(.custom-image-upload, .weui-desktop-upload__img__btn, .upload-button-wrapper)").First;
            yield return page.Locator($".weui-desktop-form__item:has(.weui-desktop-form__label:has-text(\"{safe}\")):has(.custom-image-upload, .weui-desktop-upload__img__btn, .upload-button-wrapper)").First;
            yield return page.Locator($"xpath=//*[contains(@class,'weui-desktop-form__label')][contains(normalize-space(.), \"{safe}\")]/ancestor::*[contains(@class,'weui-desktop-form__item')][1][.//*[contains(@class,'custom-image-upload') or contains(@class,'weui-desktop-upload__img__btn') or contains(@class,'upload-button-wrapper')]]").First;
        }

        yield return page.Locator($".weui-desktop-form__item:has(.weui-desktop-form__label:text-is(\"{safe}\")):has(input[type='file'])").First;
        yield return page.Locator($".weui-desktop-form__item:has(.weui-desktop-form__label:text-is(\"{safe}\")):has(button)").First;
        yield return page.Locator($".weui-desktop-form__item:has(.weui-desktop-form__label:has-text(\"{safe}\")):has(input[type='file'])").First;
        yield return page.Locator($".weui-desktop-form__item:has(.weui-desktop-form__label:has-text(\"{safe}\")):has(button)").First;
        yield return page.Locator($".weui-desktop-form__control-group:has(.weui-desktop-form__label:text-is(\"{safe}\")):has(input[type='file'])").First;
        yield return page.Locator($".weui-desktop-form__control-group:has(.weui-desktop-form__label:text-is(\"{safe}\")):has(button)").First;
        yield return page.Locator($".weui-desktop-form__control-group:has(.weui-desktop-form__label:has-text(\"{safe}\")):has(input[type='file'])").First;
        yield return page.Locator($".weui-desktop-form__control-group:has(.weui-desktop-form__label:has-text(\"{safe}\")):has(button)").First;
        yield return page.Locator($"xpath=//*[contains(@class,'weui-desktop-form__label')][contains(normalize-space(.), \"{safe}\")]/ancestor::*[contains(@class,'weui-desktop-form__item')][1][.//input[@type='file'] or .//button]").First;
        yield return page.Locator($"xpath=//*[contains(@class,'weui-desktop-form__label')][contains(normalize-space(.), \"{safe}\")]/ancestor::*[contains(@class,'weui-desktop-form__control-group')][1][.//input[@type='file'] or .//button]").First;
    }

    private static bool IsVisualUploadLabel(string label)
    {
        return string.Equals(label, "剧目海报", StringComparison.Ordinal) ||
               string.Equals(label, "推广海报", StringComparison.Ordinal);
    }

    private static async Task<ILocator?> ResolveVisualUploadGroupAsync(
        IPage page,
        string label,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var safe = EscapeSelectorText(label);
        var candidates = new ILocator[]
        {
            page.GetByText(label, new PageGetByTextOptions { Exact = false })
                .Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__item')][1]")
                .First,
            page.GetByText(label, new PageGetByTextOptions { Exact = false })
                .Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__control-group')][1]")
                .First,
            page.Locator($".weui-desktop-form__item:has(.weui-desktop-form__label:text-is(\"{safe}\")):has(.custom-image-upload, .weui-desktop-upload__img__btn, .upload-button-wrapper)").First,
            page.Locator($".weui-desktop-form__item:has(.weui-desktop-form__label:has-text(\"{safe}\")):has(.custom-image-upload, .weui-desktop-upload__img__btn, .upload-button-wrapper)").First,
            page.Locator($"xpath=//*[contains(@class,'weui-desktop-form__label')][contains(normalize-space(.), \"{safe}\")]/ancestor::*[contains(@class,'weui-desktop-form__item')][1][.//*[contains(@class,'custom-image-upload') or contains(@class,'weui-desktop-upload__img__btn') or contains(@class,'upload-button-wrapper')]]").First
        };

        try
        {
            return await WaitForFirstCandidateAsync(
                candidates,
                800,
                WaitForSelectorState.Attached,
                "not found",
                scrollOnSuccess: false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> HasUploadUiWithinAsync(ILocator group, string? configuredText, int timeoutMs)
    {
        var candidates = new List<ILocator>();
        if (!string.IsNullOrWhiteSpace(configuredText))
        {
            candidates.Add(group.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = configuredText.Trim(), Exact = false }).First);
            candidates.Add(group.GetByText(configuredText.Trim(), new LocatorGetByTextOptions { Exact = false }).First);
        }

        candidates.AddRange(
        [
            group.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "选择", Exact = false }).First,
            group.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "选择文件", Exact = false }).First,
            group.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "选择图片", Exact = false }).First,
            group.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "上传文件", Exact = false }).First,
            group.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "重新选择", Exact = false }).First,
            group.Locator("button:has-text('选择'), button:has-text('选择文件'), button:has-text('选择图片'), button:has-text('上传文件'), button:has-text('重新选择')").First,
            group.Locator(".weui-desktop-btn:has-text('选择'), .weui-desktop-btn:has-text('选择文件'), .weui-desktop-btn:has-text('选择图片'), .weui-desktop-btn:has-text('上传文件'), .weui-desktop-btn:has-text('重新选择')").First
        ]);

        try
        {
            var locator = await WaitForFirstCandidateAsync(
                candidates,
                timeoutMs,
                WaitForSelectorState.Visible,
                "not found",
                scrollOnSuccess: false);
            return locator is not null;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ILocator> FindFirstVisibleAsync(
        IReadOnlyList<ILocator> candidates,
        int timeoutMs,
        WaitForSelectorState state = WaitForSelectorState.Visible)
    {
        return await WaitForFirstCandidateAsync(
            candidates,
            timeoutMs,
            state,
            "未找到可用控件。",
            scrollOnSuccess: state == WaitForSelectorState.Visible);
    }

    private static async Task ClickTextAsync(IPage page, string text, bool exact, int timeoutMs)
    {
        var locator = await FirstVisibleAsync(page, text, exact, timeoutMs);
        await locator.ClickAsync();
    }

    private static async Task<bool> MaybeClickTextAsync(IPage page, string text, bool exact, int timeoutMs)
    {
        try
        {
            await ClickTextAsync(page, text, exact, timeoutMs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> HasVisibleTextAsync(IPage page, string text, bool exact, int timeoutMs)
    {
        try
        {
            await FirstVisibleAsync(page, text, exact, timeoutMs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> HasVisibleTextWithinAsync(ILocator scope, string text, int timeoutMs)
    {
        var locators = new[]
        {
            scope.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = text, Exact = false }),
            scope.GetByText(text, new LocatorGetByTextOptions { Exact = false })
        };

        foreach (var locator in locators)
        {
            try
            {
                await WaitForVisibleCandidateAsync(locator, timeoutMs);
                return true;
            }
            catch
            {
                // Try next.
            }
        }

        return false;
    }

    private static async Task<ILocator?> TryFindVisibleWithinAsync(ILocator scope, string text, int timeoutMs)
    {
        var locators = new ILocator[]
        {
            scope.GetByText(text, new LocatorGetByTextOptions { Exact = false }),
            scope.GetByLabel(text, new LocatorGetByLabelOptions { Exact = false }),
            scope.GetByRole(AriaRole.Radio, new LocatorGetByRoleOptions { NameString = text, Exact = false }),
            scope.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { NameString = text, Exact = false })
        };

        try
        {
            return await WaitForFirstCandidateAsync(
                locators,
                timeoutMs,
                WaitForSelectorState.Visible,
                "not found",
                scrollOnSuccess: true);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ILocator> FirstVisibleAsync(IPage page, string text, bool exact, int timeoutMs)
    {
        var locators = new[]
        {
            page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = text, Exact = exact }),
            page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { NameString = text, Exact = exact }),
            page.GetByText(text, new PageGetByTextOptions { Exact = exact })
        };

        Exception? lastError = null;
        foreach (var locator in locators)
        {
            try
            {
                return await WaitForVisibleCandidateAsync(locator, timeoutMs);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException($"未找到可见文本: {text}");
    }

    private static async Task<ILocator> WaitForVisibleCandidateAsync(ILocator locator, int timeoutMs)
    {
        var count = await locator.CountAsync();
        var limit = Math.Max(1, Math.Min(count, 20));
        var candidates = new List<ILocator>(limit);
        for (var index = 0; index < limit; index++)
        {
            candidates.Add(locator.Nth(index));
        }

        return await WaitForFirstCandidateAsync(
            candidates,
            timeoutMs,
            WaitForSelectorState.Visible,
            "未找到可见候选项。",
            scrollOnSuccess: true);
    }

    private static async Task<bool> IsSecondPageReadyAsync(
        IPage page,
        WeixinSecondPageOptions secondPage,
        int timeoutMs)
    {
        try
        {
            await WaitForVisibleCandidateAsync(
                page.Locator(".weui-desktop-step.current:has(.weui-desktop-step__title:has-text('剧集文件选取'))").First,
                timeoutMs);
            return true;
        }
        catch
        {
            // Fall through to other second-page probes.
        }

        if (!string.IsNullOrWhiteSpace(secondPage.ReadyText))
        {
            try
            {
                await WaitForVisibleCandidateAsync(
                    page.Locator(".weui-desktop-step.current:has(.weui-desktop-step__title:has-text('剧集文件选取'))").First,
                    timeoutMs);
                await WaitForVisibleCandidateAsync(
                    page.GetByText(secondPage.ReadyText, new PageGetByTextOptions
                    {
                        Exact = false
                    }),
                    timeoutMs);
                return true;
            }
            catch
            {
                // Fall through to visible upload area detection.
            }
        }

        if (!string.IsNullOrWhiteSpace(secondPage.Upload.InputSelector))
        {
            try
            {
                await WaitForVisibleCandidateAsync(
                    page.Locator(".weui-desktop-step.current:has(.weui-desktop-step__title:has-text('剧集文件选取'))").First,
                    timeoutMs);

                var uploadInput = page.Locator(secondPage.Upload.InputSelector).First;
                await uploadInput.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = timeoutMs
                });

                var uploadGroup = uploadInput.Locator("xpath=ancestor::*[contains(@class,'weui-desktop-form__control-group') or contains(@class,'weui-desktop-uploader') or contains(@class,'upload')][1]");
                await uploadGroup.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = timeoutMs
                });
                await ScrollIntoViewIfNeededSafeAsync(uploadGroup);
                return true;
            }
            catch
            {
                // Ignore and report not ready below.
            }
        }

        return false;
    }

    private static async Task<bool> IsFirstPageReadyAsync(
        IPage page,
        WeixinFirstPageOptions? firstPage,
        int timeoutMs)
    {
        var quickSelectors = new[]
        {
            "input[placeholder*='剧目名称']",
            "textarea[placeholder*='剧目简介']",
            "input[placeholder*='推荐语']",
            "input[placeholder*='总集数']"
        };

        foreach (var selector in quickSelectors)
        {
            try
            {
                var locator = page.Locator(selector).First;
                await locator.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = timeoutMs
                });
                return true;
            }
            catch
            {
                // Try next selector.
            }
        }

        var quickLabels = firstPage?.ReadyLabels?.Count > 0
            ? firstPage.ReadyLabels.Take(3)
            : ["剧目名称", "剧目简介", "推荐语"];

        foreach (var label in quickLabels)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            try
            {
                await WaitForVisibleCandidateAsync(
                    page.Locator($".weui-desktop-form__label:text-is(\"{EscapeSelectorText(label)}\")"),
                    timeoutMs);
                return true;
            }
            catch
            {
                // Try next label.
            }
        }

        return false;
    }

    private static Task WaitBrieflyForLoadAsync(IPage page)
    {
        return Task.Delay(80);
    }

    // Sequential quick-poll: tries each candidate in order with a short per-candidate timeout.
    // This avoids accumulating background Playwright WaitForAsync tasks (which cannot be
    // externally cancelled) that would saturate the CDP message queue under the old Task.WhenAny
    // race approach.
    private static async Task<ILocator> WaitForFirstCandidateAsync(
        IReadOnlyList<ILocator> candidates,
        int timeoutMs,
        WaitForSelectorState state,
        string errorMessage,
        bool scrollOnSuccess)
    {
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(errorMessage);
        }

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        // Short per-candidate timeout: finds visible elements in ~50ms; skips hidden ones fast.
        const int QuickPollMs = 150;
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var candidate in candidates)
            {
                var remainingMs = (int)(deadline - DateTimeOffset.UtcNow).TotalMilliseconds;
                if (remainingMs <= 0)
                {
                    break;
                }

                try
                {
                    await candidate.WaitForAsync(new LocatorWaitForOptions
                    {
                        State = state,
                        Timeout = Math.Min(QuickPollMs, remainingMs)
                    });

                    if (scrollOnSuccess && state == WaitForSelectorState.Visible)
                    {
                        await ScrollIntoViewIfNeededSafeAsync(candidate);
                    }

                    return candidate;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }
        }

        throw lastError ?? new InvalidOperationException(errorMessage);
    }

    private static async Task ScrollIntoViewIfNeededSafeAsync(ILocator locator)
    {
        try
        {
            await locator.ScrollIntoViewIfNeededAsync();
            await Task.Delay(20);
        }
        catch
        {
            // Ignore scroll failures for hidden or detached nodes.
        }
    }

    private static async Task ClearActiveElementFocusAsync(IPage page)
    {
        try
        {
            await page.EvaluateAsync(
                @"() => {
                    const active = document.activeElement;
                    if (active instanceof HTMLElement) {
                        active.blur();
                    }

                    const body = document.body;
                    if (body instanceof HTMLElement) {
                        if (!body.hasAttribute('tabindex')) {
                            body.setAttribute('tabindex', '-1');
                        }

                        body.focus({ preventScroll: true });
                    }
                }");
        }
        catch
        {
            // Ignore focus-reset failures; this is a cosmetic cleanup only.
        }
    }

    private static string EscapeSelectorText(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
