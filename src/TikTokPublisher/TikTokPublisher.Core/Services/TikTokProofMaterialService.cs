using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public sealed class TikTokProofMaterialService
{
    public const string ProofPdfFileName = "证明材料.pdf";
    public const string ProofDocxFileName = "证明材料.docx";
    public const string StateDocumentType = "tiktok_proof_material_state";

    private const string FingerprintVersion = "v9-editing-project-files";
    private static readonly IReadOnlySet<string> SupportedSealImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".gif",
            ".bmp",
            ".tif",
            ".tiff",
            ".emf",
            ".wmf",
            ".svg",
        };
    private readonly TikTokProofMaterialDocumentBuilder _documentBuilder;
    private readonly TikTokProofMaterialPdfRenderService _pdfRenderService;

    public TikTokProofMaterialService(
        TikTokProofMaterialDocumentBuilder? documentBuilder = null,
        TikTokProofMaterialPdfRenderService? pdfRenderService = null)
    {
        _documentBuilder = documentBuilder ?? new TikTokProofMaterialDocumentBuilder();
        _pdfRenderService = pdfRenderService ?? new TikTokProofMaterialPdfRenderService();
    }

    public async Task<TikTokProofMaterialResult> GenerateAsync(
        TikTokProofMaterialRequest request,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCanonicalOutputFileName(request.OutputPdfPath);

        TikTokProofMaterialDocumentResult? documentResult = null;
        try
        {
            documentResult = _documentBuilder.CreateTemporaryDocx(request);
            log?.Invoke("证明材料 Word 模板替换完成。");

            var renderResult = await _pdfRenderService.RenderAsync(
                documentResult.DocxPath,
                request.OutputPdfPath,
                new TikTokProofMaterialPdfRenderOptions
                {
                    PreferredRenderer = request.PreferredPdfRenderer,
                    WpsExecutablePath = request.WpsExecutablePath,
                    LibreOfficeExecutablePath = request.LibreOfficeExecutablePath,
                    Timeout = request.RenderTimeout,
                },
                log,
                cancellationToken).ConfigureAwait(false);

            string? keptDocxPath = null;
            if (request.KeepIntermediateDocx)
            {
                var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPdfPath))!;
                keptDocxPath = Path.Combine(outputDirectory, ProofDocxFileName);
                File.Move(documentResult.DocxPath, keptDocxPath, overwrite: true);
            }

            log?.Invoke($"证明材料生成完成：{ProofPdfFileName}（{renderResult.RendererName}）。");
            return new TikTokProofMaterialResult(
                renderResult.PdfPath,
                keptDocxPath,
                renderResult.RendererName,
                documentResult.Replacements);
        }
        finally
        {
            if (documentResult is not null)
            {
                TikTokProofMaterialDocumentBuilder.TryDeleteDirectory(documentResult.WorkingDirectory);
            }
        }
    }

    public static async Task<TikTokProofMaterialResult> GenerateAsync(
        QueueProjectItem item,
        ClientSettings settings,
        TikTokAccountProfile? account,
        bool forceRerun,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        ProjectWorkspaceService.ValidateContextOwnership(context);
        Directory.CreateDirectory(context.WorkflowProjectDir);
        var checkpoint = LoadState(context);
        var statementDate = ResolveStatementDate(item, checkpoint);
        var request = CreateQueueRequest(
            item,
            settings,
            account,
            context.WorkflowProjectDir,
            statementDate);
        item.ProofMaterialStatementDate = statementDate.ToString("yyyy-MM-dd");
        var fingerprint = ComputeFingerprint(request);
        var outputDocxPath = GetDocxPath(context.WorkflowProjectDir);
        var selectedMaterials = new List<string>();
        if (request.GenerateProductionAgreement) selectedMaterials.Add("合作协议（核心）");
        if (request.GenerateSourceFileScreenshots) selectedMaterials.Add("原始文件或素材文件信息");
        if (request.GenerateAiGenerationScreenshots) selectedMaterials.Add("AI 生成过程截图");
        if (request.GenerateEditingProjectFiles) selectedMaterials.Add("剪辑工程文件");
        log?.Invoke(
            $"证明材料任务：已选 {selectedMaterials.Count} 类 [{string.Join("、", selectedMaterials)}]；" +
            $"项目目录={context.WorkflowProjectDir}；强制重跑={(forceRerun ? "是" : "否")}。");

        if (request.GenerateProductionAgreement && !request.KeepIntermediateDocx)
        {
            TryDelete(outputDocxPath);
        }

        if (!forceRerun && HasCurrentOutput(context, request, fingerprint, settings))
        {
            var renderer = GetStateString(checkpoint, "renderer");
            log?.Invoke($"[合作协议（核心）] 复用现有文件：{request.OutputPdfPath}。");
            LogExistingMaterial(
                log,
                "原始文件或素材文件信息",
                request.GenerateSourceFileScreenshots,
                TikTokSourceFileInfoUploadPackageService.ListFiles(
                    context.WorkflowProjectDir,
                    request.IncludeSourceInfoRoleSceneScreenshot));
            LogExistingMaterial(
                log,
                "AI 生成过程截图",
                request.GenerateAiGenerationScreenshots,
                TikTokAiGenerationScreenshotService.ListGeneratedImages(context.WorkflowProjectDir));
            LogRetainedAiFrames(
                log,
                context.WorkflowProjectDir,
                request.GenerateAiGenerationScreenshots);
            LogExistingMaterial(
                log,
                "剪辑工程文件",
                request.GenerateEditingProjectFiles,
                TikTokProjectImageService.ListGeneratedImages(context.WorkflowProjectDir));
            log?.Invoke("证明材料任务完成：配置指纹未变化，全部已选材料均已就绪，无需重新生成。");
            return new TikTokProofMaterialResult(
                request.GenerateProductionAgreement ? request.OutputPdfPath : string.Empty,
                request.KeepIntermediateDocx ? outputDocxPath : null,
                string.IsNullOrWhiteSpace(renderer) ? "WPS" : renderer,
                new TikTokProofMaterialReplacementCounts(0, 0, 0, 0, 0));
        }

        var canResume = !forceRerun &&
                        string.Equals(
                            GetStateString(checkpoint, "fingerprint"),
                            fingerprint,
                            StringComparison.OrdinalIgnoreCase);
        var coreCompleted = !request.GenerateProductionAgreement ||
                            (canResume && GetStateBool(checkpoint, "core_completed", fallback: true));
        var sourceCompleted = canResume && GetStateBool(checkpoint, "source_file_screenshots_completed", fallback: true);
        var aiCompleted = canResume && GetStateBool(checkpoint, "ai_generation_screenshots_completed", fallback: true);
        var editingCompleted = canResume && GetStateBool(checkpoint, "editing_project_files_completed", fallback: true);

        var service = new TikTokProofMaterialService();
        TikTokProofMaterialResult result;
        if (!request.GenerateProductionAgreement)
        {
            result = new TikTokProofMaterialResult(
                string.Empty,
                null,
                string.Empty,
                new TikTokProofMaterialReplacementCounts(0, 0, 0, 0, 0));
            log?.Invoke("[合作协议（核心）] 跳过：当前账号未勾选此材料类型。");
        }
        else if (coreCompleted && IsCoreOutputCurrent(context, request))
        {
            var renderer = GetStateString(checkpoint, "renderer");
            result = new TikTokProofMaterialResult(
                request.OutputPdfPath,
                request.KeepIntermediateDocx ? outputDocxPath : null,
                string.IsNullOrWhiteSpace(renderer) ? "WPS" : renderer,
                new TikTokProofMaterialReplacementCounts(0, 0, 0, 0, 0));
            log?.Invoke($"[合作协议（核心）] 断点复用：{DescribeFile(result.PdfPath)}。");
        }
        else
        {
            var coreTimer = Stopwatch.StartNew();
            log?.Invoke(
                $"[合作协议（核心）] 开始：模板={request.TemplateDocxPath}；" +
                $"输出={request.OutputPdfPath}；渲染器={request.PreferredPdfRenderer}。");
            try
            {
                result = await service.GenerateAsync(request, log, cancellationToken).ConfigureAwait(false);
                coreCompleted = true;
                sourceCompleted = false;
                aiCompleted = false;
                editingCompleted = false;
                SaveState(
                    context, request, fingerprint, result,
                    coreCompleted, sourceCompleted, aiCompleted, editingCompleted);
                log?.Invoke(
                    $"[合作协议（核心）] 完成并保存断点：{DescribeFile(result.PdfPath)}；" +
                    $"渲染器={result.PdfRenderer}；耗时={FormatElapsed(coreTimer.Elapsed)}。");
            }
            catch (Exception ex)
            {
                log?.Invoke(
                    $"[合作协议（核心）] 失败：阶段=模板替换或 PDF 渲染；" +
                    $"耗时={FormatElapsed(coreTimer.Elapsed)}；原因={ex.Message}");
                throw;
            }
        }

        if (request.GenerateProductionAgreement && !request.KeepIntermediateDocx)
        {
            TryDelete(outputDocxPath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (request.GenerateSourceFileScreenshots && sourceCompleted &&
            TikTokSourceFileInfoScreenshotService.HasCurrentOutput(context.WorkflowProjectDir) &&
            TikTokSourceFileInfoUploadPackageService.HasCurrentOutput(
                context.WorkflowProjectDir,
                request.IncludeSourceInfoRoleSceneScreenshot))
        {
            LogExistingMaterial(
                log,
                "原始文件或素材文件信息（断点复用）",
                selected: true,
                TikTokSourceFileInfoUploadPackageService.ListFiles(
                    context.WorkflowProjectDir,
                    request.IncludeSourceInfoRoleSceneScreenshot));
        }
        else if (request.GenerateSourceFileScreenshots)
        {
            var timer = Stopwatch.StartNew();
            log?.Invoke("[原始文件或素材文件信息] 正在确认 AI 大纲和剧本 PDF。");
            var outlinePdf = await TikTokAiScriptOutlineService.GenerateAsync(
                item,
                settings,
                account,
                forceRerun: false,
                log,
                cancellationToken).ConfigureAwait(false);
            var scriptPdf = await TikTokEpisodeScriptService.GenerateAsync(
                item,
                settings,
                forceRerun: false,
                log,
                cancellationToken).ConfigureAwait(false);
            log?.Invoke("[原始文件或素材文件信息] 正在生成参考格式素材包；角色定妆图将由已配置的图片模型生成。");
            await TikTokReferenceSourcePackageService.GenerateAsync(
                item,
                settings,
                forceRerun: false,
                log,
                cancellationToken).ConfigureAwait(false);
            if (!TikTokRoleVectorService.HasCurrentOutput(context.WorkflowProjectDir))
            {
                throw new InvalidOperationException(
                    "原始文件或素材文件信息缺少角色矢量图，请先执行“生成角色矢量图”步骤。");
            }
            if (!TikTokAiDramaProductionMaterialService.HasCurrentOutput(context.WorkflowProjectDir) &&
                TikTokAiDramaProductionMaterialService.CanGenerate(context.WorkflowProjectDir))
            {
                log?.Invoke("[原始文件或素材文件信息] 检测到真实抽帧和工作台素材，自动整理 AI 漫剧制作素材。");
                await TikTokAiDramaProductionMaterialService.GenerateAsync(
                    item,
                    settings,
                    forceRerun: false,
                    log,
                    cancellationToken).ConfigureAwait(false);
            }
            log?.Invoke(
                $"[原始文件或素材文件信息] 开始：来源={context.WorkflowProjectDir}；" +
                $"输出目录={TikTokSourceFileInfoScreenshotService.GetOutputDirectory(context.WorkflowProjectDir)}。");
            try
            {
                var outputs = TikTokSourceFileInfoScreenshotService.Generate(
                    context.WorkflowProjectDir,
                    request.DramaTitle,
                    request.CopyrightCompanyName,
                    log,
                    cancellationToken);
                var uploadFiles = TikTokSourceFileInfoUploadPackageService.Generate(
                    context.WorkflowProjectDir,
                    outlinePdf,
                    scriptPdf,
                    log,
                    request.IncludeSourceInfoRoleSceneScreenshot);
                sourceCompleted = true;
                SaveState(
                    context, request, fingerprint, result,
                    coreCompleted, sourceCompleted, aiCompleted, editingCompleted);
                LogGeneratedMaterial(log, "原始文件或素材文件信息", outputs, timer.Elapsed);
                LogGeneratedMaterial(log, "原始文件信息上传包", uploadFiles, timer.Elapsed);
            }
            catch (Exception ex)
            {
                log?.Invoke(
                    $"[原始文件或素材文件信息] 失败：耗时={FormatElapsed(timer.Elapsed)}；原因={ex.Message}");
                throw;
            }
        }
        else
        {
            log?.Invoke("[原始文件或素材文件信息] 跳过：当前账号未勾选此材料类型。");
        }

        var aiNeedsVideo =
            request.GenerateAiGenerationScreenshots &&
            (!aiCompleted || !TikTokAiGenerationScreenshotService.HasCurrentOutput(context.WorkflowProjectDir));
        var editingNeedsVideo =
            request.GenerateEditingProjectFiles &&
            (!editingCompleted ||
             !TikTokProjectImageService.HasCurrentProjectImages(context.SourceProjectDir, settings));
        var proofVideoEpisodeCount = ResolveTemporaryVideoEpisodeCount(
            aiNeedsVideo,
            editingNeedsVideo,
            settings);
        if (proofVideoEpisodeCount > 0)
        {
            _ = await QueueMaterialStepService.EnsureProofMaterialVideosAsync(
                    item,
                    settings,
                    proofVideoEpisodeCount,
                    log ?? (_ => { }),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (request.GenerateAiGenerationScreenshots && aiCompleted &&
            TikTokAiGenerationScreenshotService.HasCurrentOutput(context.WorkflowProjectDir))
        {
            LogExistingMaterial(
                log,
                "AI 生成过程截图（断点复用）",
                selected: true,
                TikTokAiGenerationScreenshotService.ListGeneratedImages(context.WorkflowProjectDir));
            LogRetainedAiFrames(log, context.WorkflowProjectDir, selected: true);
        }
        else if (request.GenerateAiGenerationScreenshots)
        {
            var timer = Stopwatch.StartNew();
            log?.Invoke(
                $"[AI 生成过程截图] 开始：来源={context.WorkflowProjectDir}；" +
                $"目标={TikTokAiGenerationScreenshotService.RequiredImageCount} 张；" +
                $"视觉模型={DescribeVisionConfiguration(settings)}。");
            try
            {
                var outputs = TikTokAiGenerationScreenshotService.Generate(
                    context.WorkflowProjectDir,
                    request.DramaTitle,
                    settings,
                    log,
                    cancellationToken);
                aiCompleted = true;
                SaveState(
                    context, request, fingerprint, result,
                    coreCompleted, sourceCompleted, aiCompleted, editingCompleted);
                LogGeneratedMaterial(log, "AI 生成过程截图", outputs, timer.Elapsed);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[AI 生成过程截图] 失败：耗时={FormatElapsed(timer.Elapsed)}；原因={ex.Message}");
                throw;
            }
        }
        else
        {
            log?.Invoke("[AI 生成过程截图] 跳过：当前账号未勾选此材料类型。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (request.GenerateEditingProjectFiles && editingCompleted &&
            TikTokProjectImageService.HasCurrentProjectImages(context.SourceProjectDir, settings))
        {
            LogExistingMaterial(
                log,
                "剪辑工程文件（断点复用）",
                selected: true,
                TikTokProjectImageService.ListGeneratedImages(context.WorkflowProjectDir));
        }
        else if (request.GenerateEditingProjectFiles)
        {
            var timer = Stopwatch.StartNew();
            log?.Invoke(
                $"[剪辑工程文件] 开始：输出目录=" +
                $"{TikTokProjectImageService.GetOutputDirectory(context.WorkflowProjectDir)}；" +
                $"强制重跑={(forceRerun ? "是" : "否")}。");
            try
            {
                await TikTokProjectImageService.GenerateAsync(
                    item,
                    settings,
                    forceRerun,
                    log,
                    cancellationToken).ConfigureAwait(false);
                editingCompleted = true;
                SaveState(
                    context, request, fingerprint, result,
                    coreCompleted, sourceCompleted, aiCompleted, editingCompleted);
                LogGeneratedMaterial(
                    log,
                    "剪辑工程文件",
                    TikTokProjectImageService.ListGeneratedImages(context.WorkflowProjectDir),
                    timer.Elapsed);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[剪辑工程文件] 失败：耗时={FormatElapsed(timer.Elapsed)}；原因={ex.Message}");
                throw;
            }
        }
        else
        {
            log?.Invoke("[剪辑工程文件] 跳过：当前账号未勾选此材料类型。");
        }

        SaveState(
            context, request, fingerprint, result,
            coreCompleted,
            !request.GenerateSourceFileScreenshots || sourceCompleted,
            !request.GenerateAiGenerationScreenshots || aiCompleted,
            !request.GenerateEditingProjectFiles || editingCompleted);
        log?.Invoke($"证明材料任务完成：已生成并登记 {selectedMaterials.Count} 类材料。");
        return result;
    }

    internal static int ResolveTemporaryVideoEpisodeCount(
        bool generateAiScreenshots,
        bool generateEditingProjectFiles,
        ClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (generateEditingProjectFiles)
        {
            return Math.Clamp(
                settings.TiktokProjectImageRenderEpisodeLimit <= 0
                    ? ClientSettingsDefaults.TiktokProjectImageRenderEpisodeLimit
                    : settings.TiktokProjectImageRenderEpisodeLimit,
                1,
                200);
        }

        // The AI workbench can fill all required shots from different frames of one episode.
        return generateAiScreenshots ? 1 : 0;
    }

    public static async Task<string> EnsureCurrentForUploadAsync(
        QueueProjectItem item,
        TikTokAccountProfile? account,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
            var expectedPath = GetPdfPath(context.WorkflowProjectDir);
            var result = await GenerateAsync(
                item,
                ClientSettingsStore.Load(),
                account,
                forceRerun: false,
                log,
                cancellationToken).ConfigureAwait(false);
            var requiresAgreement = TikTokPublishConstants.RequiresGeneratedProofMaterial(
                account?.TiktokCopyrightMaterialTypes);
            if (!requiresAgreement)
                return string.Empty;

            var resultPath = Path.GetFullPath(result.PdfPath);
            if (!string.Equals(resultPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"证明材料生成到了非当前项目目录：{resultPath}；预期路径：{expectedPath}。");
            }

            TikTokProofMaterialPdfRenderService.ValidatePdf(expectedPath);
            return expectedPath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"上传合作协议前准备证明材料失败：{ex.Message}", ex);
        }
    }

    internal static string ValidateExistingForUpload(QueueProjectItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var expectedPath = GetPdfPath(context.WorkflowProjectDir);
        try
        {
            TikTokProofMaterialPdfRenderService.ValidatePdf(expectedPath);
            return expectedPath;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"生成证明材料步骤已完成，但现有证明材料 PDF 缺失或无效：{expectedPath}。" +
                "请先点击“补全勾选证明材料”修复该项目，或勾选“强制重跑已完成步骤”。",
                ex);
        }
    }

    public static async Task<string> EnsureCurrentForUploadAsync(
        PublishItem item,
        TikTokAccountProfile? account,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        var materialTypes = TikTokPublishConstants.ValidateCopyrightMaterialTypes(
            account?.TiktokCopyrightMaterialTypes);
        if (!TikTokPublishConstants.RequiresAutoGeneratedCopyrightMaterial(materialTypes))
            return string.Empty;

        if (string.IsNullOrWhiteSpace(item.ProjectDir))
        {
            throw new InvalidOperationException(
                "上传合作协议前准备证明材料失败：未提供当前项目目录，无法定位 workflow/证明材料.pdf。");
        }

        var payload = TikTokProjectPayloadFactory.BuildFromPublishItem(item);
        var queueItem = new QueueProjectItem
        {
            ProjectDir = Path.GetFullPath(item.ProjectDir),
            DisplayName = item.ProjectKey ?? string.Empty,
            OriginalTitle = payload.OriginalTitle,
            NewTitle = payload.Title,
            Description = payload.Description,
            EpisodeCount = payload.EpisodeCount,
            PrimaryVideoPath = item.VideoPath,
        };
        return await EnsureCurrentForUploadAsync(queueItem, account, log, cancellationToken)
            .ConfigureAwait(false);
    }

    public static bool NeedsGenerateProofMaterial(
        QueueProjectItem item,
        ClientSettings settings,
        TikTokAccountProfile? account)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(settings);
            var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
            var state = LoadState(context);
            var request = CreateQueueRequest(
                item,
                settings,
                account,
                context.WorkflowProjectDir,
                ResolveStatementDate(item, state));
            var fingerprint = ComputeFingerprint(request);
            return !HasCurrentOutput(context, request, fingerprint, settings);
        }
        catch
        {
            // If configuration or inputs are invalid, the selected queue step must run and
            // report the actionable validation error rather than being silently skipped.
            return true;
        }
    }

    public static bool HasReusableProofMaterialForCopyrightCompletion(
        QueueProjectItem item,
        ClientSettings settings,
        TikTokAccountProfile? account)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(settings);
            var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
            var state = LoadState(context);
            var request = CreateQueueRequest(
                item,
                settings,
                account,
                context.WorkflowProjectDir,
                ResolveStatementDate(item, state));
            return HasCurrentOutput(context, request, ComputeFingerprint(request), settings);
        }
        catch
        {
            return false;
        }
    }

    internal static DateOnly? ResolveExistingStatementDate(
        IReadOnlyDictionary<string, JsonElement> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var value = GetStateString(state, "statement_date");
        return DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var statementDate)
            ? statementDate
            : null;
    }

    public static string GetPdfPath(string workflowProjectDirectory) =>
        Path.Combine(Path.GetFullPath(workflowProjectDirectory), ProofPdfFileName);

    public static string GetDocxPath(string workflowProjectDirectory) =>
        Path.Combine(Path.GetFullPath(workflowProjectDirectory), ProofDocxFileName);

    public static DateOnly GetChinaToday(TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;
        var timeZone = ResolveChinaTimeZone();
        var chinaNow = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeZone);
        return DateOnly.FromDateTime(chinaNow.DateTime);
    }

    public static string ComputeFingerprint(TikTokProofMaterialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = new
        {
            version = FingerprintVersion,
            generate_production_agreement = request.GenerateProductionAgreement,
            template_sha256 = request.GenerateProductionAgreement
                ? ComputeFileSha256(request.TemplateDocxPath, "证明材料 Word 模板")
                : "skipped",
            copyright_company = request.GenerateProductionAgreement
                ? request.CopyrightCompanyName.Trim()
                : "skipped",
            declarant_company = request.GenerateProductionAgreement
                ? request.DeclarantCompanyName.Trim()
                : "skipped",
            drama_title = request.DramaTitle.Trim(),
            statement_date = request.GenerateProductionAgreement
                ? request.StatementDate.ToString("yyyy-MM-dd")
                : "skipped",
            seal_sha256 = !request.GenerateProductionAgreement
                ? "skipped"
                : string.IsNullOrWhiteSpace(request.SealImagePath)
                    ? "template-seal"
                    : ComputeFileSha256(request.SealImagePath, "证明材料印章图片"),
            renderer = !request.GenerateProductionAgreement
                ? "skipped"
                : request.PreferredPdfRenderer == TikTokProofMaterialPdfRendererPreference.Wps
                    ? "wps"
                    : "libreoffice",
            wps_executable_path = request.GenerateProductionAgreement
                ? (request.WpsExecutablePath ?? string.Empty).Trim()
                : "skipped",
            generate_source_file_screenshots = request.GenerateSourceFileScreenshots,
            include_source_info_role_scene_screenshot = request.IncludeSourceInfoRoleSceneScreenshot,
            generate_ai_generation_screenshots = request.GenerateAiGenerationScreenshots,
            generate_editing_project_files = request.GenerateEditingProjectFiles,
            source_file_screenshots = request.GenerateSourceFileScreenshots
                ? TikTokSourceFileInfoScreenshotService.ScreenshotVersion
                : "skipped",
            ai_generation_screenshots = request.GenerateAiGenerationScreenshots
                ? TikTokAiGenerationScreenshotService.ScreenshotVersion
                : "skipped",
            editing_project_files = request.GenerateEditingProjectFiles
                ? TikTokProjectImageService.OutputDirectoryName + ":" + "v4-dedicated-folder"
                : "skipped",
        };
        var json = JsonSerializer.Serialize(payload);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    internal static TikTokProofMaterialRequest CreateQueueRequest(
        QueueProjectItem item,
        ClientSettings settings,
        TikTokAccountProfile? account,
        string workflowProjectDirectory,
        DateOnly? statementDate = null)
    {
        if (string.IsNullOrWhiteSpace(item.NewTitle))
        {
            throw new InvalidOperationException("生成证明材料前请先完成改写信息；改写后剧名不能为空。");
        }

        if (account is null)
        {
            var savedId = (item.AccountProfileId ?? string.Empty).Trim();
            var savedName = (item.AccountProfileName ?? string.Empty).Trim();
            var binding = !string.IsNullOrWhiteSpace(savedName) && !string.IsNullOrWhiteSpace(savedId)
                ? $"{savedName} ({savedId})"
                : !string.IsNullOrWhiteSpace(savedName)
                    ? savedName
                    : !string.IsNullOrWhiteSpace(savedId)
                        ? savedId
                        : "未绑定";
            throw new InvalidOperationException(
                $"未找到队列项目绑定账号：{binding}。该账号可能已删除或重新创建，请选择该工作目录对应账号后重新执行。");
        }

        var accountConfigMigrated = account.TiktokProofAccountConfigMigrated;
        var declarantCompanyName = FirstNonEmpty(
            account.TiktokProofDeclarantCompanyName,
            accountConfigMigrated ? null : settings.TiktokProofDeclarantCompanyName);
        var sealPath = accountConfigMigrated
            ? (account.TiktokProofSealPath ?? string.Empty).Trim()
            : FirstNonEmpty(account.TiktokProofSealPath, settings.TiktokProofSealPath);

        var materialTypes = TikTokPublishConstants.NormalizeCopyrightMaterialTypes(
            account.TiktokCopyrightMaterialTypes);
        var generateProductionAgreement = materialTypes.Contains(
            TikTokPublishConstants.ProductionAgreementMaterialType,
            StringComparer.Ordinal);
        if (generateProductionAgreement)
            sealPath = ResolveSealImagePath(sealPath);

        return new TikTokProofMaterialRequest(
            generateProductionAgreement
                ? TikTokProofMaterialTemplateProvider.ResolveTemplatePath(settings.TiktokProofTemplateDocxPath)
                : string.Empty,
            GetPdfPath(workflowProjectDirectory),
            account.TiktokProofCopyrightCompanyName ?? string.Empty,
            declarantCompanyName,
            item.NewTitle.Trim(),
            statementDate ?? ResolveStatementDate(item))
        {
            GenerateProductionAgreement = generateProductionAgreement,
            SealImagePath = sealPath,
            PreferredPdfRenderer = TikTokProofMaterialPdfRendererPreferenceExtensions.Parse(
                settings.TiktokProofPdfRenderer),
            WpsExecutablePath = settings.TiktokProofWpsPath,
            KeepIntermediateDocx = settings.TiktokProofKeepDocx,
            GenerateSourceFileScreenshots = materialTypes.Contains(
                TikTokPublishConstants.SourceFileInformationMaterialType,
                StringComparer.Ordinal),
            IncludeSourceInfoRoleSceneScreenshot = account.TiktokUploadSourceInfoRoleSceneScreenshot,
            GenerateAiGenerationScreenshots = materialTypes.Contains(
                TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
                StringComparer.Ordinal),
            GenerateEditingProjectFiles = materialTypes.Contains(
                TikTokPublishConstants.EditingProjectFilesMaterialType,
                StringComparer.Ordinal),
        };
    }

    internal static DateOnly ResolveStatementDate(QueueProjectItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (TryParseStatementDate(item.ProofMaterialStatementDate, out var statementDate))
            return statementDate;

        return GetChinaToday();
    }

    internal static DateOnly ResolveStatementDate(
        QueueProjectItem item,
        IReadOnlyDictionary<string, JsonElement> existingState)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(existingState);
        if (TryParseStatementDate(item.ProofMaterialStatementDate, out var queueStatementDate))
            return queueStatementDate;

        return ResolveExistingStatementDate(existingState) ?? GetChinaToday();
    }

    private static bool TryParseStatementDate(string? value, out DateOnly statementDate) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out statementDate);

    private static void LogExistingMaterial(
        Action<string>? log,
        string materialName,
        bool selected,
        IReadOnlyList<string> files)
    {
        if (!selected)
        {
            log?.Invoke($"[{materialName}] 未选择。");
            return;
        }

        log?.Invoke(
            $"[{materialName}] 复用现有产物：{files.Count} 个；" +
            $"文件={FormatFileList(files)}。");
    }

    private static void LogRetainedAiFrames(
        Action<string>? log,
        string workflowProjectDirectory,
        bool selected)
    {
        if (!selected)
            return;
        var frames = TikTokAiGenerationScreenshotService.ListRetainedFrameImages(workflowProjectDirectory);
        var manifest = TikTokAiGenerationScreenshotService.GetRetainedFramesManifestPath(
            workflowProjectDirectory);
        log?.Invoke(
            $"[AI 生成过程截图] 抽帧原图已保留：{frames.Count} 张；" +
            $"目录={TikTokAiGenerationScreenshotService.GetRetainedFramesDirectory(workflowProjectDirectory)}；" +
            $"清单={(File.Exists(manifest) ? manifest : "缺失")}。");
    }

    private static void LogGeneratedMaterial(
        Action<string>? log,
        string materialName,
        IReadOnlyList<string> files,
        TimeSpan elapsed)
    {
        var totalBytes = files.Where(File.Exists).Sum(path => new FileInfo(path).Length);
        log?.Invoke(
            $"[{materialName}] 完成：生成 {files.Count} 个文件，" +
            $"合计={FormatBytes(totalBytes)}，耗时={FormatElapsed(elapsed)}。");
        log?.Invoke($"[{materialName}] 输出：{FormatFileList(files)}。");
    }

    private static string DescribeVisionConfiguration(ClientSettings settings)
    {
        var configured = !string.IsNullOrWhiteSpace(settings.AiTextEndpoint)
                         && !string.IsNullOrWhiteSpace(settings.AiTextApiKey)
                         && !string.IsNullOrWhiteSpace(settings.AiTextModel);
        return configured
            ? $"已配置（{settings.AiTextModel.Trim()}）"
            : "未配置，将使用本地分析兜底";
    }

    private static string DescribeFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? $"{fullPath}（{FormatBytes(new FileInfo(fullPath).Length)}）"
            : $"{fullPath}（文件不存在）";
    }

    private static string FormatFileList(IEnumerable<string> files)
    {
        var values = files
            .Select(path =>
            {
                var name = Path.GetFileName(path);
                return File.Exists(path)
                    ? $"{name}({FormatBytes(new FileInfo(path).Length)})"
                    : $"{name}(不存在)";
            })
            .ToArray();
        return values.Length == 0 ? "无" : string.Join("、", values);
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 1
            ? $"{Math.Max(1, elapsed.TotalMilliseconds):0}ms"
            : $"{elapsed.TotalSeconds:0.0}s";

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024 * 1024):0.00}GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024d * 1024):0.00}MB";
        if (bytes >= 1024) return $"{bytes / 1024d:0.0}KB";
        return $"{bytes}B";
    }

    private static bool HasCurrentOutput(
        ProjectWorkspaceContext context,
        TikTokProofMaterialRequest request,
        string fingerprint,
        ClientSettings settings)
    {
        var state = LoadState(context);
        if (!string.Equals(
                GetStateString(state, "fingerprint"),
                fingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            (request.GenerateProductionAgreement &&
             !GetStateBool(state, "core_completed", fallback: true)) ||
            (request.GenerateSourceFileScreenshots &&
             !GetStateBool(state, "source_file_screenshots_completed", fallback: true)) ||
            (request.GenerateAiGenerationScreenshots &&
             !GetStateBool(state, "ai_generation_screenshots_completed", fallback: true)) ||
            (request.GenerateEditingProjectFiles &&
             !GetStateBool(state, "editing_project_files_completed", fallback: true)))
        {
            return false;
        }

        if (request.GenerateProductionAgreement && !IsCoreOutputCurrent(context, request))
        {
            return false;
        }

        if (request.GenerateSourceFileScreenshots &&
            (!TikTokSourceFileInfoScreenshotService.HasCurrentOutput(context.WorkflowProjectDir) ||
             !TikTokSourceFileInfoUploadPackageService.HasCurrentOutput(
                 context.WorkflowProjectDir,
                 request.IncludeSourceInfoRoleSceneScreenshot)))
        {
            return false;
        }

        if (request.GenerateAiGenerationScreenshots &&
            !TikTokAiGenerationScreenshotService.HasCurrentOutput(context.WorkflowProjectDir))
        {
            return false;
        }

        if (request.GenerateEditingProjectFiles &&
            !TikTokProjectImageService.HasCurrentProjectImages(context.SourceProjectDir, settings))
        {
            return false;
        }

        return true;
    }

    private static bool IsCoreOutputCurrent(
        ProjectWorkspaceContext context,
        TikTokProofMaterialRequest request)
    {
        try
        {
            TikTokProofMaterialPdfRenderService.ValidatePdf(request.OutputPdfPath);
        }
        catch
        {
            return false;
        }

        return !request.KeepIntermediateDocx ||
               File.Exists(GetDocxPath(context.WorkflowProjectDir));
    }

    private static Dictionary<string, JsonElement> LoadState(ProjectWorkspaceContext context) =>
        ProjectStateDocumentStore.LoadDocument(
            context.WorkspaceRoot,
            context.SourceProjectDir,
            StateDocumentType);

    private static void SaveState(
        ProjectWorkspaceContext context,
        TikTokProofMaterialRequest request,
        string fingerprint,
        TikTokProofMaterialResult result,
        bool coreCompleted,
        bool sourceFileScreenshotsCompleted,
        bool aiGenerationScreenshotsCompleted,
        bool editingProjectFilesCompleted)
    {
        var payload = new Dictionary<string, object?>
        {
            ["fingerprint"] = fingerprint,
            ["pdf_path"] = string.IsNullOrWhiteSpace(result.PdfPath)
                ? string.Empty
                : Path.GetFullPath(result.PdfPath),
            ["docx_path"] = string.IsNullOrWhiteSpace(result.IntermediateDocxPath)
                ? string.Empty
                : Path.GetFullPath(result.IntermediateDocxPath),
            ["template_path"] = string.IsNullOrWhiteSpace(request.TemplateDocxPath)
                ? string.Empty
                : Path.GetFullPath(request.TemplateDocxPath),
            ["generate_production_agreement"] = request.GenerateProductionAgreement,
            ["copyright_company"] = request.CopyrightCompanyName.Trim(),
            ["declarant_company"] = request.DeclarantCompanyName.Trim(),
            ["drama_title"] = request.DramaTitle.Trim(),
            ["statement_date"] = request.StatementDate.ToString("yyyy-MM-dd"),
            ["renderer"] = result.PdfRenderer,
            ["wps_executable_path"] = (request.WpsExecutablePath ?? string.Empty).Trim(),
            ["generate_source_file_screenshots"] = request.GenerateSourceFileScreenshots,
            ["generate_ai_generation_screenshots"] = request.GenerateAiGenerationScreenshots,
            ["generate_editing_project_files"] = request.GenerateEditingProjectFiles,
            ["core_completed"] = coreCompleted,
            ["source_file_screenshots_completed"] = sourceFileScreenshotsCompleted,
            ["ai_generation_screenshots_completed"] = aiGenerationScreenshotsCompleted,
            ["editing_project_files_completed"] = editingProjectFilesCompleted,
            ["source_file_screenshots"] = request.GenerateSourceFileScreenshots
                ? TikTokSourceFileInfoScreenshotService
                    .ListGeneratedImages(context.WorkflowProjectDir)
                    .Select(Path.GetFileName)
                    .ToArray()
                : Array.Empty<string>(),
            ["ai_generation_screenshots"] = request.GenerateAiGenerationScreenshots
                ? TikTokAiGenerationScreenshotService
                    .ListGeneratedImages(context.WorkflowProjectDir)
                    .Select(Path.GetFileName)
                    .ToArray()
                : Array.Empty<string>(),
            ["ai_generation_retained_frame_count"] = request.GenerateAiGenerationScreenshots
                ? TikTokAiGenerationScreenshotService
                    .ListRetainedFrameImages(context.WorkflowProjectDir)
                    .Count
                : 0,
            ["ai_generation_retained_frames_manifest"] = request.GenerateAiGenerationScreenshots
                ? Path.Combine(
                    TikTokAiGenerationScreenshotService.OutputDirectoryName,
                    TikTokAiGenerationScreenshotService.RetainedFramesDirectoryName,
                    TikTokAiGenerationScreenshotService.RetainedFramesManifestFileName)
                : string.Empty,
            ["editing_project_files"] = request.GenerateEditingProjectFiles
                ? TikTokProjectImageService
                    .ListGeneratedImages(context.WorkflowProjectDir)
                    .Select(Path.GetFileName)
                    .ToArray()
                : Array.Empty<string>(),
            ["generated_at"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        };
        ProjectStateDocumentStore.SaveDocument(
            context.WorkspaceRoot,
            context.SourceProjectDir,
            StateDocumentType,
            payload,
            context.WorkflowProjectDir);
    }

    private static string GetStateString(
        IReadOnlyDictionary<string, JsonElement> state,
        string key)
    {
        if (!state.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return value.GetString()?.Trim() ?? string.Empty;
    }

    private static bool GetStateBool(
        IReadOnlyDictionary<string, JsonElement> state,
        string key,
        bool fallback)
    {
        if (!state.TryGetValue(key, out var value) ||
            (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False))
        {
            return fallback;
        }

        return value.GetBoolean();
    }

    internal static string ResolveSealImagePath(string? configuredPath)
    {
        var value = (configuredPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"证明材料印章路径无效：{value}。", ex);
        }

        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        if (!Directory.Exists(fullPath))
        {
            // 保留不存在的文件路径，由指纹校验输出包含完整路径的明确错误。
            return fullPath;
        }

        string[] candidates;
        try
        {
            candidates = Directory
                .EnumerateFiles(fullPath, "*", SearchOption.TopDirectoryOnly)
                .Where(path => SupportedSealImageExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"无法读取证明材料印章目录：{fullPath}。", ex);
        }

        var preferred = candidates.FirstOrDefault(path =>
            string.Equals(Path.GetFileName(path), "seal.png", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return Path.GetFullPath(preferred);
        }

        if (candidates.Length == 1)
        {
            return Path.GetFullPath(candidates[0]);
        }

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"证明材料印章配置指向目录，但目录中未找到支持的图片：{fullPath}。" +
                "请在账号配置中选择具体的 PNG、JPG、GIF、BMP、TIFF、EMF、WMF 或 SVG 文件。");
        }

        throw new InvalidOperationException(
            $"证明材料印章配置指向目录，且找到 {candidates.Length} 个候选图片：{fullPath}。" +
            "请在账号配置中选择具体的印章图片文件。");
    }

    private static string ComputeFileSha256(string path, string displayName)
    {
        var value = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FileNotFoundException($"{displayName}路径为空，无法计算证明材料指纹。", value);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"{displayName}路径无效：{value}。", ex);
        }

        if (Directory.Exists(fullPath))
        {
            throw new InvalidDataException($"{displayName}路径指向目录而不是文件：{fullPath}。");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"{displayName}不存在：{fullPath}。", fullPath);
        }

        using var stream = File.OpenRead(fullPath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static TimeZoneInfo ResolveChinaTimeZone()
    {
        foreach (var id in new[] { "Asia/Shanghai", "China Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            "Asia/Shanghai-Fallback",
            TimeSpan.FromHours(8),
            "China Standard Time",
            "China Standard Time");
    }

    private static void EnsureCanonicalOutputFileName(string outputPdfPath)
    {
        if (string.IsNullOrWhiteSpace(outputPdfPath))
        {
            throw new ArgumentException("证明材料 PDF 输出路径不能为空。", nameof(outputPdfPath));
        }

        if (!string.Equals(
                Path.GetFileName(outputPdfPath),
                ProofPdfFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"证明材料 PDF 文件名必须为 {ProofPdfFileName}。");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A stale optional DOCX must not block PDF generation.
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }
}
