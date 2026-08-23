using Microsoft.Playwright;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services.TikTok;

internal enum CopyrightProofMaterialProbeState
{
    HasMaterial,
    Empty,
    Unavailable,
}

internal sealed record CopyrightProofMaterialProbe(
    CopyrightProofMaterialProbeState State,
    string Detail);

internal sealed record CopyrightProofMaterialCoverageProbe(
    bool FormAvailable,
    TikTokCopyrightMaterialCompletionPlan Plan,
    IReadOnlyList<string> Details);

internal sealed record CopyrightMaterialCheckbox(
    ILocator Control,
    ILocator ClickTarget);

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
    private const string OriginalRightsHolderFieldId = "copyrightProof.isOriginalRightsHolder";
    private const string AdaptationFieldId = "copyrightProof.isAdaptation";
    private static string? GetCopyrightMaterialUploadFieldSelector(string materialKey) =>
        materialKey switch
        {
            TikTokPublishConstants.ProductionAgreementMaterialType =>
                "[x-field-id='copyrightProof.materialFiles.2']",
            TikTokPublishConstants.FilingOrDistributionLicenseMaterialType =>
                "[x-field-id='copyrightProof.materialFiles.4']",
            "opening_ending_rights_notice" =>
                "[x-field-id='copyrightProof.materialFiles.5']",
            TikTokPublishConstants.AiGenerationScreenshotsMaterialType =>
                "[x-field-id='copyrightProof.materialFiles.6']",
            TikTokPublishConstants.EditingProjectFilesMaterialType =>
                "[x-field-id='copyrightProof.materialFiles.7']",
            TikTokPublishConstants.SourceFileInformationMaterialType =>
                "[x-field-id='copyrightProof.materialFiles.8']",
            _ => null,
        };

    internal static async Task RemoveAuxiliaryCopyrightProofMaterialsAsync(
        IPage page,
        Action<string>? log,
        CancellationToken ct)
    {
        var targets = new[]
        {
            TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
            TikTokPublishConstants.EditingProjectFilesMaterialType,
        };

        await RemoveCopyrightProofMaterialsAsync(page, targets, log, ct);
        await VerifyCopyrightProofMaterialsRemovedAsync(page, targets, ct);
        Log(log, "TikTok 已删除 AI 生成过程截图、剪辑工程文件，并取消勾选对应材料类型。");
    }

    private static async Task RemoveAutoManagedCopyrightProofMaterialsAsync(
        IPage page,
        Action<string>? log,
        CancellationToken ct)
    {
        await RemoveCopyrightProofMaterialsAsync(
            page,
            TikTokPublishConstants.AutoManagedCopyrightMaterialTypes,
            log,
            ct);
        await VerifyCopyrightProofMaterialsRemovedAsync(
            page,
            TikTokPublishConstants.AutoManagedCopyrightMaterialTypes,
            ct);
        Log(log, "TikTok 编辑页已清空全部自动管理的版权材料，并取消原有材料勾选。");
    }

    private static async Task RemoveCopyrightProofMaterialsAsync(
        IPage page,
        IReadOnlyList<string> targets,
        Action<string>? log,
        CancellationToken ct)
    {

        foreach (var materialKey in targets)
        {
            ct.ThrowIfCancellationRequested();
            var label = TikTokPublishConstants.CopyrightMaterialLabels[materialKey];
            var field = await TryFindCopyrightMaterialFieldAsync(page, materialKey, label);
            if (field is null)
            {
                Log(log, $"TikTok 版权材料没有显示上传区域，按无附件处理：{label}。");
                continue;
            }

            await RemoveAllCopyrightMaterialFilesAsync(page, field, label, log, ct);
        }

        var trigger = await WaitForCopyrightMaterialTypeTriggerAsync(
            page,
            CopyrightControlTimeoutMs,
            ct);
        await trigger.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 }).WaitAsync(ct);
        await OpenCopyrightMaterialTypePopupAsync(page, trigger, ct);
        foreach (var materialKey in targets)
        {
            var label = TikTokPublishConstants.CopyrightMaterialLabels[materialKey];
            var option = await WaitForCopyrightMaterialCheckboxAsync(
                page,
                materialKey,
                label,
                CopyrightControlTimeoutMs,
                ct);
            await EnsureCopyrightMaterialCheckboxStateAsync(
                page,
                materialKey,
                label,
                option,
                shouldSelect: false,
                log,
                ct);
        }
        await ClosePopupIfOpenAsync(page);
    }

    internal static async Task VerifyAuxiliaryCopyrightProofMaterialsRemovedAsync(
        IPage page,
        CancellationToken ct)
    {
        var targets = new[]
        {
            TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
            TikTokPublishConstants.EditingProjectFilesMaterialType,
        };

        await VerifyCopyrightProofMaterialsRemovedAsync(page, targets, ct);
    }

    private static async Task VerifyCopyrightProofMaterialsRemovedAsync(
        IPage page,
        IReadOnlyList<string> targets,
        CancellationToken ct)
    {

        foreach (var materialKey in targets)
        {
            ct.ThrowIfCancellationRequested();
            var label = TikTokPublishConstants.CopyrightMaterialLabels[materialKey];
            var field = await TryFindCopyrightMaterialFieldAsync(page, materialKey, label);
            if (field is not null && await CountExistingCopyrightMaterialFilesAsync(field) > 0)
                throw new InvalidOperationException($"TikTok 版权材料仍有附件残留：{label}。");
        }

        var trigger = await WaitForCopyrightMaterialTypeTriggerAsync(
            page,
            CopyrightControlTimeoutMs,
            ct);
        await OpenCopyrightMaterialTypePopupAsync(page, trigger, ct);
        try
        {
            foreach (var materialKey in targets)
            {
                var label = TikTokPublishConstants.CopyrightMaterialLabels[materialKey];
                var option = await WaitForCopyrightMaterialCheckboxAsync(
                    page,
                    materialKey,
                    label,
                    CopyrightControlTimeoutMs,
                    ct);
                if (await IsCopyrightMaterialCheckboxSelectedAsync(option.Control))
                    throw new InvalidOperationException($"TikTok 版权材料仍处于勾选状态：{label}。");
            }
        }
        finally
        {
            await ClosePopupIfOpenAsync(page);
        }
    }

    private static async Task RemoveAllCopyrightMaterialFilesAsync(
        IPage page,
        ILocator field,
        string label,
        Action<string>? log,
        CancellationToken ct)
    {
        const int maximumFiles = 30;
        for (var removed = 0; removed < maximumFiles; removed++)
        {
            ct.ThrowIfCancellationRequested();
            var before = await CountExistingCopyrightMaterialFilesAsync(field);
            if (before == 0)
            {
                Log(log, $"TikTok 版权材料附件已清空：{label}。");
                return;
            }

            var clicked = await field.EvaluateAsync<bool>(
                """
                root => {
                  const visible = element => {
                    if (!(element instanceof Element)) return false;
                    const style = getComputedStyle(element);
                    const rect = element.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' &&
                      Number(style.opacity || '1') > 0 && rect.width > 0 && rect.height > 0;
                  };
                  const cards = [...root.querySelectorAll(
                    '.semi-upload-file-list-main[role="list"] > *, ' +
                    '.semi-upload-file-list-main [role="list"] > *, ' +
                    '[class*="pictureCard"], [class*="fileCard"], [class*="upload-file"]')]
                    .filter(card => visible(card) && !card.querySelector('input[type="file"]'));
                  const card = cards[0];
                  if (!card) return false;
                  const selectors = [
                    'button[aria-label*="删除"]', 'button[title*="删除"]',
                    '[role="button"][aria-label*="删除"]', '[role="button"][title*="删除"]',
                    'button[aria-label*="remove" i]', 'button[title*="remove" i]',
                    '.semi-upload-file-card-close', '.semi-upload-file-card-icon-close',
                    '[class*="remove"]', '[class*="close"]', '.semi-icon-close'
                  ];
                  const target = selectors
                    .map(selector => card.querySelector(selector))
                    .find(element => visible(element));
                  if (!target) return false;
                  target.click();
                  return true;
                }
                """).WaitAsync(ct);
            if (!clicked)
                throw new InvalidOperationException($"未找到 TikTok 版权材料附件的删除按钮：{label}。");

            await ConfirmCopyrightMaterialRemovalAsync(page, ct);
            var decreased = await WaitUntilAsync(async () =>
            {
                ct.ThrowIfCancellationRequested();
                try { return await CountExistingCopyrightMaterialFilesAsync(field) < before; }
                catch { return before == 1; }
            }, 10000, 250, ct);
            if (!decreased)
                throw new InvalidOperationException($"删除 TikTok 版权材料附件后数量未减少：{label}。");
        }

        throw new InvalidOperationException($"TikTok 版权材料附件数量超过安全处理上限：{label}。");
    }

    private static async Task ConfirmCopyrightMaterialRemovalAsync(IPage page, CancellationToken ct)
    {
        var dialogs = page.Locator("[role='dialog']:visible, .semi-modal:visible");
        if (await dialogs.CountAsync() == 0)
            return;
        var dialog = dialogs.Last;
        foreach (var text in new[] { "确定", "删除", "确认" })
        {
            var button = dialog.GetByRole(AriaRole.Button, new() { Name = text, Exact = true }).Last;
            try
            {
                if (await button.CountAsync() == 0 || !await button.IsVisibleAsync())
                    continue;
                await button.ClickAsync(new() { Timeout = 3000 }).WaitAsync(ct);
                return;
            }
            catch
            {
                // Some upload cards remove immediately without a confirmation dialog.
            }
        }
    }

    internal static async Task<string?> FindExistingCopyrightProofMaterialAsync(
        IPage page,
        CancellationToken ct)
    {
        var result = await ProbeCopyrightProofMaterialAsync(page, ct).ConfigureAwait(false);
        return result.State == CopyrightProofMaterialProbeState.HasMaterial
            ? result.Detail
            : null;
    }

    internal static async Task<CopyrightProofMaterialProbe> ProbeCopyrightProofMaterialAsync(
        IPage page,
        CancellationToken ct)
    {
        var probe = "missing";
        var formAvailable = await WaitUntilAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            probe = await page.EvaluateAsync<string>(
                """
                () => {
                  const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
                  const isVisible = element => {
                    if (!(element instanceof Element)) return false;
                    const style = getComputedStyle(element);
                    const rect = element.getBoundingClientRect();
                    return style.display !== 'none' &&
                      style.visibility !== 'hidden' &&
                      Number(style.opacity || '1') > 0 &&
                      rect.width > 0 && rect.height > 0;
                  };
                  const fields = [...document.querySelectorAll(
                    "[x-field-id^='copyrightProof.materialFiles.']")]
                    .filter(isVisible);
                  if (fields.length === 0) return 'missing';

                  for (const field of fields) {
                    const fieldId = field.getAttribute('x-field-id') || 'copyright material';
                    const listCards = [...field.querySelectorAll(
                      '.semi-upload-file-list-main[role="list"] > *, ' +
                      '.semi-upload-file-list-main [role="list"] > *')]
                      .filter(isVisible);
                    if (listCards.length > 0)
                      return `existing:${fieldId}（${listCards.length} 个文件）`;

                    const cards = [...field.querySelectorAll(
                      '[class*="pictureCard"], [class*="fileCard"], [class*="upload-file"]')]
                      .filter(isVisible);
                    const existingCard = cards.find(card => {
                      if (card.querySelector('input[type="file"]')) return false;
                      const text = normalize([
                        card.innerText,
                        card.getAttribute('title'),
                        card.getAttribute('aria-label'),
                        card.getAttribute('data-file-name')
                      ].filter(Boolean).join(' '));
                      const hasFileLabel =
                        /\.(pdf|png|jpe?g|webp|gif|docx?)\b/i.test(text) ||
                        /\bPDF\b/i.test(text);
                      const hasPreview = Boolean(card.querySelector(
                        'img[src], canvas, [class*="preview"], [class*="pdf"]'));
                      return hasFileLabel || hasPreview;
                    });
                    if (existingCard)
                      return `existing:${fieldId}`;
                  }

                  return 'empty';
                }
                """);
            return !string.Equals(probe, "missing", StringComparison.Ordinal);
        }, 5000, 250, ct);

        if (!formAvailable || string.Equals(probe, "missing", StringComparison.Ordinal))
        {
            return new CopyrightProofMaterialProbe(
                CopyrightProofMaterialProbeState.Unavailable,
                "版权证明表单未加载或字段不可识别");
        }

        const string prefix = "existing:";
        if (probe.StartsWith(prefix, StringComparison.Ordinal))
        {
            return new CopyrightProofMaterialProbe(
                CopyrightProofMaterialProbeState.HasMaterial,
                probe[prefix.Length..]);
        }

        return new CopyrightProofMaterialProbe(
            CopyrightProofMaterialProbeState.Empty,
            "未检测到已上传文件");
    }

    internal static async Task<CopyrightProofMaterialCoverageProbe>
        ProbeConfiguredCopyrightProofMaterialsAsync(
            IPage page,
            IEnumerable<string>? configuredMaterialTypes,
            CancellationToken ct)
    {
        var configured = TikTokPublishConstants
            .NormalizeCopyrightMaterialTypes(configuredMaterialTypes)
            .ToArray();
        var formAvailable = await WaitUntilAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var materialTypeField = page.Locator(CopyrightMaterialTypeFieldSelector).First;
                if (await materialTypeField.CountAsync() > 0 &&
                    await materialTypeField.IsVisibleAsync())
                    return true;

                var uploadFields = page.Locator(CopyrightMaterialUploadFieldSelector);
                return await uploadFields.CountAsync() > 0;
            }
            catch
            {
                return false;
            }
        }, 5000, 250, ct);

        if (!formAvailable)
        {
            return new CopyrightProofMaterialCoverageProbe(
                false,
                TikTokCopyrightMaterialCompletionPlan.Create(configured, []),
                ["版权证明表单未加载或字段不可识别"]);
        }

        var existing = new List<string>();
        var details = new List<string>();
        foreach (var materialType in configured)
        {
            ct.ThrowIfCancellationRequested();
            var label = TikTokPublishConstants.CopyrightMaterialLabels[materialType];
            var field = await TryFindCopyrightMaterialFieldAsync(page, materialType, label);
            if (field is null)
            {
                details.Add($"{label}：未显示上传区域");
                continue;
            }

            var fileCount = await CountExistingCopyrightMaterialFilesAsync(field);
            if (fileCount > 0)
            {
                existing.Add(materialType);
                details.Add($"{label}：已有 {fileCount} 个文件");
            }
            else
            {
                details.Add($"{label}：未检测到文件");
            }
        }

        return new CopyrightProofMaterialCoverageProbe(
            true,
            TikTokCopyrightMaterialCompletionPlan.Create(configured, existing),
            details);
    }

    internal static Task ConfigureCopyrightProofAsync(
        IPage page,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct) =>
        ConfigureCopyrightProofAsync(page, options, [], log, ct, uploadAiScriptOutlineOnly: false);

    internal static async Task ConfigureCopyrightProofForEditAsync(
        IPage page,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        var desiredMaterialTypes = TikTokPublishConstants
            .ValidateAutoManagedCopyrightMaterialTypes(options.CopyrightMaterialTypes);

        // Editing is a full reconciliation, not an incremental append. Validate every
        // configured local artifact before touching the remote draft so a missing file
        // can never leave the draft half-cleared.
        await ConfigureCopyrightProofAsync(
                page,
                options,
                existingMaterialTypes: [],
                log,
                ct,
                uploadAiScriptOutlineOnly: false,
                validateOnly: true)
            .ConfigureAwait(false);
        Log(log, "TikTok 编辑页版权材料本地产物预检通过，开始按最新配置全量重建。");

        var coverage = await ProbeConfiguredCopyrightProofMaterialsAsync(
                page,
                options.CopyrightMaterialTypes,
                ct)
            .ConfigureAwait(false);
        foreach (var detail in coverage.Details)
            Log(log, $"TikTok 编辑页版权材料逐项检查：{detail}。");

        if (!coverage.FormAvailable)
        {
            throw new InvalidOperationException(
                "TikTok 编辑页未能识别现有版权材料。为避免重复上传，已停止自动填写；" +
                "请刷新页面后重试，或使用“补全版权证明”功能。");
        }

        await RemoveAutoManagedCopyrightProofMaterialsAsync(page, log, ct)
            .ConfigureAwait(false);

        await ConfigureCopyrightProofAsync(
                page,
                options,
                existingMaterialTypes: [],
                log,
                ct,
                uploadAiScriptOutlineOnly: false,
                preserveUnmanagedMaterialSelections: true)
            .ConfigureAwait(false);
        await VerifyCopyrightProofRebuildAsync(page, desiredMaterialTypes, log, ct)
            .ConfigureAwait(false);
    }

    internal static async Task ConfigureCopyrightProofAsync(
        IPage page,
        TikTokPublishOptions options,
        IEnumerable<string>? existingMaterialTypes,
        Action<string>? log,
        CancellationToken ct,
        bool uploadAiScriptOutlineOnly = false,
        bool preserveUnmanagedMaterialSelections = false,
        bool validateOnly = false)
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

        var completionPlan = TikTokCopyrightMaterialCompletionPlan.Create(
            configuredMaterialKeys,
            existingMaterialTypes);

        var supportedAutoUploadKeys = new HashSet<string>(
            TikTokPublishConstants.AutoManagedCopyrightMaterialTypes,
            StringComparer.Ordinal);
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
                    "AI 大纲、剧本、项目资料截图和角色矢量图仅可上传到「原始文件或素材文件信息」，" +
                    "AI 生成截图仅可上传到「AI 生成过程截图」，" +
                    "工程图仅可上传到「剪辑工程文件」。");

            var configuredLabels = unsupportedMaterialKeys
                .Select(key => TikTokPublishConstants.CopyrightMaterialLabels[key]);
            throw new NotSupportedException(
                $"版权材料「{string.Join("、", configuredLabels)}」已有独立文件，但当前自动上传流程尚未支持该类型；" +
                "请选择「制作协议、联合出品协议等合作协议」，以及可选的「原始文件或素材文件信息」/「AI 生成过程截图」/「剪辑工程文件」。");
        }

        var includeSourceFileInformation = configuredMaterialKeys.Contains(
            TikTokPublishConstants.SourceFileInformationMaterialType,
            StringComparer.Ordinal);
        var uploadSourceFileInformation = completionPlan.ShouldUpload(
            TikTokPublishConstants.SourceFileInformationMaterialType);
        var sourceInfoFiles = includeSourceFileInformation && uploadSourceFileInformation
            ? ResolveSourceFileInformationFiles(options)
            : [];
        var expectedSourceInfoFileCount = TikTokSourceFileInfoUploadPackageService.RequiredFileCount +
                                          (options.UploadSourceInfoRoleSceneScreenshot ? 1 : 0);
        if (includeSourceFileInformation && uploadSourceFileInformation &&
            sourceInfoFiles.Count != expectedSourceInfoFileCount)
        {
            throw new FileNotFoundException(
                $"「原始文件或素材文件信息」必须上传 {expectedSourceInfoFileCount} 个文件：" +
                "AI剧本大纲.pdf、剧本.pdf、01_剧本与项目资料.png、角色矢量图.png；" +
                (options.UploadSourceInfoRoleSceneScreenshot
                    ? "另需上传02_角色场景或项目素材.png；"
                    : string.Empty) +
                $"当前找到 {sourceInfoFiles.Count} 个（目录：{TikTokSourceFileInfoUploadPackageService.OutputDirectoryName}）。" +
                "请先执行“生成证明材料”。");
        }

        var includeAiGenerationScreenshots = configuredMaterialKeys.Contains(
            TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
            StringComparer.Ordinal);
        var uploadAiGenerationScreenshots = completionPlan.ShouldUpload(
            TikTokPublishConstants.AiGenerationScreenshotsMaterialType);
        var aiScreenshotFiles = includeAiGenerationScreenshots && uploadAiGenerationScreenshots &&
                                !uploadAiScriptOutlineOnly
            ? ResolveAiGenerationScreenshotFiles(options)
            : [];
        if (includeAiGenerationScreenshots && uploadAiGenerationScreenshots &&
            !uploadAiScriptOutlineOnly &&
            aiScreenshotFiles.Count < TikTokAiGenerationScreenshotService.RequiredImageCount)
        {
            throw new FileNotFoundException(
                $"「AI 生成过程截图」需要至少 {TikTokAiGenerationScreenshotService.RequiredImageCount} 张截图，" +
                $"当前仅找到 {aiScreenshotFiles.Count} 张（目录：{TikTokAiGenerationScreenshotService.OutputDirectoryName}）；" +
                "请先执行「生成证明材料」。");
        }

        IReadOnlyList<string> aiUploadFiles = aiScreenshotFiles;
        if (includeAiGenerationScreenshots && uploadAiGenerationScreenshots &&
            options.UploadAiScriptOutlineWithScreenshots &&
            (!includeSourceFileInformation || uploadAiScriptOutlineOnly))
        {
            var outlineFile = options.AiScriptOutlineFilePath?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(outlineFile) || !File.Exists(outlineFile))
            {
                throw new FileNotFoundException(
                    "已配置向「AI 生成过程截图」上传 AI剧本大纲.pdf，但文件不存在；" +
                    "请先执行「生成AI大纲」步骤。",
                    outlineFile);
            }

            outlineFile = Path.GetFullPath(outlineFile);
            aiUploadFiles = uploadAiScriptOutlineOnly
                ? [outlineFile]
                : aiScreenshotFiles.Concat([outlineFile]).ToArray();
        }

        if (uploadAiScriptOutlineOnly && aiUploadFiles.Count != 1)
            throw new InvalidOperationException(
                "补全 AI 剧本大纲时只允许向“AI 生成过程截图”材料栏追加 AI剧本大纲.pdf。");

        var includeEditingProjectFiles = configuredMaterialKeys.Contains(
            TikTokPublishConstants.EditingProjectFilesMaterialType,
            StringComparer.Ordinal);
        var uploadEditingProjectFiles = completionPlan.ShouldUpload(
            TikTokPublishConstants.EditingProjectFilesMaterialType);
        var editingProjectFiles = includeEditingProjectFiles && uploadEditingProjectFiles
            ? ResolveEditingProjectFiles(options)
            : [];
        if (includeEditingProjectFiles && uploadEditingProjectFiles &&
            editingProjectFiles.Count < TikTokProjectImageService.MinUploadImageCount)
        {
            throw new FileNotFoundException(
                $"「剪辑工程文件」需要至少 {TikTokProjectImageService.MinUploadImageCount} 张工程图，" +
                $"当前仅找到 {editingProjectFiles.Count} 张（目录：{TikTokProjectImageService.OutputDirectoryName}）；" +
                "请先执行「生成证明材料」或「生成工程图」。");
        }

        var includeFilingLicense = configuredMaterialKeys.Contains(
            TikTokPublishConstants.FilingOrDistributionLicenseMaterialType,
            StringComparer.Ordinal);
        var uploadFilingLicense = includeFilingLicense && completionPlan.ShouldUpload(
            TikTokPublishConstants.FilingOrDistributionLicenseMaterialType);
        var filingLicenseFile = string.Empty;
        if (uploadFilingLicense)
        {
            filingLicenseFile = options.ResolveCopyrightMaterialFilePath(
                TikTokPublishConstants.FilingOrDistributionLicenseMaterialType);
            if (string.IsNullOrWhiteSpace(filingLicenseFile) || !File.Exists(filingLicenseFile))
                throw new FileNotFoundException(
                    "“备案/发行许可”已勾选，但未找到可信时间戳认证证书；请先生成时间戳。",
                    filingLicenseFile);
            filingLicenseFile = Path.GetFullPath(filingLicenseFile);
        }

        var includeProductionAgreement = configuredMaterialKeys.Contains(
            TikTokPublishConstants.ProductionAgreementMaterialType,
            StringComparer.Ordinal);
        var uploadProductionAgreement = includeProductionAgreement && completionPlan.ShouldUpload(
            TikTokPublishConstants.ProductionAgreementMaterialType);
        var resolvedFilePath = string.Empty;
        if (uploadProductionAgreement)
        {
            var filePath = options.ResolveCopyrightMaterialFilePath(
                TikTokPublishConstants.ProductionAgreementMaterialType);
            if (string.IsNullOrWhiteSpace(filePath))
                throw new FileNotFoundException("未配置当前项目的 TikTok 证明材料文件路径。", filePath);

            resolvedFilePath = Path.GetFullPath(filePath);
            if (!File.Exists(resolvedFilePath))
                throw new FileNotFoundException("当前项目的 TikTok 证明材料文件不存在。", resolvedFilePath);
        }

        if (validateOnly)
        {
            ValidateLocalCopyrightUploadFiles(
                sourceInfoFiles
                    .Concat(aiUploadFiles)
                    .Concat(editingProjectFiles)
                    .Concat(string.IsNullOrWhiteSpace(filingLicenseFile) ? [] : [filingLicenseFile])
                    .Concat(string.IsNullOrWhiteSpace(resolvedFilePath) ? [] : [resolvedFilePath]));
            return;
        }

        await SelectCopyrightRadioAsync(
            page,
            OriginalRightsHolderFieldId,
            options.IsOriginalRightsHolder ? 0 : 1,
            "是否原始权利人",
            options.IsOriginalRightsHolder ? "是" : "否",
            ct,
            dependentReady: () => IsCopyrightRadioFieldUnlockedAsync(page, AdaptationFieldId),
            dependentDescription: "内容原创类型仍未解锁");
        await SelectCopyrightRadioAsync(
            page,
            AdaptationFieldId,
            string.Equals(options.ContentOriginalityType, "adapted", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            "内容原创类型",
            string.Equals(options.ContentOriginalityType, "adapted", StringComparison.OrdinalIgnoreCase) ? "改编" : "原创",
            ct,
            dependentReady: () => IsCopyrightMaterialTriggerUnlockedAsync(page),
            dependentDescription: "上传材料类型仍被级联锁定");

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
        var editingProjectLabel =
            TikTokPublishConstants.CopyrightMaterialLabels[TikTokPublishConstants.EditingProjectFilesMaterialType];
        var filingLicenseLabel =
            TikTokPublishConstants.CopyrightMaterialLabels[TikTokPublishConstants.FilingOrDistributionLicenseMaterialType];
        if (includeProductionAgreement)
        {
            var productionAgreementOption = await WaitForCopyrightMaterialCheckboxAsync(
                page,
                TikTokPublishConstants.ProductionAgreementMaterialType,
                productionAgreementLabel,
                CopyrightControlTimeoutMs,
                ct);
            await EnsureCopyrightMaterialCheckboxStateAsync(
                page,
                TikTokPublishConstants.ProductionAgreementMaterialType,
                productionAgreementLabel,
                productionAgreementOption,
                shouldSelect: true,
                log,
                ct);
        }

        if (includeSourceFileInformation)
        {
            var sourceInfoOption = await WaitForCopyrightMaterialCheckboxAsync(
                page,
                TikTokPublishConstants.SourceFileInformationMaterialType,
                sourceInfoLabel,
                CopyrightControlTimeoutMs,
                ct);
            await EnsureCopyrightMaterialCheckboxStateAsync(
                page,
                TikTokPublishConstants.SourceFileInformationMaterialType,
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
                TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
                aiScreenshotLabel,
                CopyrightControlTimeoutMs,
                ct);
            await EnsureCopyrightMaterialCheckboxStateAsync(
                page,
                TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
                aiScreenshotLabel,
                aiOption,
                shouldSelect: true,
                log,
                ct);
        }

        if (includeEditingProjectFiles)
        {
            var editingOption = await WaitForCopyrightMaterialCheckboxAsync(
                page,
                TikTokPublishConstants.EditingProjectFilesMaterialType,
                editingProjectLabel,
                CopyrightControlTimeoutMs,
                ct);
            await EnsureCopyrightMaterialCheckboxStateAsync(
                page,
                TikTokPublishConstants.EditingProjectFilesMaterialType,
                editingProjectLabel,
                editingOption,
                shouldSelect: true,
                log,
                ct);
        }

        if (includeFilingLicense)
        {
            var filingOption = await WaitForCopyrightMaterialCheckboxAsync(
                page,
                TikTokPublishConstants.FilingOrDistributionLicenseMaterialType,
                filingLicenseLabel,
                CopyrightControlTimeoutMs,
                ct);
            await EnsureCopyrightMaterialCheckboxStateAsync(
                page,
                TikTokPublishConstants.FilingOrDistributionLicenseMaterialType,
                filingLicenseLabel,
                filingOption,
                shouldSelect: true,
                log,
                ct);
        }

        // 页面可能保留上一次未提交的选择；清掉当前未配置的已知类型，避免出现无文件映射的上传框。
        foreach (var pair in TikTokPublishConstants.CopyrightMaterialLabels)
        {
            if (includeProductionAgreement &&
                string.Equals(pair.Key, TikTokPublishConstants.ProductionAgreementMaterialType, StringComparison.Ordinal))
                continue;
            if (includeSourceFileInformation &&
                string.Equals(pair.Key, TikTokPublishConstants.SourceFileInformationMaterialType, StringComparison.Ordinal))
                continue;
            if (includeAiGenerationScreenshots &&
                string.Equals(pair.Key, TikTokPublishConstants.AiGenerationScreenshotsMaterialType, StringComparison.Ordinal))
                continue;
            if (includeEditingProjectFiles &&
                string.Equals(pair.Key, TikTokPublishConstants.EditingProjectFilesMaterialType, StringComparison.Ordinal))
                continue;
            if (includeFilingLicense &&
                string.Equals(pair.Key, TikTokPublishConstants.FilingOrDistributionLicenseMaterialType, StringComparison.Ordinal))
                continue;
            if (preserveUnmanagedMaterialSelections && !supportedAutoUploadKeys.Contains(pair.Key))
                continue;
            ct.ThrowIfCancellationRequested();
            var option = await TryFindCopyrightMaterialCheckboxAsync(page, pair.Key, pair.Value);
            if (option is null ||
                !await IsCopyrightMaterialCheckboxSelectedAsync(option.Control)) continue;
            await EnsureCopyrightMaterialCheckboxStateAsync(
                page,
                pair.Key,
                pair.Value,
                option,
                shouldSelect: false,
                log,
                ct);
        }
        await ClosePopupIfOpenAsync(page);

        var selectedParts = new List<string>();
        if (includeProductionAgreement)
            selectedParts.Add(productionAgreementLabel);
        if (includeSourceFileInformation)
            selectedParts.Add(sourceInfoLabel);
        if (includeAiGenerationScreenshots)
            selectedParts.Add(aiScreenshotLabel);
        if (includeEditingProjectFiles)
            selectedParts.Add(editingProjectLabel);
        if (includeFilingLicense)
            selectedParts.Add(filingLicenseLabel);
        Log(log, $"TikTok 版权材料类型已确认：{string.Join("、", selectedParts)}。");

        if (uploadProductionAgreement)
        {
            await UploadCopyrightMaterialFilesAsync(
                page,
                TikTokPublishConstants.ProductionAgreementMaterialType,
                productionAgreementLabel,
                [resolvedFilePath],
                preferProductionAgreementFieldId: true,
                log,
                ct);
        }
        else if (includeProductionAgreement)
        {
            Log(log, $"TikTok 版权材料已存在，保留并跳过重复上传：{productionAgreementLabel}。");
        }

        if (includeSourceFileInformation && uploadSourceFileInformation)
        {
            await UploadCopyrightMaterialFilesAsync(
                page,
                TikTokPublishConstants.SourceFileInformationMaterialType,
                sourceInfoLabel,
                sourceInfoFiles.ToArray(),
                preferProductionAgreementFieldId: false,
                log,
                ct);
        }
        else if (includeSourceFileInformation)
        {
            Log(log, $"TikTok 版权材料已存在，保留并跳过重复上传：{sourceInfoLabel}。");
        }

        if (includeAiGenerationScreenshots && uploadAiGenerationScreenshots)
        {
            await UploadCopyrightMaterialFilesAsync(
                page,
                TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
                aiScreenshotLabel,
                aiUploadFiles,
                preferProductionAgreementFieldId: false,
                log,
                ct);
        }
        else if (includeAiGenerationScreenshots)
        {
            Log(log, $"TikTok 版权材料已存在，保留并跳过重复上传：{aiScreenshotLabel}。");
        }

        if (includeEditingProjectFiles && uploadEditingProjectFiles)
        {
            await UploadCopyrightMaterialFilesAsync(
                page,
                TikTokPublishConstants.EditingProjectFilesMaterialType,
                editingProjectLabel,
                editingProjectFiles.ToArray(),
                preferProductionAgreementFieldId: false,
                log,
                ct);
        }
        else if (includeEditingProjectFiles)
        {
            Log(log, $"TikTok 版权材料已存在，保留并跳过重复上传：{editingProjectLabel}。");
        }

        if (includeFilingLicense && uploadFilingLicense)
        {
            await UploadCopyrightMaterialFilesAsync(
                page,
                TikTokPublishConstants.FilingOrDistributionLicenseMaterialType,
                filingLicenseLabel,
                [filingLicenseFile],
                preferProductionAgreementFieldId: false,
                log,
                ct);
        }
        else if (includeFilingLicense)
        {
            Log(log, $"TikTok 版权材料已存在，保留并跳过重复上传：{filingLicenseLabel}。");
        }
    }

    private static void ValidateLocalCopyrightUploadFiles(IEnumerable<string> filePaths)
    {
        foreach (var path in filePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
                throw new InvalidDataException($"待上传版权材料文件不存在或为空：{path}");
            if (string.Equals(info.Extension, ".pdf", StringComparison.OrdinalIgnoreCase))
                TikTokProofMaterialPdfRenderService.ValidatePdf(info.FullName);
        }
    }

    private static async Task VerifyCopyrightProofRebuildAsync(
        IPage page,
        IEnumerable<string>? configuredMaterialTypes,
        Action<string>? log,
        CancellationToken ct)
    {
        var configured = TikTokPublishConstants
            .NormalizeCopyrightMaterialTypes(configuredMaterialTypes)
            .ToHashSet(StringComparer.Ordinal);
        var coverage = await ProbeConfiguredCopyrightProofMaterialsAsync(page, configured, ct)
            .ConfigureAwait(false);
        foreach (var detail in coverage.Details)
            Log(log, $"TikTok 编辑页版权材料重建复查：{detail}。");
        if (!coverage.FormAvailable || !coverage.Plan.IsComplete)
        {
            var missing = coverage.Plan.MissingMaterialTypes
                .Select(type => TikTokPublishConstants.CopyrightMaterialLabels[type]);
            throw new InvalidOperationException(
                "TikTok 编辑页版权材料全量重建后复查失败：" +
                (coverage.FormAvailable
                    ? $"仍缺少 {string.Join("、", missing)}。"
                    : "版权证明表单不可识别。"));
        }

        var trigger = await WaitForCopyrightMaterialTypeTriggerAsync(
            page,
            CopyrightControlTimeoutMs,
            ct);
        await OpenCopyrightMaterialTypePopupAsync(page, trigger, ct);
        try
        {
            foreach (var materialType in TikTokPublishConstants.AutoManagedCopyrightMaterialTypes)
            {
                ct.ThrowIfCancellationRequested();
                var label = TikTokPublishConstants.CopyrightMaterialLabels[materialType];
                var option = await WaitForCopyrightMaterialCheckboxAsync(
                    page,
                    materialType,
                    label,
                    CopyrightControlTimeoutMs,
                    ct);
                var expected = configured.Contains(materialType);
                if (await IsCopyrightMaterialCheckboxSelectedAsync(option.Control) != expected)
                {
                    throw new InvalidOperationException(
                        $"TikTok 编辑页版权材料勾选状态不一致：{label}，期望：{(expected ? "勾选" : "取消勾选")}。");
                }

                if (expected) continue;
                var field = await TryFindCopyrightMaterialFieldAsync(page, materialType, label);
                if (field is not null && await CountExistingCopyrightMaterialFilesAsync(field) > 0)
                    throw new InvalidOperationException($"TikTok 编辑页已取消的版权材料仍有附件：{label}。");
            }
        }
        finally
        {
            await ClosePopupIfOpenAsync(page);
        }

        Log(log, "TikTok 编辑页版权材料已按最新配置全量重建并通过提交前复查。");
    }

    /// <summary>
    /// 仅从 workflow 下的「原始文件信息上传」目录读取配置的上传文件。
    /// </summary>
    private static IReadOnlyList<string> ResolveSourceFileInformationFiles(TikTokPublishOptions options)
    {
        var configured = options.ResolveCopyrightMaterialFilePath(
            TikTokPublishConstants.SourceFileInformationMaterialType);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return [];
        }

        var fullPath = Path.GetFullPath(configured);
        string? workflowDir = null;
        if (Directory.Exists(fullPath))
        {
            var dirName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(
                    dirName,
                    TikTokSourceFileInfoUploadPackageService.OutputDirectoryName,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(dirName, TikTokSourceFileInfoScreenshotService.OutputDirectoryName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(dirName, "原始文件或素材文件信息", StringComparison.OrdinalIgnoreCase))
            {
                workflowDir = Directory.GetParent(fullPath)?.FullName;
            }
            else
            {
                // 误绑到其它目录时仍按「该路径为 workflow」尝试解析专用子目录。
                workflowDir = fullPath;
            }
        }
        else if (File.Exists(fullPath))
        {
            workflowDir = Directory.GetParent(fullPath)?.Parent?.FullName
                          ?? Directory.GetParent(fullPath)?.FullName;
        }

        return string.IsNullOrWhiteSpace(workflowDir)
            ? []
            : TikTokSourceFileInfoUploadPackageService.ListFiles(
                workflowDir,
                options.UploadSourceInfoRoleSceneScreenshot);
    }

    /// <summary>
    /// 仅从 workflow 下的「剪辑工程文件」目录取工程图，不回落到 workflow 根目录散落文件。
    /// </summary>
    private static IReadOnlyList<string> ResolveEditingProjectFiles(TikTokPublishOptions options)
    {
        var configured = options.ResolveCopyrightMaterialFilePath(
            TikTokPublishConstants.EditingProjectFilesMaterialType);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return [];
        }

        var fullPath = Path.GetFullPath(configured);
        string? workflowDir = null;
        if (Directory.Exists(fullPath))
        {
            var dirName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(
                    dirName,
                    TikTokProjectImageService.OutputDirectoryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                workflowDir = Directory.GetParent(fullPath)?.FullName;
            }
            else
            {
                workflowDir = fullPath;
            }
        }
        else if (File.Exists(fullPath))
        {
            workflowDir = Directory.GetParent(fullPath)?.Parent?.FullName
                          ?? Directory.GetParent(fullPath)?.FullName;
        }

        return string.IsNullOrWhiteSpace(workflowDir)
            ? []
            : TikTokProjectImageService.ListGeneratedImages(workflowDir);
    }

    /// <summary>
    /// 仅从 workflow 下的「AI 生成过程截图」目录取图，不回落到工程图根目录。
    /// </summary>
    private static IReadOnlyList<string> ResolveAiGenerationScreenshotFiles(TikTokPublishOptions options)
    {
        var configured = options.ResolveCopyrightMaterialFilePath(
            TikTokPublishConstants.AiGenerationScreenshotsMaterialType);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return [];
        }

        var fullPath = Path.GetFullPath(configured);
        string? workflowDir = null;
        if (Directory.Exists(fullPath))
        {
            var dirName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(
                    dirName,
                    TikTokAiGenerationScreenshotService.OutputDirectoryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                workflowDir = Directory.GetParent(fullPath)?.FullName;
            }
            else
            {
                // 误绑到其它目录时仍按「该路径为 workflow」尝试解析专用子目录。
                workflowDir = fullPath;
            }
        }
        else if (File.Exists(fullPath))
        {
            workflowDir = Directory.GetParent(fullPath)?.Parent?.FullName
                          ?? Directory.GetParent(fullPath)?.FullName;
        }

        return string.IsNullOrWhiteSpace(workflowDir)
            ? []
            : TikTokAiGenerationScreenshotService.ListGeneratedImages(workflowDir);
    }

    private static async Task UploadCopyrightMaterialFilesAsync(
        IPage page,
        string materialKey,
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
            materialKey,
            label,
            CopyrightControlTimeoutMs,
            preferProductionAgreementFieldId,
            ct);
        await uploadControl.Field.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 }).WaitAsync(ct);
        Log(log, $"TikTok 版权材料上传组件已就绪：{label}。");

        var displayNames = string.Join("、", filePaths.Select(Path.GetFileName));
        var initialFileCardCount = await CountCopyrightMaterialFileCardsAsync(uploadControl.Field);
        Log(log, $"TikTok 版权材料开始上传：{label}（{displayNames}）。");
        await EnsureCopyrightUploadInputSupportsFilesAsync(
            uploadControl.Input,
            filePaths,
            label,
            ct);
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

            // Semi Upload rebuilds the material field after accepting a file. Re-resolve the
            // locator so completion probing observes the newly rendered PDF card instead of
            // the detached pre-upload subtree.
            var refreshedUploadControl = await WaitForCopyrightMaterialUploadControlAsync(
                page,
                materialKey,
                label,
                CopyrightControlTimeoutMs,
                preferProductionAgreementFieldId,
                ct);
            await WaitForCopyrightMaterialUploadResultAsync(
                refreshedUploadControl.Field,
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

    private static async Task EnsureCopyrightUploadInputSupportsFilesAsync(
        ILocator input,
        IReadOnlyList<string> filePaths,
        string label,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (filePaths.Count > 1 && !await input.EvaluateAsync<bool>("element => element.multiple"))
            throw new InvalidOperationException(
                $"TikTok「{label}」上传控件当前不支持一次选择多个文件，无法上传 {filePaths.Count} 个材料。");

        var accept = (await input.GetAttributeAsync("accept") ?? string.Empty).Trim().ToLowerInvariant();
        if (accept.Length == 0) return;
        var tokens = accept.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var extension in filePaths.Select(Path.GetExtension)
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(value => value!.ToLowerInvariant())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mime = extension switch
            {
                ".pdf" => "application/pdf",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => string.Empty,
            };
            var accepted = tokens.Any(token =>
                token == extension || token == mime ||
                (mime.StartsWith("image/", StringComparison.Ordinal) && token == "image/*") ||
                token == "*/*");
            if (!accepted)
                throw new InvalidOperationException(
                    $"TikTok「{label}」上传控件不接受 {extension} 文件；页面 accept={accept}。");
        }
    }

    private static async Task<ILocator?> TryFindCopyrightMaterialFieldAsync(
        IPage page,
        string materialKey,
        string label)
    {
        var fieldSelector = GetCopyrightMaterialUploadFieldSelector(materialKey);
        if (!string.IsNullOrWhiteSpace(fieldSelector))
        {
            var mappedField = page.Locator(fieldSelector).First;
            try
            {
                if (await mappedField.CountAsync() > 0 && await mappedField.IsVisibleAsync())
                    return mappedField;
            }
            catch
            {
                // Continue with the label-based fallback.
            }
        }

        var candidates = GetCopyrightMaterialTextCandidates(materialKey, label);

        foreach (var candidateText in candidates.Distinct(StringComparer.Ordinal))
        {
            var exactLabels = page.Locator(
                $"xpath=//*[normalize-space(translate(text(), '*', ''))={XPathLiteral(candidateText)}]");
            if (await exactLabels.CountAsync() == 0)
                exactLabels = page.GetByText(candidateText, new() { Exact = true });

            var count = await exactLabels.CountAsync();
            for (var index = count - 1; index >= 0; index--)
            {
                try
                {
                    var exactLabel = exactLabels.Nth(index);
                    if (!await exactLabel.IsVisibleAsync()) continue;

                    var field = exactLabel.Locator(
                        "xpath=ancestor::*[contains(@class,'materialBlock')][1]" +
                        "//*[starts-with(@x-field-id, 'copyrightProof.materialFiles.')][1]");
                    if (await field.CountAsync() == 0)
                    {
                        field = exactLabel.Locator(
                            "xpath=ancestor::*[starts-with(@x-field-id, 'copyrightProof.materialFiles.')][1]");
                    }
                    if (await field.CountAsync() > 0 && await field.IsVisibleAsync())
                        return field;
                }
                catch
                {
                    // The form may redraw while probing; try the next candidate.
                }
            }
        }

        return null;
    }

    private static Task<int> CountExistingCopyrightMaterialFilesAsync(ILocator field) =>
        field.EvaluateAsync<int>(
            """
            root => {
              const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
              const isVisible = element => {
                if (!(element instanceof Element)) return false;
                const style = getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                return style.display !== 'none' &&
                  style.visibility !== 'hidden' &&
                  Number(style.opacity || '1') > 0 &&
                  rect.width > 0 && rect.height > 0;
              };
              const listCards = [...root.querySelectorAll(
                '.semi-upload-file-list-main[role="list"] > *, ' +
                '.semi-upload-file-list-main [role="list"] > *')]
                .filter(isVisible);
              if (listCards.length > 0) return listCards.length;

              const cards = [...root.querySelectorAll(
                '[class*="pictureCard"], [class*="fileCard"], [class*="upload-file"]')]
                .filter(card => {
                  if (!isVisible(card) || card.querySelector('input[type="file"]')) return false;
                  const text = normalize([
                    card.innerText,
                    card.getAttribute('title'),
                    card.getAttribute('aria-label'),
                    card.getAttribute('data-file-name')
                  ].filter(Boolean).join(' '));
                  const hasFileLabel =
                    /\.(pdf|png|jpe?g|webp|gif|docx?)\b/i.test(text) ||
                    /\bPDF\b/i.test(text);
                  const hasPreview = Boolean(card.querySelector(
                    'img[src], canvas, [class*="preview"], [class*="pdf"]'));
                  return hasFileLabel || hasPreview;
                });
              return new Set(cards).size;
            }
            """);

    private static async Task<CopyrightMaterialCheckbox> WaitForCopyrightMaterialCheckboxAsync(
        IPage page,
        string materialKey,
        string label,
        int timeoutMs,
        CancellationToken ct)
    {
        CopyrightMaterialCheckbox? result = null;
        var found = await WaitUntilAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            result = await TryFindCopyrightMaterialCheckboxAsync(page, materialKey, label);
            return result is not null;
        }, timeoutMs, 300, ct);

        return found && result is not null
            ? result
            : throw new InvalidOperationException(
                $"TikTok「上传材料类型」已打开，但 {timeoutMs / 1000} 秒内未找到可操作的「{label}」复选框。");
    }

    private static async Task<CopyrightMaterialCheckbox?> TryFindCopyrightMaterialCheckboxAsync(
        IPage page,
        string materialKey,
        string label)
    {
        var candidates = GetCopyrightMaterialTextCandidates(materialKey, label);

        foreach (var candidateText in candidates.Distinct(StringComparer.Ordinal))
        {
            var labelLocators = new[]
            {
                page.Locator(
                    $"xpath=//*[normalize-space(translate(text(), '*', ''))={XPathLiteral(candidateText)}]"),
                page.GetByText(candidateText, new() { Exact = true }),
                page.GetByText(candidateText, new() { Exact = false }),
            };

            foreach (var labels in labelLocators)
            {
                var count = Math.Min(await labels.CountAsync(), 100);
                for (var index = count - 1; index >= 0; index--)
                {
                    try
                    {
                        var textNode = labels.Nth(index);
                        if (!await textNode.IsVisibleAsync()) continue;

                        // Old dropdowns and new right-side drawers may place the text and
                        // checkbox in sibling columns. Resolve their nearest common option.
                        var clickTarget = textNode.Locator(
                            "xpath=ancestor-or-self::*[" +
                            ".//input[@type='checkbox'] or .//*[@role='checkbox'] or @role='checkbox'][1]");
                        if (await clickTarget.CountAsync() == 0 ||
                            !await clickTarget.IsVisibleAsync()) continue;

                        var control = string.Equals(
                                await clickTarget.GetAttributeAsync("role"),
                                "checkbox",
                                StringComparison.OrdinalIgnoreCase)
                            ? clickTarget
                            : clickTarget.Locator(
                                "input[type='checkbox'], [role='checkbox']").First;
                        if (await control.CountAsync() == 0) continue;
                        if (!await control.EvaluateAsync<bool>(
                                "element => element.isConnected && " +
                                "!element.hasAttribute('disabled') && " +
                                "element.getAttribute('aria-disabled') !== 'true'")) continue;
                        return new CopyrightMaterialCheckbox(control, clickTarget);
                    }
                    catch
                    {
                        // The dropdown/drawer may redraw during polling.
                    }
                }
            }
        }

        return null;
    }

    private static async Task EnsureCopyrightMaterialCheckboxStateAsync(
        IPage page,
        string materialKey,
        string label,
        CopyrightMaterialCheckbox option,
        bool shouldSelect,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var isSelected = await IsCopyrightMaterialCheckboxSelectedAsync(option.Control);
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
            var current = await TryFindCopyrightMaterialCheckboxAsync(page, materialKey, label);
            return current is not null &&
                   await IsCopyrightMaterialCheckboxSelectedAsync(current.Control) == shouldSelect;
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
        string materialKey,
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
                materialKey,
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
        string materialKey,
        string label,
        bool preferProductionAgreementFieldId)
    {
        var mappedFieldSelector = GetCopyrightMaterialUploadFieldSelector(materialKey);
        if (!string.IsNullOrWhiteSpace(mappedFieldSelector))
        {
            var mappedControl = await TryFindCopyrightMaterialUploadControlBySelectorAsync(
                page,
                mappedFieldSelector);
            if (mappedControl is not null)
                return mappedControl;
        }

        if (preferProductionAgreementFieldId)
        {
            var fieldBasedControl = await TryFindCopyrightMaterialUploadControlByFieldIdAsync(page);
            if (fieldBasedControl is not null)
                return fieldBasedControl;
        }

        foreach (var candidateText in GetCopyrightMaterialTextCandidates(materialKey, label))
        {
            var exactLabels = page.Locator(
                $"xpath=//*[normalize-space(translate(text(), '*', ''))={XPathLiteral(candidateText)}]");
            if (await exactLabels.CountAsync() == 0)
                exactLabels = page.GetByText(candidateText, new() { Exact = true });
            var count = await exactLabels.CountAsync();
            for (var index = count - 1; index >= 0; index--)
            {
                try
                {
                    var exactLabel = exactLabels.Nth(index);
                    if (!await exactLabel.IsVisibleAsync()) continue;

                    var field = exactLabel.Locator(
                        "xpath=ancestor::*[contains(@class,'materialBlock')][1]" +
                        "//*[starts-with(@x-field-id, 'copyrightProof.materialFiles.')][1]");
                    if (await field.CountAsync() == 0)
                    {
                        field = exactLabel.Locator(
                            "xpath=ancestor::*[starts-with(@x-field-id, 'copyrightProof.materialFiles.')][1]");
                    }
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
        }

        return null;
    }

    private static IReadOnlyList<string> GetCopyrightMaterialTextCandidates(
        string materialKey,
        string label)
    {
        var candidates = new List<string>();
        if (TikTokPublishConstants.CopyrightMaterialI18nKeys.TryGetValue(materialKey, out var i18nKey))
            candidates.Add(i18nKey);
        candidates.Add(label);
        candidates.AddRange(TikTokPublishConstants.GetCopyrightMaterialLabelCandidates(materialKey));
        return candidates.Distinct(StringComparer.Ordinal).ToArray();
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
                if (!await IsCopyrightMaterialTriggerUnlockedAsync(candidate))
                    continue;
                return candidate;
            }
            catch
            {
                // 页面异步重绘时重新定位或走旧版回退。
            }
        }

        var fallback = await FindComboboxByFieldLabelAsync(page, ["上传材料类型"]);
        return fallback is not null && await IsCopyrightMaterialTriggerUnlockedAsync(fallback)
            ? fallback
            : null;
    }

    internal static bool IsCopyrightMaterialTriggerUnlockedState(
        bool connected,
        bool disabled,
        string? ariaDisabled,
        string? className)
    {
        var classes = (className ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return connected &&
               !disabled &&
               !string.Equals(ariaDisabled?.Trim(), "true", StringComparison.OrdinalIgnoreCase) &&
               !classes.Contains("triggerCascadeLocked-dG71jy", StringComparer.OrdinalIgnoreCase) &&
               !classes.Any(value => value.StartsWith("triggerCascadeLocked-", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> IsCopyrightMaterialTriggerUnlockedAsync(ILocator trigger)
    {
        try
        {
            var connected = await trigger.EvaluateAsync<bool>("element => element.isConnected");
            var disabled = await trigger.EvaluateAsync<bool>(
                "element => element.hasAttribute('disabled') || element.disabled === true");
            return IsCopyrightMaterialTriggerUnlockedState(
                connected,
                disabled,
                await trigger.GetAttributeAsync("aria-disabled"),
                await trigger.GetAttributeAsync("class"));
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsCopyrightMaterialTriggerUnlockedAsync(IPage page)
    {
        var stableTrigger = page.Locator(
            $"{CopyrightMaterialTypeFieldSelector} [aria-haspopup='true']").First;
        if (await stableTrigger.CountAsync() > 0)
            return await IsCopyrightMaterialTriggerUnlockedAsync(stableTrigger);

        var fallback = await FindComboboxByFieldLabelAsync(page, ["上传材料类型"]);
        return fallback is not null && await IsCopyrightMaterialTriggerUnlockedAsync(fallback);
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

    internal static bool IsCopyrightMaterialCheckboxSelectedState(
        bool nativeChecked,
        string? ariaChecked,
        string? controlClass,
        string? wrapperClass,
        string? innerClass)
    {
        if (nativeChecked ||
            string.Equals(ariaChecked?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        static bool HasCheckedClass(string? value) =>
            (value ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Any(part =>
                    part.Equals("semi-checkbox-checked", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("semi-checkbox-inner-checked", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("checked", StringComparison.OrdinalIgnoreCase));

        return HasCheckedClass(controlClass) ||
               HasCheckedClass(wrapperClass) ||
               HasCheckedClass(innerClass);
    }

    private static async Task<bool> IsCopyrightMaterialCheckboxSelectedAsync(ILocator control)
    {
        try
        {
            var tagName = await control.EvaluateAsync<string>(
                "element => element.tagName.toLowerCase()");
            var nativeChecked = string.Equals(tagName, "input", StringComparison.Ordinal) &&
                                await control.IsCheckedAsync();
            var ariaChecked = await control.GetAttributeAsync("aria-checked");
            var wrapper = control.Locator(
                "xpath=ancestor-or-self::*[contains(concat(' ', normalize-space(@class), ' '), ' semi-checkbox-wrapper ') or " +
                "contains(concat(' ', normalize-space(@class), ' '), ' semi-checkbox ')][1]");
            var inner = wrapper.Locator(".semi-checkbox-inner").First;
            return IsCopyrightMaterialCheckboxSelectedState(
                nativeChecked,
                ariaChecked,
                await control.GetAttributeAsync("class"),
                await wrapper.CountAsync() > 0 ? await wrapper.GetAttributeAsync("class") : null,
                await inner.CountAsync() > 0 ? await inner.GetAttributeAsync("class") : null);
        }
        catch
        {
            return false;
        }
    }

    private static async Task ClickCopyrightMaterialOptionAsync(
        CopyrightMaterialCheckbox option,
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
                  const target = element.matches('.semi-checkbox, input[type="checkbox"], [role="checkbox"]')
                    ? element
                    : element.querySelector('.semi-checkbox') ||
                      element.querySelector('input[type="checkbox"]') ||
                      element.querySelector('[role="checkbox"]') ||
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

    private static async Task<(ILocator Field, ILocator Input)?>
        TryFindCopyrightMaterialUploadControlBySelectorAsync(IPage page, string fieldSelector)
    {
        var field = page.Locator(fieldSelector).First;
        try
        {
            if (await field.CountAsync() == 0 || !await field.IsVisibleAsync())
                return null;

            foreach (var inputSelector in new[]
                     {
                         "input.semi-upload-hidden-input:not(.semi-upload-hidden-input-replace)",
                         "input[type='file'][multiple]",
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
            // The selected material field may be asynchronously redrawn.
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

    internal static bool IsCopyrightRadioSelectedState(
        bool inputChecked,
        string? inputAriaChecked,
        string? roleAriaChecked,
        string? labelClass,
        string? innerClass)
    {
        if (inputChecked ||
            string.Equals(inputAriaChecked?.Trim(), "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(roleAriaChecked?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        static bool HasCheckedClass(string? value, string expected) =>
            (value ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Contains(expected, StringComparer.OrdinalIgnoreCase);

        return HasCheckedClass(labelClass, "semi-radio-checked") ||
               HasCheckedClass(innerClass, "semi-radio-inner-checked");
    }

    private static async Task<bool> IsCopyrightRadioSelectedAsync(ILocator radio)
    {
        try
        {
            var inputChecked = await radio.IsCheckedAsync();
            var inputAriaChecked = await radio.GetAttributeAsync("aria-checked");
            var label = radio.Locator("xpath=ancestor::label[1]");
            var roleRadio = radio.Locator("xpath=ancestor-or-self::*[@role='radio'][1]");
            var inner = label.Locator(".semi-radio-inner").First;
            var labelClass = await label.CountAsync() > 0
                ? await label.GetAttributeAsync("class")
                : null;
            var roleAriaChecked = await roleRadio.CountAsync() > 0
                ? await roleRadio.GetAttributeAsync("aria-checked")
                : null;
            var innerClass = await inner.CountAsync() > 0
                ? await inner.GetAttributeAsync("class")
                : null;

            return IsCopyrightRadioSelectedState(
                inputChecked,
                inputAriaChecked,
                roleAriaChecked,
                labelClass,
                innerClass);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ILocator?> TryResolveCopyrightRadioAsync(
        IPage page,
        string fieldId,
        int optionIndex)
    {
        var fields = page.Locator($"[x-field-id='{fieldId}']");
        var count = await fields.CountAsync();
        ILocator? connectedFallback = null;
        for (var index = count - 1; index >= 0; index--)
        {
            var field = fields.Nth(index);
            var radio = field.Locator("input[type='radio']").Nth(optionIndex);
            try
            {
                if (await radio.CountAsync() == 0 ||
                    !await radio.EvaluateAsync<bool>(
                        "element => element.isConnected && !element.disabled"))
                {
                    continue;
                }

                connectedFallback ??= radio;
                if (await field.IsVisibleAsync())
                    return radio;
            }
            catch
            {
                // React may redraw the field while the form is initializing.
            }
        }

        return connectedFallback;
    }

    private static async Task<bool> IsCopyrightRadioFieldUnlockedAsync(
        IPage page,
        string fieldId)
    {
        var field = page.Locator($"[x-field-id='{fieldId}']").First;
        if (await field.CountAsync() == 0)
            return true;

        try
        {
            return await field.EvaluateAsync<bool>(
                """
                element => Array.from(element.querySelectorAll('input[type="radio"]'))
                  .some(input => input.isConnected && !input.disabled)
                """);
        }
        catch
        {
            return false;
        }
    }

    internal static async Task SelectCopyrightRadioAsync(
        IPage page,
        string fieldId,
        int optionIndex,
        string legacyFieldLabel,
        string legacyOptionLabel,
        CancellationToken ct,
        Func<Task<bool>>? dependentReady = null,
        string? dependentDescription = null)
    {
        if (await page.Locator($"[x-field-id='{fieldId}']").CountAsync() > 0)
        {
            ILocator? radio = null;
            var ready = await WaitUntilAsync(async () =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    radio = await TryResolveCopyrightRadioAsync(page, fieldId, optionIndex);
                    return radio is not null;
                }
                catch
                {
                    return false;
                }
            }, CopyrightControlTimeoutMs, 200, ct);
            if (!ready)
                throw new InvalidOperationException(
                    $"TikTok 版权字段「{fieldId}」的第 {optionIndex + 1} 个选项未在 " +
                    $"{CopyrightControlTimeoutMs / 1000} 秒内解锁。");

            async Task<bool> IsSelectionEffectiveAsync()
            {
                var current = await TryResolveCopyrightRadioAsync(page, fieldId, optionIndex);
                if (current is null || !await IsCopyrightRadioSelectedAsync(current))
                    return false;
                return dependentReady is null || await dependentReady();
            }

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                if (await IsSelectionEffectiveAsync())
                    return;

                var current = await TryResolveCopyrightRadioAsync(page, fieldId, optionIndex);
                if (current is null)
                {
                    await page.WaitForTimeoutAsync(250);
                    continue;
                }

                var label = current.Locator("xpath=ancestor::label[1]");
                await ClickWithFallbackAsync(await label.CountAsync() > 0 ? label : current, ct);
                var confirmed = await WaitUntilAsync(
                    async () =>
                    {
                        try { return await IsSelectionEffectiveAsync(); }
                        catch { return false; }
                    },
                    4000,
                    150,
                    ct);
                if (confirmed)
                    return;
            }

            var suffix = string.IsNullOrWhiteSpace(dependentDescription)
                ? "选中状态未生效"
                : dependentDescription;
            throw new InvalidOperationException(
                $"TikTok 版权字段「{fieldId}」的第 {optionIndex + 1} 个选项点击后未生效：{suffix}。");
        }

        // 兼容尚未提供 x-field-id 的旧页面。
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
            new { fieldLabel = legacyFieldLabel, optionLabel = legacyOptionLabel });
        ct.ThrowIfCancellationRequested();
        if (!selected)
            throw new InvalidOperationException(
                $"未找到 TikTok 版权字段「{fieldId}」（旧版「{legacyFieldLabel}」的「{legacyOptionLabel}」选项）。");
        if (dependentReady is not null)
        {
            var effective = await WaitUntilAsync(dependentReady, 5000, 150, ct);
            if (!effective)
                throw new InvalidOperationException(
                    $"TikTok 版权字段「{legacyFieldLabel}」选择「{legacyOptionLabel}」后未生效：" +
                    $"{dependentDescription ?? "后续字段仍未解锁"}。");
        }
        await page.WaitForTimeoutAsync(150);
    }
}
