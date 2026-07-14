using Microsoft.Playwright;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Ui.Services.TikTok;

public static partial class TikTokBrowserActions
{
    private const int CopyrightControlTimeoutMs = 15000;
    private const int CopyrightUploadTimeoutMs = 60000;

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

        var unsupportedMaterialKeys = configuredMaterialKeys
            .Where(key => !string.Equals(
                key,
                TikTokPublishConstants.ProductionAgreementMaterialType,
                StringComparison.Ordinal))
            .ToList();
        if (unsupportedMaterialKeys.Count > 0)
        {
            var keysWithoutIndependentFiles = unsupportedMaterialKeys
                .Where(key => string.IsNullOrWhiteSpace(options.ResolveCopyrightMaterialFilePath(key)))
                .ToList();
            var labels = keysWithoutIndependentFiles
                .Select(key => TikTokPublishConstants.CopyrightMaterialLabels[key]);
            if (keysWithoutIndependentFiles.Count > 0)
                throw new InvalidOperationException(
                    $"已选择版权材料「{string.Join("、", labels)}」，但尚未配置对应的独立文件；" +
                    "证明材料.pdf 仅可上传到「制作协议、联合出品协议等合作协议」。");

            var configuredLabels = unsupportedMaterialKeys
                .Select(key => TikTokPublishConstants.CopyrightMaterialLabels[key]);
            throw new NotSupportedException(
                $"版权材料「{string.Join("、", configuredLabels)}」已有独立文件，但当前自动上传流程尚未支持该类型；" +
                "请仅选择「制作协议、联合出品协议等合作协议」。");
        }

        if (!configuredMaterialKeys.Contains(
                TikTokPublishConstants.ProductionAgreementMaterialType,
                StringComparer.Ordinal))
            throw new InvalidOperationException("TikTok 上传材料类型必须包含「制作协议、联合出品协议等合作协议」。");

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

        var combo = await FindComboboxByFieldLabelAsync(page, ["上传材料类型"])
            ?? throw new InvalidOperationException("未找到 TikTok「上传材料类型」下拉框。");
        await combo.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 }).WaitAsync(ct);
        await OpenComboboxAsync(page, combo, ct);

        var productionAgreementLabel =
            TikTokPublishConstants.CopyrightMaterialLabels[TikTokPublishConstants.ProductionAgreementMaterialType];
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

        // 页面可能保留上一次未提交的选择；清掉当前未配置的已知类型，避免出现无文件映射的上传框。
        foreach (var pair in TikTokPublishConstants.CopyrightMaterialLabels)
        {
            if (string.Equals(
                    pair.Key,
                    TikTokPublishConstants.ProductionAgreementMaterialType,
                    StringComparison.Ordinal)) continue;
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

        Log(log, $"TikTok 版权材料类型已确认：{productionAgreementLabel}。");
        var uploadControl = await WaitForCopyrightMaterialUploadControlAsync(
            page,
            productionAgreementLabel,
            CopyrightControlTimeoutMs,
            ct);
        await uploadControl.Field.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 }).WaitAsync(ct);
        Log(log, $"TikTok 版权材料上传组件已就绪：{productionAgreementLabel}。");

        var fileName = Path.GetFileName(resolvedFilePath);
        Log(log, $"TikTok 版权材料开始上传：{productionAgreementLabel}（{fileName}）。");
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
                    .SetInputFilesAsync(resolvedFilePath, new() { Timeout = 30000 })
                    .WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"向 TikTok「{productionAgreementLabel}」选择文件失败：{ex.Message}", ex);
            }

            await VerifyCopyrightInputFileNameAsync(uploadControl.Input, fileName, ct);
            Log(log, $"TikTok 版权材料文件已送入上传控件：{fileName}，等待页面确认上传结果。");

            await WaitForCopyrightMaterialUploadResultAsync(
                uploadControl.Field,
                productionAgreementLabel,
                fileName,
                networkOutcome.Task,
                log,
                ct);
        }
        finally
        {
            page.Response -= OnResponse;
        }
        Log(log, $"TikTok 版权材料上传完成：{productionAgreementLabel}（{fileName}）。");
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

        await ClickWithFallbackAsync(option.ClickTarget, ct);
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
        CancellationToken ct)
    {
        (ILocator Field, ILocator Input)? result = null;
        var found = await WaitUntilAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            result = await TryFindCopyrightMaterialUploadControlAsync(page, label);
            return result is not null;
        }, timeoutMs, 300, ct);

        return found && result is not null
            ? result.Value
            : throw new InvalidOperationException(
                $"已勾选版权材料「{label}」，但 {timeoutMs / 1000} 秒内未出现可见的文件上传组件。");
    }

    private static async Task<(ILocator Field, ILocator Input)?> TryFindCopyrightMaterialUploadControlAsync(
        IPage page,
        string label)
    {
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

    private static async Task VerifyCopyrightInputFileNameAsync(
        ILocator input,
        string expectedFileName,
        CancellationToken ct)
    {
        string[] actualFileNames = [];
        var matched = await WaitUntilAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                actualFileNames = await input.EvaluateAsync<string[]>(
                    "element => Array.from(element.files || []).map(file => file.name)");
                return actualFileNames.Any(name =>
                    string.Equals(name, expectedFileName, StringComparison.OrdinalIgnoreCase));
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
                $"TikTok 版权材料文件选择校验失败，期望：{expectedFileName}；input.files：{actual}。");
        }
    }

    private static async Task WaitForCopyrightMaterialUploadResultAsync(
        ILocator field,
        string label,
        string fileName,
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
                lastProbe = await ProbeCopyrightMaterialUploadStateAsync(field, fileName);
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

    private static Task<string> ProbeCopyrightMaterialUploadStateAsync(ILocator field, string fileName) =>
        field.EvaluateAsync<string>(
            """
            (root, expectedFileName) => {
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
              const successMarkers = ['上传成功', '已上传', 'upload success', 'uploaded'];
              const successMarker = successMarkers.find(marker => fieldText.includes(marker));
              const successfulElement = elements.find(element => {
                const className = lower(typeof element.className === 'string' ? element.className : '');
                return /(?:upload.*success|success.*upload|upload.*finished|upload.*complete)/.test(className);
              });
              if (fileShown && (successMarker || successfulElement))
                return `success:${expectedFileName} 已显示且页面无上传中或错误状态`;
              if (fileShown)
                return `ready:${expectedFileName} 已显示且页面无上传中或错误状态`;
              return `pending:等待上传区域显示 ${expectedFileName}`;
            }
            """,
            fileName);

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
