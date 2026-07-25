using Microsoft.Playwright;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services.TikTok;

public static partial class TikTokBrowserActions
{
    private const int CopyrightControlTimeoutMs = 15000;
    private const int CopyrightUploadTimeoutMs = 60000;
    private const string CopyrightMaterialTypeFieldSelector =
        "[x-field-id='copyrightProof.selectedMaterialTypes']";
    private const string ProductionAgreementUploadFieldSelector =
        "[x-field-id='copyrightProof.materialFiles.2']";
    private const string CopyrightMaterialUploadFieldSelector =
        "[x-field-id^='copyrightProof.materialFiles.']";

    internal static async Task ConfigureCopyrightProofAsync(
        IPage page,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var configuredMaterialKeys = (options.CopyrightMaterialTypes ?? Array.Empty<string>())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var unknownMaterialKeys = configuredMaterialKeys
            .Where(key => !TikTokPublishConstants.CopyrightMaterialLabels.ContainsKey(key))
            .ToList();
        if (unknownMaterialKeys.Count > 0)
            throw new InvalidOperationException(
                $"TikTok 上传材料类型配置无效：{string.Join("、", unknownMaterialKeys)}。");

        if (configuredMaterialKeys.Count == 0)
            throw new InvalidOperationException("TikTok 上传材料类型未配置，请选择「制作协议、联合出品协议等合作协议」。");

        var supportedAutoUploadKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            TikTokPublishConstants.ProductionAgreementMaterialType,
            TikTokPublishConstants.SourceFileInformationMaterialType,
            TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
        };
        var unsupportedMaterialKeys = configuredMaterialKeys
            .Where(key => !supportedAutoUploadKeys.Contains(key))
            .ToList();
        if (unsupportedMaterialKeys.Count > 0)
        {
            var keysWithoutIndependentFiles = unsupportedMaterialKeys
                .Where(key => options.ResolveCopyrightMaterialFilePaths(key).Count == 0)
                .ToList();
            var labels = keysWithoutIndependentFiles
                .Select(key => TikTokPublishConstants.CopyrightMaterialLabels[key]);
            if (keysWithoutIndependentFiles.Count > 0)
                throw new InvalidOperationException(
                    $"已选择版权材料「{string.Join("、", labels)}」，但尚未配置对应的独立文件；" +
                    "证明材料.pdf 仅可上传到「制作协议、联合出品协议等合作协议」，" +
                    "原始文件截图仅可上传到「原始文件或素材文件信息」，" +
                    "AI 生成截图仅可上传到「AI 生成过程截图」。");

            var configuredLabels = unsupportedMaterialKeys
                .Select(key => TikTokPublishConstants.CopyrightMaterialLabels[key]);
            throw new NotSupportedException(
                $"版权材料「{string.Join("、", configuredLabels)}」已有独立文件，但当前自动上传流程尚未支持该类型；" +
                "请选择「制作协议、联合出品协议等合作协议」，以及可选的「原始文件或素材文件信息」/「AI 生成过程截图」。");
        }

        if (!configuredMaterialKeys.Contains(
                TikTokPublishConstants.ProductionAgreementMaterialType,
                StringComparer.Ordinal))
            throw new InvalidOperationException("TikTok 上传材料类型必须包含「制作协议、联合出品协议等合作协议」。");

        var includeSourceFileInformation = configuredMaterialKeys.Contains(
            TikTokPublishConstants.SourceFileInformationMaterialType,
            StringComparer.Ordinal);
        var sourceInfoFiles = includeSourceFileInformation
            ? options.ResolveCopyrightMaterialFilePaths(
                TikTokPublishConstants.SourceFileInformationMaterialType)
            : [];
        if (includeSourceFileInformation &&
            sourceInfoFiles.Count < TikTokSourceFileInfoScreenshotService.RequiredImageCount)
        {
            throw new FileNotFoundException(
                $"「原始文件或素材文件信息」需要至少 {TikTokSourceFileInfoScreenshotService.RequiredImageCount} 张截图，" +
                $"当前仅找到 {sourceInfoFiles.Count} 张；请先执行「生成证明材料」。");
        }

        var includeAiGenerationScreenshots = configuredMaterialKeys.Contains(
            TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
            StringComparer.Ordinal);
        var aiScreenshotFiles = includeAiGenerationScreenshots
            ? options.ResolveCopyrightMaterialFilePaths(
                TikTokPublishConstants.AiGenerationScreenshotsMaterialType)
            : [];
        if (includeAiGenerationScreenshots &&
            aiScreenshotFiles.Count < TikTokAiGenerationScreenshotService.RequiredImageCount)
        {
            throw new FileNotFoundException(
                $"「AI 生成过程截图」需要至少 {TikTokAiGenerationScreenshotService.RequiredImageCount} 张截图，" +
                $"当前仅找到 {aiScreenshotFiles.Count} 张；请先执行「生成证明材料」。");
        }

        var filePath = options.ResolveCopyrightMaterialFilePath(
            TikTokPublishConstants.ProductionAgreementMaterialType);
        if (string.IsNullOrWhiteSpace(filePath))
            throw new FileNotFoundException("未配置当前项目的 TikTok 证明材料文件路径。", filePath);

        var resolvedFilePath = Path.GetFullPath(filePath);
        if (!File.Exists(resolvedFilePath))
            throw new FileNotFoundException("当前项目的 TikTok 证明材料文件不存在。", resolvedFilePath);

        await SelectCopyrightRadioAsync(page, "是否原始权利人", options.IsOriginalRightsHolder ? "是" : "否", ct);
        await SelectCopyrightRadioAsync(
            page,
            "内容原创类型",
            string.Equals(options.ContentOriginalityType, "adapted", StringComparison.OrdinalIgnoreCase) ? "改编" : "原创",
            ct);

        var combo = await WaitForCopyrightMaterialTypeTriggerAsync(
            page,
            CopyrightControlTimeoutMs,
            ct);
        await combo.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 }).WaitAsync(ct);
        await OpenCopyrightMaterialTypePopupAsync(page, combo, ct);

        var productionAgreementLabel =
            TikTokPublishConstants.CopyrightMaterialLabels[TikTokPublishConstants.ProductionAgreementMaterialType];
        var sourceInfoLabel =
            TikTokPublishConstants.CopyrightMaterialLabels[TikTokPublishConstants.SourceFileInformationMaterialType];
        var aiScreenshotLabel =
            TikTokPublishConstants.CopyrightMaterialLabels[TikTokPublishConstants.AiGenerationScreenshotsMaterialType];
        var productionAgreementOption = await WaitForCopyrightMaterialCheckboxAsync(
            page,
            productionAgreementLabel,
            CopyrightControlTimeoutMs,
            ct);
        await EnsureCopyrightMaterialCheckboxStateAsync(
            page,
            productionAgreementLabel,
            productionAgreementOption,
            shouldSelect: true,
            log,
            ct);

        if (includeSourceFileInformation)
        {
            var sourceInfoOption = await WaitForCopyrightMaterialCheckboxAsync(
                page,
                sourceInfoLabel,
                CopyrightControlTimeoutMs,
                ct);
            await EnsureCopyrightMaterialCheckboxStateAsync(
                page,
                sourceInfoLabel,
                sourceInfoOption,
                shouldSelect: true,
                log,
                ct);
        }

        if (includeAiGenerationScreenshots)
        {
            var aiOption = await WaitForCopyrightMaterialCheckboxAsync(
                page,
                aiScreenshotLabel,
                CopyrightControlTimeoutMs,
                ct);
            await EnsureCopyrightMaterialCheckboxStateAsync(
                page,
                aiScreenshotLabel,
                aiOption,
                shouldSelect: true,
                log,
                ct);
        }

        // 页面可能保留上一次未提交的选择；清掉当前未配置的已知类型，避免出现无文件映射的上传框。
        foreach (var pair in TikTokPublishConstants.CopyrightMaterialLabels)
        {
            if (string.Equals(pair.Key, TikTokPublishConstants.ProductionAgreementMaterialType, StringComparison.Ordinal))
                continue;
            if (includeSourceFileInformation &&
                string.Equals(pair.Key, TikTokPublishConstants.SourceFileInformationMaterialType, StringComparison.Ordinal))
                continue;
            if (includeAiGenerationScreenshots &&
                string.Equals(pair.Key, TikTokPublishConstants.AiGenerationScreenshotsMaterialType, StringComparison.Ordinal))
                continue;
            ct.ThrowIfCancellationRequested();
            var option = await TryFindCopyrightMaterialCheckboxAsync(page, pair.Value);
            if (option is null || !await option.Value.Input.IsCheckedAsync()) continue;
            await EnsureCopyrightMaterialCheckboxStateAsync(
                page,
                pair.Value,
                option.Value,
                shouldSelect: false,
                log,
                ct);
        }
        await ClosePopupIfOpenAsync(page);

        var selectedParts = new List<string> { productionAgreementLabel };
        if (includeSourceFileInformation)
            selectedParts.Add(sourceInfoLabel);
        if (includeAiGenerationScreenshots)
            selectedParts.Add(aiScreenshotLabel);
        Log(log, $"TikTok 版权材料类型已确认：{string.Join("、", selectedParts)}。");

        await UploadCopyrightMaterialFilesAsync(
            page,
            productionAgreementLabel,
            [resolvedFilePath],
            preferProductionAgreementFieldId: true,
            log,
            ct);

        if (includeSourceFileInformation)
        {
            await UploadCopyrightMaterialFilesAsync(
                page,
                sourceInfoLabel,
                sourceInfoFiles.ToArray(),
                preferProductionAgreementFieldId: false,
                log,
                ct);
        }

        if (includeAiGenerationScreenshots)
        {
            await UploadCopyrightMaterialFilesAsync(
                page,
                aiScreenshotLabel,
                aiScreenshotFiles.ToArray(),
                preferProductionAgreementFieldId: false,
                log,
                ct);
        }
    }

    private static async Task UploadCopyrightMaterialFilesAsync(
        IPage page,
        string label,
        IReadOnlyList<string> filePaths,
        bool preferProductionAgreementFieldId,
        Action<string>? log,
        CancellationToken ct)
    {
        if (filePaths.Count == 0)
            throw new InvalidOperationException($"TikTok 版权材料「{label}」没有可上传的文件。");

        var uploadControl = await WaitForCopyrightMaterialUploadControlAsync(
            page,
            label,
            CopyrightControlTimeoutMs,
            preferProductionAgreementFieldId,
            ct);
        await uploadControl.Field.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 }).WaitAsync(ct);
        Log(log, $"TikTok 版权材料上传组件已就绪：{label}。");

        var displayNames = string.Join("、", filePaths.Select(Path.GetFileName));
        var initialFileCardCount = await CountCopyrightMaterialFileCardsAsync(uploadControl.Field);
        Log(log, $"TikTok 版权材料开始上传：{label}（{displayNames}）。");
        var networkOutcome = new TaskCompletionSource<CopyrightUploadNetworkOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnResponse(object? _, IResponse response)
        {
            if (!IsLikelyCopyrightUploadResponse(response)) return;
            if (response.Status is >= 200 and < 300)
            {
                networkOutcome.TrySetResult(new CopyrightUploadNetworkOutcome(
                    true,
                    $"上传请求已返回 HTTP {response.Status}"));
            }
            else if (response.Status >= 400)
            {
                networkOutcome.TrySetResult(new CopyrightUploadNetworkOutcome(
                    false,
                    $"上传请求返回 HTTP {response.Status}：{response.Url}"));
            }
        }

        page.Response += OnResponse;
        try
        {
            try
            {
                await uploadControl.Input
                    .SetInputFilesAsync(filePaths.ToArray(), new() { Timeout = 30000 })
                    .WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"向 TikTok「{label}」选择文件失败：{ex.Message}", ex);
            }

            await VerifyCopyrightMaterialFilesAcceptedAsync(
                uploadControl.Field,
                uploadControl.Input,
                filePaths.Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().ToArray(),
                initialFileCardCount,
                ct);
            Log(log, $"TikTok 版权材料文件已送入上传组件：{displayNames}，等待页面确认上传结果。");

            await WaitForCopyrightMaterialUploadResultAsync(
                uploadControl.Field,
                label,
                Path.GetFileName(filePaths[0]),
                initialFileCardCount,
                networkOutcome.Task,
                log,
                ct);
        }
        finally
        {
            page.Response -= OnResponse;
        }
        Log(log, $"TikTok 版权材料上传完成：{label}（{displayNames}）。");
    }

    private static async Task<(ILocator Input, ILocator ClickTarget)> WaitForCopyrightMaterialCheckboxAsync(
        IPage page,
        string label,
        int timeoutMs,
        CancellationToken ct)
    {
        (ILocator Input, ILocator ClickTarget)? result = null;
        var found = await WaitUntilAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            result = await TryFindCopyrightMaterialCheckboxAsync(page, label);
            return result is not null;
        }, timeoutMs, 300, ct);

        return found && result is not null
            ? result.Value
            : throw new InvalidOperationException(
                $"TikTok「上传材料类型」已打开，但 {timeoutMs / 1000} 秒内未找到可操作的「{label}」复选框。");
    }

    private static async Task<(ILocator Input, ILocator ClickTarget)?> TryFindCopyrightMaterialCheckboxAsync(
        IPage page,
        string label)
    {
        var literal = XPathLiteral(label);
        var exactLabels = page.Locator(
            $"xpath=//*[normalize-space(translate(text(), '*', ''))={literal} and " +
            "ancestor::*[@role='tooltip' or @role='dialog' or " +
            "contains(concat(' ', normalize-space(@class), ' '), ' semi-portal ')]]");
        if (await exactLabels.CountAsync() == 0)
        {
            var popupRoots = page.Locator("[role='tooltip'], [role='dialog'], .semi-portal");
            exactLabels = popupRoots.GetByText(label, new() { Exact = true });
        }
        var count = await exactLabels.CountAsync();
        for (var index = count - 1; index >= 0; index--)
        {
            try
            {
                var exactLabel = exactLabels.Nth(index);
                if (!await exactLabel.IsVisibleAsync()) continue;

                var clickTarget = exactLabel.Locator(
                    "xpath=ancestor-or-self::*[self::label or @role='checkbox' or " +
                    "contains(concat(' ', normalize-space(@class), ' '), ' semi-checkbox ')][1]");
                if (await clickTarget.CountAsync() == 0)
                    clickTarget = exactLabel.Locator("xpath=ancestor::*[.//input[@type='checkbox']][1]");
                if (await clickTarget.CountAsync() == 0 || !await clickTarget.IsVisibleAsync()) continue;

                var input = clickTarget.Locator("input[type='checkbox']").First;
                if (await input.CountAsync() == 0) continue;
                if (!await input.EvaluateAsync<bool>("element => element.isConnected && !element.disabled")) continue;
                return (input, clickTarget);
            }
            catch
            {
                // 下拉层可能在轮询期间重绘，下一轮重新定位。
            }
        }

        return null;
    }

    private static async Task EnsureCopyrightMaterialCheckboxStateAsync(
        IPage page,
        string label,
        (ILocator Input, ILocator ClickTarget) option,
        bool shouldSelect,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var isSelected = await option.Input.IsCheckedAsync();
        if (isSelected == shouldSelect)
        {
            if (shouldSelect)
                Log(log, $"TikTok 版权材料已保持勾选：{label}。");
            return;
        }

        await ClickCopyrightMaterialOptionAsync(option, ct);
        var confirmed = await WaitUntilAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var current = await TryFindCopyrightMaterialCheckboxAsync(page, label);
            return current is not null && await current.Value.Input.IsCheckedAsync() == shouldSelect;
        }, 5000, 200, ct);
        if (!confirmed)
            throw new InvalidOperationException(
                $"TikTok 版权材料「{label}」复选框状态设置失败，期望：{(shouldSelect ? "勾选" : "取消勾选")}。");

        Log(log, shouldSelect
            ? $"TikTok 版权材料已精确勾选：{label}。"
            : $"TikTok 已取消页面残留的版权材料选择：{label}。");
    }

    private static async Task<(ILocator Field, ILocator Input)> WaitForCopyrightMaterialUploadControlAsync(
        IPage page,
        string label,
        int timeoutMs,
        bool preferProductionAgreementFieldId,
        CancellationToken ct)
    {
        (ILocator Field, ILocator Input)? result = null;
        var found = await WaitUntilAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            result = await TryFindCopyrightMaterialUploadControlAsync(
                page,
                label,
                preferProductionAgreementFieldId);
            return result is not null;
        }, timeoutMs, 300, ct);

        return found && result is not null
            ? result.Value
            : throw new InvalidOperationException(
                $"已勾选版权材料「{label}」，但 {timeoutMs / 1000} 秒内未出现可见的文件上传组件。");
    }

    private static async Task<(ILocator Field, ILocator Input)?> TryFindCopyrightMaterialUploadControlAsync(
        IPage page,
        string label,
        bool preferProductionAgreementFieldId)
    {
        if (preferProductionAgreementFieldId)
        {
            var fieldBasedControl = await TryFindCopyrightMaterialUploadControlByFieldIdAsync(page);
            if (fieldBasedControl is not null)
                return fieldBasedControl;
        }

        var exactLabels = page.Locator(
            $"xpath=//*[normalize-space(translate(text(), '*', ''))={XPathLiteral(label)}]");
        if (await exactLabels.CountAsync() == 0)
            exactLabels = page.GetByText(label, new() { Exact = true });
        var count = await exactLabels.CountAsync();
        for (var index = count - 1; index >= 0; index--)
        {
            try
            {
                var exactLabel = exactLabels.Nth(index);
                if (!await exactLabel.IsVisibleAsync()) continue;

                var field = exactLabel.Locator("xpath=ancestor::*[.//input[@type='file']][1]");
                if (await field.CountAsync() == 0 || !await field.IsVisibleAsync()) continue;

                var input = field.Locator("input[type='file']").First;
                if (await input.CountAsync() == 0) continue;
                if (!await input.EvaluateAsync<bool>("element => element.isConnected && !element.disabled")) continue;
                return (field, input);
            }
            catch
            {
                // 选择材料后上传组件会异步重绘，下一轮重新定位。
            }
        }

        return null;
    }

    private static async Task<ILocator> WaitForCopyrightMaterialTypeTriggerAsync(
        IPage page,
        int timeoutMs,
        CancellationToken ct)
    {
        ILocator? result = null;
        var found = await WaitUntilAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            result = await TryFindCopyrightMaterialTypeTriggerAsync(page);
            return result is not null;
        }, timeoutMs, 300, ct);

        return found && result is not null
            ? result
            : throw new InvalidOperationException(
                $"{timeoutMs / 1000} 秒内未找到 TikTok「上传材料类型」触发器。");
    }

    private static async Task<ILocator?> TryFindCopyrightMaterialTypeTriggerAsync(IPage page)
    {
        // 2026-07 的真实编辑页不是 button[role=combobox]，而是
        // copyrightProof.selectedMaterialTypes 字段内的 div[aria-haspopup=true]。
        // 优先使用稳定的表单字段标识，保留旧版按钮定位作为兼容回退。
        foreach (var selector in new[]
                 {
                     $"{CopyrightMaterialTypeFieldSelector} [aria-haspopup='true']",
                     $"{CopyrightMaterialTypeFieldSelector} [tabindex='0']",
                 })
        {
            var candidate = page.Locator(selector).First;
            try
            {
                if (await candidate.CountAsync() == 0 || !await candidate.IsVisibleAsync())
                    continue;
                if (!await candidate.EvaluateAsync<bool>(
                        "element => element.isConnected && " +
                        "!element.hasAttribute('disabled') && element.getAttribute('aria-disabled') !== 'true'"))
                    continue;
                return candidate;
            }
            catch
            {
                // 页面异步重绘时重新定位或走旧版回退。
            }
        }

        return await FindComboboxByFieldLabelAsync(page, ["上传材料类型"]);
    }

    private static async Task OpenCopyrightMaterialTypePopupAsync(
        IPage page,
        ILocator trigger,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.Equals(
                await trigger.GetAttributeAsync("aria-expanded"),
                "true",
                StringComparison.OrdinalIgnoreCase))
            return;

        await ClickWithFallbackAsync(trigger, ct);
        await page.WaitForTimeoutAsync(400);
    }

    private static async Task ClickCopyrightMaterialOptionAsync(
        (ILocator Input, ILocator ClickTarget) option,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // 当前页面的 checkbox 与文字分列，ClickTarget 是共同的 option 容器；
            // 直接触发容器内 Semi checkbox，避免浮层贴近视口边缘时坐标点击超时。
            await option.ClickTarget.EvaluateAsync(
                """
                element => {
                  const target = element.matches('.semi-checkbox, input[type="checkbox"]')
                    ? element
                    : element.querySelector('.semi-checkbox') ||
                      element.querySelector('input[type="checkbox"]') ||
                      element;
                  target.click();
                }
                """).WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            await ClickWithFallbackAsync(option.ClickTarget, ct);
        }
    }

    private static async Task<(ILocator Field, ILocator Input)?>
        TryFindCopyrightMaterialUploadControlByFieldIdAsync(IPage page)
    {
        foreach (var fieldSelector in new[]
                 {
                     ProductionAgreementUploadFieldSelector,
                     CopyrightMaterialUploadFieldSelector,
                 })
        {
            var fields = page.Locator(fieldSelector);
            int count;
            try { count = await fields.CountAsync(); }
            catch { continue; }

            for (var index = 0; index < count; index++)
            {
                var field = fields.Nth(index);
                try
                {
                    if (!await field.IsVisibleAsync()) continue;

                    foreach (var inputSelector in new[]
                             {
                                 "input.semi-upload-hidden-input:not(.semi-upload-hidden-input-replace)[accept*='.pdf']",
                                 "input[type='file'][multiple][accept*='.pdf']",
                                 "input[type='file'][accept*='.pdf']",
                                 "input[type='file']",
                             })
                    {
                        var input = field.Locator(inputSelector).First;
                        if (await input.CountAsync() == 0) continue;
                        if (!await input.EvaluateAsync<bool>(
                                "element => element.isConnected && !element.disabled"))
                            continue;
                        return (field, input);
                    }
                }
                catch
                {
                    // 勾选材料后上传区域会异步创建或重绘，下一轮重新定位。
                }
            }
        }

        return null;
    }

    private static async Task VerifyCopyrightMaterialFilesAcceptedAsync(
        ILocator field,
        ILocator input,
        IReadOnlyList<string> expectedFileNames,
        int initialFileCardCount,
        CancellationToken ct)
    {
        if (expectedFileNames.Count == 0)
            throw new InvalidOperationException("版权材料文件校验失败：期望文件名为空。");

        if (expectedFileNames.Count == 1)
        {
            await VerifyCopyrightMaterialFileAcceptedAsync(
                field,
                input,
                expectedFileNames[0],
                initialFileCardCount,
                ct);
            return;
        }

        var renderedFileCardCount = initialFileCardCount;
        var matched = await WaitUntilAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                renderedFileCardCount = await CountCopyrightMaterialFileCardsAsync(field);
                return renderedFileCardCount >= initialFileCardCount + expectedFileNames.Count;
            }
            catch
            {
                return false;
            }
        }, 8000, 150, ct);

        if (!matched)
        {
            throw new InvalidOperationException(
                $"TikTok 版权材料多文件选择校验失败，期望 {expectedFileNames.Count} 个文件；" +
                $"文件卡片：{initialFileCardCount} → {renderedFileCardCount}。");
        }
    }

    private static async Task VerifyCopyrightMaterialFileAcceptedAsync(
        ILocator field,
        ILocator input,
        string expectedFileName,
        int initialFileCardCount,
        CancellationToken ct)
    {
        string[] actualFileNames = [];
        var renderedFileCardCount = initialFileCardCount;
        var matched = await WaitUntilAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                actualFileNames = await input.EvaluateAsync<string[]>(
                    "element => Array.from(element.files || []).map(file => file.name)");
                if (actualFileNames.Any(name =>
                        string.Equals(name, expectedFileName, StringComparison.OrdinalIgnoreCase)))
                    return true;

                // Semi Upload 接收文件后会立刻重建 input 并清空 input.files；
                // 此时真实接收结果是上传区出现 PDF/file card，而不是旧 input 继续持有 File。
                renderedFileCardCount = await CountCopyrightMaterialFileCardsAsync(field);
                return renderedFileCardCount > initialFileCardCount;
            }
            catch
            {
                return false;
            }
        }, 5000, 150, ct);

        if (!matched)
        {
            var actual = actualFileNames.Length == 0 ? "空" : string.Join("、", actualFileNames);
            throw new InvalidOperationException(
                $"TikTok 版权材料文件选择校验失败，期望：{expectedFileName}；" +
                $"input.files：{actual}；文件卡片：{initialFileCardCount} → {renderedFileCardCount}。");
        }
    }

    private static Task<int> CountCopyrightMaterialFileCardsAsync(ILocator field) =>
        field.EvaluateAsync<int>(
            """
            root => {
              const listCards = root.querySelectorAll(
                '.semi-upload-file-list-main[role="list"] > *, ' +
                '.semi-upload-file-list-main [role="list"] > *');
              if (listCards.length > 0) return listCards.length;
              return root.querySelectorAll('[class*="pictureCard"]').length;
            }
            """);

    private static async Task WaitForCopyrightMaterialUploadResultAsync(
        ILocator field,
        string label,
        string fileName,
        int initialFileCardCount,
        Task<CopyrightUploadNetworkOutcome> networkOutcomeTask,
        Action<string>? log,
        CancellationToken ct)
    {
        string? failure = null;
        string lastProbe = "pending:尚未读取页面状态";
        string? lastLoggedProbeKind = null;
        var stableReadyCount = 0;

        var finished = await WaitUntilAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                lastProbe = await ProbeCopyrightMaterialUploadStateAsync(
                    field,
                    fileName,
                    initialFileCardCount);
            }
            catch (Exception ex)
            {
                lastProbe = $"pending:上传区域正在重绘（{ex.Message}）";
            }

            var separator = lastProbe.IndexOf(':');
            var kind = separator < 0 ? lastProbe : lastProbe[..separator];
            var detail = separator < 0 ? lastProbe : lastProbe[(separator + 1)..];
            if (networkOutcomeTask.IsCompletedSuccessfully)
            {
                var networkOutcome = networkOutcomeTask.Result;
                if (!networkOutcome.Success)
                {
                    failure = networkOutcome.Detail;
                    return true;
                }
            }
            if (string.Equals(kind, "error", StringComparison.Ordinal))
            {
                failure = detail;
                return true;
            }

            if (string.Equals(kind, "success", StringComparison.Ordinal))
                return true;

            if (string.Equals(kind, "ready", StringComparison.Ordinal))
            {
                stableReadyCount++;
                if (networkOutcomeTask.IsCompletedSuccessfully && networkOutcomeTask.Result.Success)
                    return true;

                // 少数版本不暴露上传请求或显式成功状态。此时至少持续观察 10 秒，确认文件卡片
                // 始终存在且没有出现上传中/错误状态，避免仅凭 change 后立即出现的文件名误判。
                if (stableReadyCount >= 20)
                {
                    Log(log, "TikTok 版权材料未暴露明确网络完成事件，已通过 10 秒稳定文件卡片确认。");
                    return true;
                }
            }
            else
            {
                stableReadyCount = 0;
            }

            if (!string.Equals(lastLoggedProbeKind, kind, StringComparison.Ordinal))
            {
                lastLoggedProbeKind = kind;
                Log(log, $"TikTok 版权材料上传状态：{detail}。");
            }
            return false;
        }, CopyrightUploadTimeoutMs, 500, ct);

        if (failure is not null)
            throw new InvalidOperationException(
                $"TikTok 版权材料「{label}」上传失败：{failure}");
        if (!finished)
            throw new TimeoutException(
                $"等待 TikTok 版权材料「{label}」上传完成超时（{CopyrightUploadTimeoutMs / 1000} 秒）；" +
                $"最后状态：{lastProbe}。");
    }

    private static Task<string> ProbeCopyrightMaterialUploadStateAsync(
        ILocator field,
        string fileName,
        int initialFileCardCount) =>
        field.EvaluateAsync<string>(
            """
            (root, args) => {
              const { expectedFileName, initialFileCardCount } = args;
              const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
              const lower = value => normalize(value).toLowerCase();
              const isVisible = element => {
                if (!(element instanceof Element)) return false;
                const style = getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                return style.display !== 'none' &&
                  style.visibility !== 'hidden' &&
                  Number(style.opacity || '1') > 0 &&
                  rect.width > 0 && rect.height > 0;
              };
              const describe = element => normalize(
                [element.innerText, element.getAttribute?.('title'),
                  element.getAttribute?.('aria-label'), element.getAttribute?.('data-file-name')]
                  .filter(Boolean).join(' '));
              const elements = [root, ...root.querySelectorAll('*')].filter(isVisible);
              const fieldText = lower(elements.map(describe).join(' '));
              const expected = lower(expectedFileName);

              const alerts = [...root.ownerDocument.querySelectorAll(
                "[role='alert'], .semi-toast-error, .semi-notification-error, .semi-banner-error")]
                .filter(isVisible);
              const alertText = normalize(alerts.map(describe).join(' '));
              const combinedErrorText = lower(`${fieldText} ${alertText}`);
              const errorMarkers = [
                '上传失败', '文件上传失败', '格式不支持', '文件类型不支持', '文件过大',
                '超出大小', '网络错误', 'upload failed', 'unsupported file', 'invalid file'
              ];
              const matchedError = errorMarkers.find(marker => combinedErrorText.includes(marker));
              const errorElement = elements.find(element => {
                const className = lower(typeof element.className === 'string' ? element.className : '');
                return /(?:upload.*error|error.*upload|upload.*fail|fail.*upload)/.test(className);
              });
              if (matchedError || errorElement) {
                const detail = alertText || (errorElement ? describe(errorElement) : '') || matchedError || '页面显示上传错误状态';
                return `error:${detail.slice(0, 300)}`;
              }

              const progressElements = [...root.querySelectorAll("[role='progressbar'], progress")]
                .filter(isVisible);
              const incompleteProgress = progressElements.some(element => {
                const raw = element.getAttribute('aria-valuenow') ?? element.value;
                const value = Number(raw);
                return Number.isFinite(value) && value < 100;
              });
              const busyMarkers = ['上传中', '正在上传', '处理中', 'uploading', 'processing'];
              const busyMarker = busyMarkers.find(marker => fieldText.includes(marker));
              const busyElement = elements.find(element => {
                const className = lower(typeof element.className === 'string' ? element.className : '');
                return /(?:uploading|upload.*loading|loading.*upload)/.test(className);
              });
              if (incompleteProgress || busyMarker || busyElement)
                return `busy:${busyMarker || '文件正在上传'}`;

              const fileShown = expected.length > 0 && fieldText.includes(expected);
              const listCards = root.querySelectorAll(
                '.semi-upload-file-list-main[role="list"] > *, ' +
                '.semi-upload-file-list-main [role="list"] > *');
              const fallbackCards = root.querySelectorAll('[class*="pictureCard"]');
              const fileCardCount = listCards.length > 0 ? listCards.length : fallbackCards.length;
              const newFileCardShown = fileCardCount > initialFileCardCount;
              const successMarkers = ['上传成功', '已上传', 'upload success', 'uploaded'];
              const successMarker = successMarkers.find(marker => fieldText.includes(marker));
              const successfulElement = elements.find(element => {
                const className = lower(typeof element.className === 'string' ? element.className : '');
                return /(?:upload.*success|success.*upload|upload.*finished|upload.*complete)/.test(className);
              });
              if ((fileShown || newFileCardShown) && (successMarker || successfulElement))
                return `success:${expectedFileName} 已显示且页面无上传中或错误状态`;
              if (fileShown || newFileCardShown)
                return `ready:${fileShown ? expectedFileName : 'PDF 文件卡片'} 已显示且页面无上传中或错误状态`;
              return `pending:等待上传区域显示 ${expectedFileName}`;
            }
            """,
            new { expectedFileName = fileName, initialFileCardCount });

    private static bool IsLikelyCopyrightUploadResponse(IResponse response)
    {
        var method = response.Request.Method;
        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var url = response.Url.ToLowerInvariant();
        var urlLooksRelevant = url.Contains("upload", StringComparison.Ordinal) ||
                               url.Contains("material", StringComparison.Ordinal) ||
                               url.Contains("copyright", StringComparison.Ordinal);
        response.Request.Headers.TryGetValue("content-type", out var contentType);
        var normalizedContentType = (contentType ?? string.Empty).ToLowerInvariant();
        var bodyLooksLikeFile = normalizedContentType.Contains("multipart/form-data", StringComparison.Ordinal) ||
                                normalizedContentType.Contains("application/octet-stream", StringComparison.Ordinal) ||
                                normalizedContentType.Contains("application/pdf", StringComparison.Ordinal);
        // 成功只接受真正携带文件体的请求；带 upload/material 字样的签名或预检 JSON
        // 不能代表对象存储已完成。相关接口的 HTTP 错误仍应尽早暴露。
        return bodyLooksLikeFile || (urlLooksRelevant && response.Status >= 400);
    }

    private sealed record CopyrightUploadNetworkOutcome(bool Success, string Detail);

    private static async Task SelectCopyrightRadioAsync(IPage page, string fieldLabel, string optionLabel, CancellationToken ct)
    {
        var selected = await page.EvaluateAsync<bool>(
            """
            ({ fieldLabel, optionLabel }) => {
              const normalize = value => (value || '').replace(/\s+/g, '').replace(/\*/g, '');
              const headings = Array.from(document.querySelectorAll('body *'))
                .filter(node => normalize(node.textContent) === normalize(fieldLabel));
              for (const heading of headings) {
                let root = heading;
                for (let i = 0; i < 5 && root; i++, root = root.parentElement) {
                  const radios = Array.from(root.querySelectorAll('input[type=radio]'));
                  if (!radios.length) continue;
                  const target = radios.find(radio => {
                    const label = radio.closest('label');
                    return normalize(label?.textContent) === normalize(optionLabel);
                  });
                  if (!target) continue;
                  if (!target.checked) (target.closest('label') || target).click();
                  return true;
                }
              }
              return false;
            }
            """,
            new { fieldLabel, optionLabel });
        ct.ThrowIfCancellationRequested();
        if (!selected)
            throw new InvalidOperationException($"未找到 TikTok「{fieldLabel}」的「{optionLabel}」选项。");
        await page.WaitForTimeoutAsync(150);
    }
}
