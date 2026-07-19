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

    private const string FingerprintVersion = "v5-seal-orientation";
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
            template_sha256 = ComputeFileSha256(request.TemplateDocxPath, "证明材料 Word 模板"),
            copyright_company = request.CopyrightCompanyName.Trim(),
            declarant_company = request.DeclarantCompanyName.Trim(),
            drama_title = request.DramaTitle.Trim(),
            statement_date = request.StatementDate.ToString("yyyy-MM-dd"),
            seal_sha256 = string.IsNullOrWhiteSpace(request.SealImagePath)
                ? "template-seal"
                : ComputeFileSha256(request.SealImagePath, "证明材料印章图片"),
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
        sealPath = ResolveSealImagePath(sealPath);

        return new TikTokProofMaterialRequest(
            TikTokProofMaterialTemplateProvider.ResolveTemplatePath(settings.TiktokProofTemplateDocxPath),
            GetPdfPath(workflowProjectDirectory),
            account.TiktokProofCopyrightCompanyName ?? string.Empty,
            declarantCompanyName,
            item.NewTitle.Trim(),
            statementDate ?? ResolveStatementDate(item))
        {
            SealImagePath = sealPath,
            PreferredPdfRenderer = TikTokProofMaterialPdfRendererPreferenceExtensions.Parse(
                settings.TiktokProofPdfRenderer),
            WpsExecutablePath = settings.TiktokProofWpsPath,
            KeepIntermediateDocx = settings.TiktokProofKeepDocx,
        };
    }

    internal static DateOnly ResolveStatementDate(QueueProjectItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (DateOnly.TryParseExact(
                item.ProofMaterialStatementDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var restoredArchiveDate))
        {
            return restoredArchiveDate;
        }

        return GetChinaToday();
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
