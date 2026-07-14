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

    private const string FingerprintVersion = "v3-embedded-template-wps-path";
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
        var request = CreateQueueRequest(item, settings, account, context.WorkflowProjectDir);
        var fingerprint = ComputeFingerprint(request);
        var outputDocxPath = GetDocxPath(context.WorkflowProjectDir);

        if (!request.KeepIntermediateDocx)
        {
            TryDelete(outputDocxPath);
        }

        if (!forceRerun && HasCurrentOutput(context, request, fingerprint))
        {
            var state = LoadState(context);
            var renderer = GetStateString(state, "renderer");
            log?.Invoke($"{ProofPdfFileName} 已存在且配置未变化，跳过生成。");
            return new TikTokProofMaterialResult(
                request.OutputPdfPath,
                request.KeepIntermediateDocx ? outputDocxPath : null,
                string.IsNullOrWhiteSpace(renderer) ? "WPS" : renderer,
                new TikTokProofMaterialReplacementCounts(0, 0, 0, 0, 0));
        }

        var service = new TikTokProofMaterialService();
        var result = await service.GenerateAsync(request, log, cancellationToken).ConfigureAwait(false);
        if (!request.KeepIntermediateDocx)
        {
            TryDelete(outputDocxPath);
        }

        SaveState(context, request, fingerprint, result);
        return result;
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
        if (!TikTokPublishConstants.RequiresGeneratedProofMaterial(materialTypes))
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
            var request = CreateQueueRequest(item, settings, account, context.WorkflowProjectDir);
            var fingerprint = ComputeFingerprint(request);
            return !HasCurrentOutput(context, request, fingerprint);
        }
        catch
        {
            // If configuration or inputs are invalid, the selected queue step must run and
            // report the actionable validation error rather than being silently skipped.
            return true;
        }
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
            template_sha256 = ComputeFileSha256(request.TemplateDocxPath),
            copyright_company = request.CopyrightCompanyName.Trim(),
            declarant_company = request.DeclarantCompanyName.Trim(),
            drama_title = request.DramaTitle.Trim(),
            statement_date = request.StatementDate.ToString("yyyy-MM-dd"),
            seal_sha256 = string.IsNullOrWhiteSpace(request.SealImagePath)
                ? "template-seal"
                : ComputeFileSha256(request.SealImagePath),
            renderer = request.PreferredPdfRenderer == TikTokProofMaterialPdfRendererPreference.Wps
                ? "wps"
                : "libreoffice",
            wps_executable_path = (request.WpsExecutablePath ?? string.Empty).Trim(),
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

        var accountConfigMigrated = account?.TiktokProofAccountConfigMigrated == true;
        var declarantCompanyName = FirstNonEmpty(
            account?.TiktokProofDeclarantCompanyName,
            accountConfigMigrated ? null : settings.TiktokProofDeclarantCompanyName);
        var sealPath = accountConfigMigrated
            ? (account?.TiktokProofSealPath ?? string.Empty).Trim()
            : FirstNonEmpty(account?.TiktokProofSealPath, settings.TiktokProofSealPath);

        return new TikTokProofMaterialRequest(
            TikTokProofMaterialTemplateProvider.ResolveTemplatePath(settings.TiktokProofTemplateDocxPath),
            GetPdfPath(workflowProjectDirectory),
            account?.TiktokProofCopyrightCompanyName ?? string.Empty,
            declarantCompanyName,
            item.NewTitle.Trim(),
            statementDate ?? GetChinaToday())
        {
            SealImagePath = sealPath,
            PreferredPdfRenderer = TikTokProofMaterialPdfRendererPreferenceExtensions.Parse(
                settings.TiktokProofPdfRenderer),
            WpsExecutablePath = settings.TiktokProofWpsPath,
            KeepIntermediateDocx = settings.TiktokProofKeepDocx,
        };
    }

    private static bool HasCurrentOutput(
        ProjectWorkspaceContext context,
        TikTokProofMaterialRequest request,
        string fingerprint)
    {
        try
        {
            TikTokProofMaterialPdfRenderService.ValidatePdf(request.OutputPdfPath);
        }
        catch
        {
            return false;
        }

        if (request.KeepIntermediateDocx && !File.Exists(GetDocxPath(context.WorkflowProjectDir)))
        {
            return false;
        }

        var state = LoadState(context);
        return string.Equals(
            GetStateString(state, "fingerprint"),
            fingerprint,
            StringComparison.OrdinalIgnoreCase);
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
        TikTokProofMaterialResult result)
    {
        var payload = new Dictionary<string, object?>
        {
            ["fingerprint"] = fingerprint,
            ["pdf_path"] = Path.GetFullPath(result.PdfPath),
            ["docx_path"] = string.IsNullOrWhiteSpace(result.IntermediateDocxPath)
                ? string.Empty
                : Path.GetFullPath(result.IntermediateDocxPath),
            ["template_path"] = Path.GetFullPath(request.TemplateDocxPath),
            ["copyright_company"] = request.CopyrightCompanyName.Trim(),
            ["declarant_company"] = request.DeclarantCompanyName.Trim(),
            ["drama_title"] = request.DramaTitle.Trim(),
            ["statement_date"] = request.StatementDate.ToString("yyyy-MM-dd"),
            ["renderer"] = result.PdfRenderer,
            ["wps_executable_path"] = (request.WpsExecutablePath ?? string.Empty).Trim(),
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

    private static string ComputeFileSha256(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("计算证明材料指纹时未找到文件。", path);
        }

        using var stream = File.OpenRead(path);
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
