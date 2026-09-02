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
    internal const string StateSidecarFileName = ".tiktok-proof-material-state.json";

    private const string FingerprintVersion = "v9-editing-project-files";
    private const string ComponentFingerprintVersion = "v1-component-checkpoints";
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
        CancellationToken cancellationToken,
        RoleReferenceEpisodeFallback? episodeFallback = null,
        QueueRunOptions? runOptions = null)
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
        var sourceInfoSelection = TikTokSourceFileInfoPackageSelection.FromEnabledSteps(
            runOptions?.EnabledSteps,
            request.IncludeSourceInfoRoleVector,
            request.IncludeSourceInfoRoleSceneScreenshot);
        item.ProofMaterialStatementDate = statementDate.ToString("yyyy-MM-dd");
        var fingerprints = ComputeComponentFingerprints(request, sourceInfoSelection);
        var fingerprint = fingerprints.Aggregate;
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

        if (!forceRerun && HasCurrentOutput(
                context,
                request,
                fingerprints,
                settings,
                sourceInfoSelection))
        {
            var renderer = GetStateString(checkpoint, "renderer");
            log?.Invoke($"[合作协议（核心）] 复用现有文件：{request.OutputPdfPath}。");
            LogExistingMaterial(
                log,
                "原始文件或素材文件信息",
                request.GenerateSourceFileScreenshots,
                TikTokSourceFileInfoUploadPackageService.ListFiles(
                    context.WorkflowProjectDir,
                    request.IncludeSourceInfoRoleSceneScreenshot,
                    sourceInfoSelection));
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

        var legacyFingerprintMatches = !forceRerun &&
                                       string.Equals(
                                           GetStateString(checkpoint, "fingerprint"),
                                           fingerprint,
                                           StringComparison.OrdinalIgnoreCase);
        var coreCompleted = !request.GenerateProductionAgreement ||
                            (!forceRerun &&
                             IsComponentCheckpointCurrent(
                                 checkpoint, "core_fingerprint", fingerprints.Core, legacyFingerprintMatches) &&
                             GetStateBool(checkpoint, "core_completed", fallback: true));
        var sourceCompleted = !forceRerun &&
                              IsComponentCheckpointCurrent(
                                  checkpoint, "source_info_fingerprint", fingerprints.SourceInfo, legacyFingerprintMatches) &&
                              GetStateBool(checkpoint, "source_file_screenshots_completed", fallback: true);
        var aiCompleted = !forceRerun &&
                          IsComponentCheckpointCurrent(
                              checkpoint, "ai_screenshot_fingerprint", fingerprints.AiScreenshots, legacyFingerprintMatches) &&
                          GetStateBool(checkpoint, "ai_generation_screenshots_completed", fallback: true);
        var editingCompleted = !forceRerun &&
                               IsComponentCheckpointCurrent(
                                   checkpoint, "editing_project_fingerprint", fingerprints.EditingProject, legacyFingerprintMatches) &&
                               GetStateBool(checkpoint, "editing_project_files_completed", fallback: true);

        var coreReusable = !request.GenerateProductionAgreement ||
                           (coreCompleted && IsCoreOutputCurrent(context, request));
        var sourceReusable = !request.GenerateSourceFileScreenshots ||
                             (sourceCompleted &&
                              TikTokSourceFileInfoScreenshotService.HasCurrentOutput(context.WorkflowProjectDir) &&
                              TikTokSourceFileInfoUploadPackageService.HasCurrentOutput(
                                  context.WorkflowProjectDir,
                                  request.IncludeSourceInfoRoleSceneScreenshot,
                                  sourceInfoSelection));
        var aiReusable = !request.GenerateAiGenerationScreenshots ||
                         (aiCompleted &&
                          TikTokAiGenerationScreenshotService.HasCurrentOutput(context.WorkflowProjectDir));
        var editingReusable = !request.GenerateEditingProjectFiles ||
                              (editingCompleted &&
                               TikTokProjectImageService.HasCurrentProjectImages(context.SourceProjectDir, settings));
        log?.Invoke(
            "证明材料选择性计划：" +
            $"合作协议={DescribePlan(request.GenerateProductionAgreement, coreReusable)}；" +
            $"原始文件信息={DescribePlan(request.GenerateSourceFileScreenshots, sourceReusable)}；" +
            $"AI过程截图={DescribePlan(request.GenerateAiGenerationScreenshots, aiReusable)}；" +
            $"剪辑工程文件={DescribePlan(request.GenerateEditingProjectFiles, editingReusable)}。");

        var service = new TikTokProofMaterialService();
        TikTokProofMaterialResult result;

        static string DescribePlan(bool selected, bool reusable) =>
            !selected ? "未选择" : reusable ? "复用" : "重新生成";
        if (!request.GenerateProductionAgreement)
        {
            result = new TikTokProofMaterialResult(
                string.Empty,
                null,
                string.Empty,
                new TikTokProofMaterialReplacementCounts(0, 0, 0, 0, 0));
            log?.Invoke("[合作协议（核心）] 跳过：当前账号未勾选此材料类型。");
        }
        else if (coreReusable)
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
                result = await QueueWorkloadResourceScheduler.RunAsync(
                    QueueWorkloadResource.Document,
                    () => service.GenerateAsync(request, log, cancellationToken),
                    log,
                    cancellationToken).ConfigureAwait(false);
                coreCompleted = true;
                SaveState(
                    context, request, fingerprints, result,
                    coreCompleted, sourceCompleted, aiCompleted, editingCompleted,
                    log);
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
                    cancellationToken,
                    episodeFallback)
                .ConfigureAwait(false);
        }

        var branchStateLock = new object();
        await Task.WhenAll(
            RunAiScreenshotBranchAsync(),
            RunEditingProjectBranchAsync()).ConfigureAwait(false);

        await RunSourceInfoBranchAsync().ConfigureAwait(false);

        async Task RunSourceInfoBranchAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.GenerateSourceFileScreenshots)
            {
                log?.Invoke("[原始文件或素材文件信息] 跳过：当前账号未勾选此材料类型。");
                return;
            }

            if (sourceReusable)
            {
                LogExistingMaterial(
                    log,
                    "原始文件或素材文件信息（断点复用）",
                    selected: true,
                    TikTokSourceFileInfoUploadPackageService.ListFiles(
                        context.WorkflowProjectDir,
                        request.IncludeSourceInfoRoleSceneScreenshot,
                        sourceInfoSelection));
                return;
            }

            var timer = Stopwatch.StartNew();
            try
            {
                log?.Invoke(
                    "[原始文件或素材文件信息] 按本次启用步骤整理真实产物；" +
                    $"AI大纲={(sourceInfoSelection.IncludeOutline ? "包含" : "未启用")}，" +
                    $"剧本={(sourceInfoSelection.IncludeScript ? "包含" : "未启用")}，" +
                    $"角色矢量图={(sourceInfoSelection.IncludeRoleVector ? "包含" : "未启用")}。");
                var prerequisites = TikTokSourceFileInfoUploadPackageService.ResolveAvailablePrerequisites(
                    context.WorkflowProjectDir,
                    sourceInfoSelection);
                if (episodeFallback is not null &&
                    ProjectVideoResolver.ResolveMaterialVideos(context.SourceProjectDir).Count == 0)
                {
                    _ = await QueueMaterialStepService.EnsureProofMaterialVideosAsync(
                            item,
                            settings,
                            requiredEpisodeCount: 1,
                            log ?? (_ => { }),
                            cancellationToken,
                            episodeFallback)
                        .ConfigureAwait(false);
                }

                // The reference package is optional. Refresh it only when a previous
                // role-vector step created it; source information must not create that
                // unselected step as a hidden dependency.
                if (TikTokReferenceSourcePackageService.HasCurrentOutput(context.WorkflowProjectDir))
                {
                    TikTokReferenceSourcePackageService.RefreshMaterialVideoLinks(
                        item,
                        log,
                        cancellationToken);
                }

                log?.Invoke(
                    $"[原始文件或素材文件信息] 开始：来源={context.WorkflowProjectDir}；" +
                    $"输出目录={TikTokSourceFileInfoScreenshotService.GetOutputDirectory(context.WorkflowProjectDir)}。");
                var outputs = TikTokSourceFileInfoScreenshotService.Generate(
                    context.WorkflowProjectDir,
                    request.DramaTitle,
                    request.CopyrightCompanyName,
                    log,
                    cancellationToken);
                var uploadFiles = TikTokSourceFileInfoUploadPackageService.Generate(
                    context.WorkflowProjectDir,
                    prerequisites.OutlinePdf,
                    prerequisites.ScriptPdf,
                    log,
                    request.IncludeSourceInfoRoleSceneScreenshot,
                    sourceInfoSelection,
                    validateComplete: true);
                sourceCompleted = true;
                SaveState(
                    context, request, fingerprints, result,
                    coreCompleted, sourceCompleted, aiCompleted, editingCompleted,
                    log);
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

        async Task RunAiScreenshotBranchAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.GenerateAiGenerationScreenshots && aiReusable)
            {
                LogExistingMaterial(
                    log,
                    "AI 生成过程截图（断点复用）",
                    selected: true,
                    TikTokAiGenerationScreenshotService.ListGeneratedImages(context.WorkflowProjectDir));
                LogRetainedAiFrames(log, context.WorkflowProjectDir, selected: true);
                return;
            }
            if (!request.GenerateAiGenerationScreenshots)
            {
                log?.Invoke("[AI 生成过程截图] 跳过：当前账号未勾选此材料类型。");
                return;
            }

            var timer = Stopwatch.StartNew();
            log?.Invoke(
                $"[AI 生成过程截图] 开始：来源={context.WorkflowProjectDir}；" +
                $"目标={TikTokAiGenerationScreenshotService.RequiredImageCount} 张；" +
                $"视觉模型={DescribeVisionConfiguration(settings)}。");
            try
            {
                var outputs = await TikTokVisualEvidencePreparationService.EnsureCurrentAsync(
                    context.WorkflowProjectDir,
                    request.DramaTitle,
                    settings,
                    log,
                    cancellationToken).ConfigureAwait(false);
                lock (branchStateLock)
                {
                    aiCompleted = true;
                    SaveState(
                        context, request, fingerprints, result,
                        coreCompleted, sourceCompleted, aiCompleted, editingCompleted,
                        log);
                }
                LogGeneratedMaterial(log, "AI 生成过程截图", outputs, timer.Elapsed);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[AI 生成过程截图] 失败：耗时={FormatElapsed(timer.Elapsed)}；原因={ex.Message}");
                throw;
            }
        }

        async Task RunEditingProjectBranchAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.GenerateEditingProjectFiles && editingReusable)
            {
                LogExistingMaterial(
                    log,
                    "剪辑工程文件（断点复用）",
                    selected: true,
                    TikTokProjectImageService.ListGeneratedImages(context.WorkflowProjectDir));
                return;
            }
            if (!request.GenerateEditingProjectFiles)
            {
                log?.Invoke("[剪辑工程文件] 跳过：当前账号未勾选此材料类型。");
                return;
            }

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
                lock (branchStateLock)
                {
                    editingCompleted = true;
                    SaveState(
                        context, request, fingerprints, result,
                        coreCompleted, sourceCompleted, aiCompleted, editingCompleted,
                        log);
                }
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

        SaveState(
            context, request, fingerprints, result,
            coreCompleted,
            !request.GenerateSourceFileScreenshots || sourceCompleted,
            !request.GenerateAiGenerationScreenshots || aiCompleted,
            !request.GenerateEditingProjectFiles || editingCompleted,
            log);
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
        CancellationToken cancellationToken,
        RoleReferenceEpisodeFallback? episodeFallback = null,
        QueueRunOptions? runOptions = null)
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
                cancellationToken,
                episodeFallback,
                runOptions).ConfigureAwait(false);
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
        TikTokAccountProfile? account,
        QueueRunOptions? runOptions = null)
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
            var selection = TikTokSourceFileInfoPackageSelection.FromEnabledSteps(
                runOptions?.EnabledSteps,
                request.IncludeSourceInfoRoleVector,
                request.IncludeSourceInfoRoleSceneScreenshot);
            var fingerprints = ComputeComponentFingerprints(request, selection);
            return !HasCurrentOutput(context, request, fingerprints, settings, selection);
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
        TikTokAccountProfile? account,
        QueueRunOptions? runOptions = null)
        => GetProofMaterialReuseIssues(item, settings, account, runOptions).Count == 0;

    public static IReadOnlyList<string> GetProofMaterialReuseIssues(
        QueueProjectItem item,
        ClientSettings settings,
        TikTokAccountProfile? account,
        QueueRunOptions? runOptions = null)
    {
        var issues = new List<string>();
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
            var selection = TikTokSourceFileInfoPackageSelection.FromEnabledSteps(
                runOptions?.EnabledSteps,
                request.IncludeSourceInfoRoleVector,
                request.IncludeSourceInfoRoleSceneScreenshot);
            var fingerprints = ComputeComponentFingerprints(request, selection);
            var legacyFingerprintMatches = string.Equals(
                GetStateString(state, "fingerprint"),
                fingerprints.Aggregate,
                StringComparison.OrdinalIgnoreCase);

            CheckComponent(
                request.GenerateProductionAgreement,
                "合作协议 PDF",
                "core_fingerprint",
                fingerprints.Core,
                "core_completed",
                () => IsCoreOutputCurrent(context, request));
            CheckComponent(
                request.GenerateSourceFileScreenshots,
                "原始文件信息",
                "source_info_fingerprint",
                fingerprints.SourceInfo,
                "source_file_screenshots_completed",
                () => TikTokSourceFileInfoScreenshotService.HasCurrentOutput(context.WorkflowProjectDir) &&
                      TikTokSourceFileInfoUploadPackageService.HasCurrentOutput(
                          context.WorkflowProjectDir,
                          request.IncludeSourceInfoRoleSceneScreenshot,
                          selection));
            CheckComponent(
                request.GenerateAiGenerationScreenshots,
                "AI 生成过程截图",
                "ai_screenshot_fingerprint",
                fingerprints.AiScreenshots,
                "ai_generation_screenshots_completed",
                () => TikTokAiGenerationScreenshotService.HasCurrentOutput(context.WorkflowProjectDir));
            CheckComponent(
                request.GenerateEditingProjectFiles,
                "剪辑工程文件",
                "editing_project_fingerprint",
                fingerprints.EditingProject,
                "editing_project_files_completed",
                () => TikTokProjectImageService.HasCurrentProjectImages(context.SourceProjectDir, settings));

            return issues;

            void CheckComponent(
                bool selected,
                string displayName,
                string fingerprintKey,
                string expectedFingerprint,
                string completedKey,
                Func<bool> filesAreCurrent)
            {
                if (!selected)
                    return;
                if (!IsComponentCheckpointCurrent(
                        state,
                        fingerprintKey,
                        expectedFingerprint,
                        legacyFingerprintMatches))
                {
                    var previousTitle = GetStateString(state, "drama_title");
                    issues.Add(!string.IsNullOrWhiteSpace(previousTitle) &&
                               !string.Equals(previousTitle, request.DramaTitle, StringComparison.Ordinal)
                        ? $"{displayName}仍对应旧剧名「{previousTitle}」，当前为「{request.DramaTitle}」"
                        : $"{displayName}的配置或输入已变化");
                    return;
                }
                if (!GetStateBool(state, completedKey, fallback: true))
                {
                    issues.Add($"{displayName}尚未完成");
                    return;
                }
                if (!filesAreCurrent())
                    issues.Add($"{displayName}文件缺失、损坏或版本过旧");
            }
        }
        catch (Exception ex)
        {
            issues.Add(ex.Message);
            return issues;
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
            include_source_info_role_vector = request.IncludeSourceInfoRoleVector,
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

    internal static TikTokProofMaterialFingerprints ComputeComponentFingerprints(
        TikTokProofMaterialRequest request,
        TikTokSourceFileInfoPackageSelection sourceInfoSelection)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sourceInfoSelection);
        return new TikTokProofMaterialFingerprints(
            Aggregate: ComputeFingerprint(request),
            Core: HashFingerprint(new
            {
                version = ComponentFingerprintVersion + ":core",
                enabled = request.GenerateProductionAgreement,
                template_sha256 = request.GenerateProductionAgreement
                    ? ComputeFileSha256(request.TemplateDocxPath, "证明材料 Word 模板")
                    : "skipped",
                copyright_company = request.CopyrightCompanyName.Trim(),
                declarant_company = request.DeclarantCompanyName.Trim(),
                drama_title = request.DramaTitle.Trim(),
                statement_date = request.StatementDate.ToString("yyyy-MM-dd"),
                seal_sha256 = !request.GenerateProductionAgreement
                    ? "skipped"
                    : string.IsNullOrWhiteSpace(request.SealImagePath)
                        ? "template-seal"
                        : ComputeFileSha256(request.SealImagePath, "证明材料印章图片"),
                renderer = request.PreferredPdfRenderer.ToString(),
                wps_executable_path = (request.WpsExecutablePath ?? string.Empty).Trim(),
                keep_docx = request.KeepIntermediateDocx,
            }),
            SourceInfo: HashFingerprint(new
            {
                version = ComponentFingerprintVersion + ":source-info:" +
                          TikTokSourceFileInfoScreenshotService.ScreenshotVersion,
                enabled = request.GenerateSourceFileScreenshots,
                drama_title = request.DramaTitle.Trim(),
                copyright_company = request.CopyrightCompanyName.Trim(),
                include_role_scene = request.IncludeSourceInfoRoleSceneScreenshot,
                include_outline = sourceInfoSelection.IncludeOutline,
                include_script = sourceInfoSelection.IncludeScript,
                include_role_vector = sourceInfoSelection.IncludeRoleVector,
            }),
            AiScreenshots: HashFingerprint(new
            {
                version = ComponentFingerprintVersion + ":ai:" +
                          TikTokAiGenerationScreenshotService.ScreenshotVersion,
                enabled = request.GenerateAiGenerationScreenshots,
                drama_title = request.DramaTitle.Trim(),
            }),
            EditingProject: HashFingerprint(new
            {
                version = ComponentFingerprintVersion + ":editing:" +
                          TikTokProjectImageService.OutputDirectoryName + ":v4-dedicated-folder",
                enabled = request.GenerateEditingProjectFiles,
                drama_title = request.DramaTitle.Trim(),
            }));
    }

    private static string HashFingerprint(object payload)
    {
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
            IncludeSourceInfoRoleVector = account.TiktokUploadSourceInfoRoleVector,
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
        TikTokProofMaterialFingerprints fingerprints,
        ClientSettings settings,
        TikTokSourceFileInfoPackageSelection sourceInfoSelection)
    {
        var state = LoadState(context);
        var legacyFingerprintMatches = string.Equals(
            GetStateString(state, "fingerprint"),
            fingerprints.Aggregate,
            StringComparison.OrdinalIgnoreCase);
        if ((request.GenerateProductionAgreement &&
             (!IsComponentCheckpointCurrent(state, "core_fingerprint", fingerprints.Core, legacyFingerprintMatches) ||
              !GetStateBool(state, "core_completed", fallback: true))) ||
            (request.GenerateSourceFileScreenshots &&
             (!IsComponentCheckpointCurrent(state, "source_info_fingerprint", fingerprints.SourceInfo, legacyFingerprintMatches) ||
              !GetStateBool(state, "source_file_screenshots_completed", fallback: true))) ||
            (request.GenerateAiGenerationScreenshots &&
             (!IsComponentCheckpointCurrent(state, "ai_screenshot_fingerprint", fingerprints.AiScreenshots, legacyFingerprintMatches) ||
              !GetStateBool(state, "ai_generation_screenshots_completed", fallback: true))) ||
            (request.GenerateEditingProjectFiles &&
             (!IsComponentCheckpointCurrent(state, "editing_project_fingerprint", fingerprints.EditingProject, legacyFingerprintMatches) ||
              !GetStateBool(state, "editing_project_files_completed", fallback: true))))
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
                 request.IncludeSourceInfoRoleSceneScreenshot,
                 sourceInfoSelection)))
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

    internal static Dictionary<string, JsonElement> LoadState(ProjectWorkspaceContext context)
    {
        var sidecarPath = Path.Combine(context.WorkflowProjectDir, StateSidecarFileName);
        if (File.Exists(sidecarPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(sidecarPath));
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return document.RootElement.EnumerateObject()
                        .ToDictionary(
                            property => property.Name,
                            property => property.Value.Clone(),
                            StringComparer.Ordinal);
                }
            }
            catch
            {
                // 侧车损坏时仍可回退数据库断点；下一次成功生成会原子覆盖该文件。
            }
        }

        return ProjectStateDocumentStore.LoadDocument(
            context.WorkspaceRoot,
            context.SourceProjectDir,
            StateDocumentType);
    }

    internal static void SaveState(
        ProjectWorkspaceContext context,
        TikTokProofMaterialRequest request,
        string fingerprint,
        TikTokProofMaterialResult result,
        bool coreCompleted,
        bool sourceFileScreenshotsCompleted,
        bool aiGenerationScreenshotsCompleted,
        bool editingProjectFilesCompleted,
        Action<string>? log = null)
        => SaveState(
            context,
            request,
            new TikTokProofMaterialFingerprints(
                fingerprint,
                fingerprint,
                fingerprint,
                fingerprint,
                fingerprint),
            result,
            coreCompleted,
            sourceFileScreenshotsCompleted,
            aiGenerationScreenshotsCompleted,
            editingProjectFilesCompleted,
            log);

    internal static void SaveState(
        ProjectWorkspaceContext context,
        TikTokProofMaterialRequest request,
        TikTokProofMaterialFingerprints fingerprints,
        TikTokProofMaterialResult result,
        bool coreCompleted,
        bool sourceFileScreenshotsCompleted,
        bool aiGenerationScreenshotsCompleted,
        bool editingProjectFilesCompleted,
        Action<string>? log = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["fingerprint"] = fingerprints.Aggregate,
            ["component_fingerprint_version"] = ComponentFingerprintVersion,
            ["core_fingerprint"] = fingerprints.Core,
            ["source_info_fingerprint"] = fingerprints.SourceInfo,
            ["ai_screenshot_fingerprint"] = fingerprints.AiScreenshots,
            ["editing_project_fingerprint"] = fingerprints.EditingProject,
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
        Exception? databaseError = null;
        try
        {
            ProjectStateDocumentStore.SaveDocument(
                context.WorkspaceRoot,
                context.SourceProjectDir,
                StateDocumentType,
                payload,
                context.WorkflowProjectDir);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            databaseError = ex;
        }

        var sidecarPath = Path.Combine(context.WorkflowProjectDir, StateSidecarFileName);
        var temporaryPath = sidecarPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(context.WorkflowProjectDir);
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
            File.Move(temporaryPath, sidecarPath, overwrite: true);
        }
        catch (Exception sidecarError)
        {
            if (databaseError is not null)
            {
                throw new InvalidOperationException(
                    $"证明材料已生成，但数据库断点和本地侧车状态均保存失败：" +
                    $"数据库={databaseError.Message}；侧车={sidecarError.Message}",
                    new AggregateException(databaseError, sidecarError));
            }

            log?.Invoke($"WARN 证明材料数据库断点已保存，但本地侧车状态保存失败：{sidecarError.Message}");
        }
        finally
        {
            TryDelete(temporaryPath);
        }

        if (databaseError is not null)
        {
            log?.Invoke(
                $"WARN 工作目录数据库暂时无法写入，已使用本地侧车断点继续：" +
                $"{Path.GetFileName(sidecarPath)}；原因={databaseError.Message}");
        }
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

    private static bool IsComponentCheckpointCurrent(
        IReadOnlyDictionary<string, JsonElement> state,
        string key,
        string expectedFingerprint,
        bool legacyAggregateMatches)
    {
        var componentFingerprint = GetStateString(state, key);
        return string.IsNullOrWhiteSpace(componentFingerprint)
            ? legacyAggregateMatches
            : string.Equals(componentFingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase);
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

internal sealed record TikTokProofMaterialFingerprints(
    string Aggregate,
    string Core,
    string SourceInfo,
    string AiScreenshots,
    string EditingProject);
