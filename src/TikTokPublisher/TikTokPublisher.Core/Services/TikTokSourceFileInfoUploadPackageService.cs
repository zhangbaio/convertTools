using SixLabors.ImageSharp;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public sealed record TikTokSourceFileInfoPackageSelection(
    bool IncludeOutline,
    bool IncludeScript,
    bool IncludeRoleVector,
    bool IncludeRoleSceneScreenshot)
{
    public static TikTokSourceFileInfoPackageSelection LegacyDefault(
        bool includeRoleSceneScreenshot = false) =>
        new(true, true, true, includeRoleSceneScreenshot);

    public static TikTokSourceFileInfoPackageSelection FromEnabledSteps(
        IEnumerable<string>? enabledSteps,
        bool includeRoleVector,
        bool includeRoleSceneScreenshot)
    {
        if (enabledSteps is null)
            return WithPlatformMinimum(new TikTokSourceFileInfoPackageSelection(
                true, true, includeRoleVector, includeRoleSceneScreenshot));
        var enabled = enabledSteps.ToHashSet(StringComparer.Ordinal);
        return WithPlatformMinimum(new TikTokSourceFileInfoPackageSelection(
            enabled.Contains(QueueStepRegistry.GenerateAiScriptOutline),
            enabled.Contains(QueueStepRegistry.GenerateEpisodeScript),
            includeRoleVector,
            includeRoleSceneScreenshot));
    }

    internal static TikTokSourceFileInfoPackageSelection WithPlatformMinimum(
        TikTokSourceFileInfoPackageSelection selection)
    {
        var count = 1 +
                    (selection.IncludeOutline ? 1 : 0) +
                    (selection.IncludeScript ? 1 : 0) +
                    (selection.IncludeRoleVector ? 1 : 0) +
                    (selection.IncludeRoleSceneScreenshot ? 1 : 0);
        return count >= TikTokSourceFileInfoUploadPackageService.RequiredFileCount ||
               selection.IncludeRoleSceneScreenshot
            ? selection
            : selection with { IncludeRoleSceneScreenshot = true };
    }
}

internal sealed record TikTokSourceFileInfoPrerequisites(
    string? OutlinePdf,
    string? ScriptPdf,
    string? RoleVectorImage);

/// <summary>
/// 为 TikTok「原始文件或素材文件信息」整理上传包：固定项目资料截图，
/// 以及按账号和生产步骤选择的剧本、AI 大纲、角色矢量图和角色场景素材截图。
/// </summary>
public static class TikTokSourceFileInfoUploadPackageService
{
    public const string OutputDirectoryName = "原始文件信息上传";
    public const int RequiredFileCount = 4;
    public const string OutlineFileName = "AI剧本大纲.pdf";
    public const string ScriptFileName = "剧本.pdf";
    public const string ProjectInfoImageFileName = "01_剧本与项目资料.png";
    internal const string LegacyProjectInfoImageFileName = "01_AI剧本与项目资料.png";
    public const string RoleSceneImageFileName = "02_角色场景或项目素材.png";
    public const string RoleVectorImageFileName = "角色矢量图.png";

    private static readonly string[] AllFileNames =
    [
        OutlineFileName,
        ScriptFileName,
        ProjectInfoImageFileName,
        RoleVectorImageFileName,
    ];

    public static string GetOutputDirectory(string workflowProjectDirectory) =>
        Path.Combine(Path.GetFullPath(workflowProjectDirectory), OutputDirectoryName);

    public static IReadOnlyList<string> GetExpectedOutputPaths(
        string workflowProjectDirectory,
        bool includeRoleSceneScreenshot = false) =>
        GetExpectedOutputPaths(
            workflowProjectDirectory,
            TikTokSourceFileInfoPackageSelection.LegacyDefault(includeRoleSceneScreenshot));

    public static IReadOnlyList<string> GetExpectedOutputPaths(
        string workflowProjectDirectory,
        TikTokSourceFileInfoPackageSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        selection = NormalizeAndValidateSelection(selection);
        var outputDirectory = GetOutputDirectory(workflowProjectDirectory);
        var names = ExpectedFileNames(selection);
        return names.Select(name => Path.Combine(outputDirectory, name)).ToArray();
    }

    public static IReadOnlyList<string> ListFiles(
        string workflowProjectDirectory,
        bool includeRoleSceneScreenshot = false,
        TikTokSourceFileInfoPackageSelection? selection = null) =>
        GetExpectedOutputPaths(
                workflowProjectDirectory,
                selection ?? TikTokSourceFileInfoPackageSelection.LegacyDefault(includeRoleSceneScreenshot))
            .Where(File.Exists)
            .ToArray();

    internal static TikTokSourceFileInfoPrerequisites ValidateExistingPrerequisites(
        string workflowProjectDirectory,
        TikTokSourceFileInfoPackageSelection? selection = null)
    {
        selection = NormalizeAndValidateSelection(
            selection ?? TikTokSourceFileInfoPackageSelection.LegacyDefault());
        var workflow = Path.GetFullPath(workflowProjectDirectory);
        var outline = selection.IncludeOutline
            ? ResolveRequiredFile(
                null,
                Path.Combine(workflow, TikTokAiScriptOutlineService.OutputFileName),
                "AI 大纲 PDF",
                "请先执行“生成AI大纲”步骤。")
            : null;
        var script = selection.IncludeScript
            ? ResolveRequiredFile(
                null,
                FindScriptPdf(workflow),
                "剧本 PDF",
                "请先执行“生成剧本”步骤。")
            : null;
        var roleVector = selection.IncludeRoleVector
            ? ResolveRequiredFile(
                null,
                Path.Combine(
                    TikTokReferenceSourcePackageService.GetRoot(workflow),
                    TikTokReferenceSourcePackageService.CharacterWorkbenchFileName),
                "角色矢量图",
                "请先执行“生成角色矢量图”步骤。")
            : null;

        if (outline is not null) ValidatePdf(outline, "AI 大纲 PDF");
        if (script is not null) ValidatePdf(script, "剧本 PDF");
        if (roleVector is not null) ValidatePng(roleVector, "角色矢量图", requireRoleVectorSize: true);
        return new TikTokSourceFileInfoPrerequisites(outline, script, roleVector);
    }

    internal static TikTokSourceFileInfoPrerequisites ResolveAvailablePrerequisites(
        string workflowProjectDirectory,
        TikTokSourceFileInfoPackageSelection selection)
    {
        selection = NormalizeAndValidateSelection(selection);
        var workflow = Path.GetFullPath(workflowProjectDirectory);
        return new TikTokSourceFileInfoPrerequisites(
            selection.IncludeOutline
                ? ResolveOptionalFile(Path.Combine(workflow, TikTokAiScriptOutlineService.OutputFileName))
                : null,
            selection.IncludeScript ? ResolveOptionalFile(FindScriptPdf(workflow)) : null,
            selection.IncludeRoleVector
                ? ResolveOptionalFile(Path.Combine(
                    TikTokReferenceSourcePackageService.GetRoot(workflow),
                    TikTokReferenceSourcePackageService.CharacterWorkbenchFileName))
                : null);
    }

    public static bool HasCurrentOutput(
        string workflowProjectDirectory,
        bool includeRoleSceneScreenshot = false,
        TikTokSourceFileInfoPackageSelection? selection = null)
    {
        try
        {
            Validate(workflowProjectDirectory, includeRoleSceneScreenshot, selection);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<string> Generate(
        string workflowProjectDirectory,
        string? outlinePdfPath = null,
        string? scriptPdfPath = null,
        Action<string>? log = null,
        bool includeRoleSceneScreenshot = false,
        TikTokSourceFileInfoPackageSelection? selection = null,
        bool validateComplete = true)
    {
        selection = NormalizeAndValidateSelection(
            selection ?? TikTokSourceFileInfoPackageSelection.LegacyDefault(includeRoleSceneScreenshot));
        var workflow = Path.GetFullPath(workflowProjectDirectory);
        var outline = selection.IncludeOutline
            ? validateComplete
                ? ResolveRequiredFile(
                    outlinePdfPath,
                    Path.Combine(workflow, TikTokAiScriptOutlineService.OutputFileName),
                    "AI 大纲 PDF",
                    "请先执行“生成AI大纲”步骤。")
                : ResolveOptionalFile(FirstExisting(outlinePdfPath,
                    Path.Combine(workflow, TikTokAiScriptOutlineService.OutputFileName)))
            : null;
        var script = selection.IncludeScript
            ? validateComplete
                ? ResolveRequiredFile(
                    scriptPdfPath,
                    FindScriptPdf(workflow),
                    "剧本 PDF",
                    "请先执行“生成剧本”步骤。")
                : ResolveOptionalFile(FirstExisting(scriptPdfPath, FindScriptPdf(workflow)))
            : null;
        var outputDirectory = GetOutputDirectory(workflow);
        var projectInfo = ResolveRequiredFile(
            null,
            Path.Combine(outputDirectory, ProjectInfoImageFileName),
            "项目资料截图",
            "请先执行“生成证明材料”步骤。");
        var roleScene = selection.IncludeRoleSceneScreenshot
            ? ResolveRequiredFile(
                null,
                Path.Combine(outputDirectory, RoleSceneImageFileName),
                "角色场景素材截图",
                "请先执行“生成证明材料”步骤。")
            : string.Empty;
        var roleVector = selection.IncludeRoleVector
            ? validateComplete
                ? ResolveRequiredFile(
                    null,
                    Path.Combine(
                        TikTokReferenceSourcePackageService.GetRoot(workflow),
                        TikTokReferenceSourcePackageService.CharacterWorkbenchFileName),
                    "角色矢量图",
                    "请先执行“生成角色矢量图”步骤。")
                : ResolveOptionalFile(Path.Combine(
                    TikTokReferenceSourcePackageService.GetRoot(workflow),
                    TikTokReferenceSourcePackageService.CharacterWorkbenchFileName))
            : null;

        if (validateComplete && outline is not null) ValidatePdf(outline, "AI 大纲 PDF");
        if (validateComplete && script is not null) ValidatePdf(script, "剧本 PDF");
        ValidatePng(projectInfo, "项目资料截图", requireRoleVectorSize: false);
        if (selection.IncludeRoleSceneScreenshot)
            ValidatePng(roleScene, "角色场景素材截图", requireRoleVectorSize: false);
        if (validateComplete && roleVector is not null)
            ValidatePng(roleVector, "角色矢量图", requireRoleVectorSize: true);

        Directory.CreateDirectory(outputDirectory);
        DeleteFilesNotSelected(outputDirectory, ExpectedFileNames(selection));
        if (outline is not null) Copy(outline, Path.Combine(outputDirectory, OutlineFileName));
        if (script is not null) Copy(script, Path.Combine(outputDirectory, ScriptFileName));
        Copy(projectInfo, Path.Combine(outputDirectory, ProjectInfoImageFileName));
        if (roleVector is not null) Copy(roleVector, Path.Combine(outputDirectory, RoleVectorImageFileName));
        if (selection.IncludeRoleSceneScreenshot)
            Copy(roleScene, Path.Combine(outputDirectory, RoleSceneImageFileName));
        if (validateComplete)
            Validate(workflow, includeRoleSceneScreenshot, selection);
        var outputs = ListFiles(workflow, includeRoleSceneScreenshot, selection);
        if (!validateComplete)
        {
            var missing = ExpectedFileNames(selection)
                .Where(name => !File.Exists(Path.Combine(outputDirectory, name)))
                .ToArray();
            if (missing.Length > 0)
                log?.Invoke($"WARN 原始文件信息上传包尚缺：{string.Join("、", missing)}；由成片检查统一校验。");
        }
        log?.Invoke(
            $"原始文件信息上传包已整理：" +
            $"{string.Join("、", outputs.Select(Path.GetFileName))} → {outputDirectory}");
        return outputs;
    }

    public static void RefreshRoleDerivedImages(
        string workflowProjectDirectory,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var workflow = Path.GetFullPath(workflowProjectDirectory);
        var outputDirectory = GetOutputDirectory(workflow);
        if (!Directory.Exists(outputDirectory)) return;

        var roleVectorSource = Path.Combine(
            TikTokReferenceSourcePackageService.GetRoot(workflow),
            TikTokReferenceSourcePackageService.CharacterWorkbenchFileName);
        var roleVectorDestination = Path.Combine(outputDirectory, RoleVectorImageFileName);
        if (File.Exists(roleVectorDestination))
        {
            ValidatePng(roleVectorSource, "角色矢量图", requireRoleVectorSize: true);
            SyncRoleVectorCopy(roleVectorSource, roleVectorDestination);
        }
        cancellationToken.ThrowIfCancellationRequested();
        TikTokSourceFileInfoScreenshotService.RefreshRoleSceneScreenshot(
            workflow, log, cancellationToken);
        log?.Invoke(File.Exists(roleVectorDestination)
            ? "原始文件信息上传：角色矢量图与角色场景截图已同步更新。"
            : "原始文件信息上传：未选择上传角色矢量图，仅同步角色场景截图。");
    }

    internal static void SyncRoleVectorCopy(string source, string destination)
    {
        var sourceFullPath = Path.GetFullPath(source);
        var destinationFullPath = Path.GetFullPath(destination);
        if (string.Equals(sourceFullPath, destinationFullPath, StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFullPath)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(destinationFullPath)!,
            $".{Path.GetFileName(destinationFullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourceFullPath, temporary, overwrite: true);
            File.Move(temporary, destinationFullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public static void Validate(
        string workflowProjectDirectory,
        bool includeRoleSceneScreenshot = false,
        TikTokSourceFileInfoPackageSelection? selection = null)
    {
        selection = NormalizeAndValidateSelection(
            selection ?? TikTokSourceFileInfoPackageSelection.LegacyDefault(includeRoleSceneScreenshot));
        var files = GetExpectedOutputPaths(workflowProjectDirectory, selection);
        var outputDirectory = GetOutputDirectory(workflowProjectDirectory);
        var actualFiles = Directory.Exists(outputDirectory)
            ? Directory.EnumerateFiles(outputDirectory).ToArray()
            : [];
        var expectedNames = ExpectedFileNames(selection);
        var expectedCount = expectedNames.Count;
        var allowedNames = expectedNames
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (files.Count != expectedCount || files.Any(path => !File.Exists(path)) ||
            actualFiles.Any(path => !allowedNames.Contains(Path.GetFileName(path))))
            throw new FileNotFoundException(
                $"原始文件信息上传包必须包含 {expectedCount} 个上传文件：" +
                $"{string.Join("、", expectedNames)}。");
        foreach (var file in files)
        {
            switch (Path.GetFileName(file))
            {
                case OutlineFileName:
                    ValidatePdf(file, "AI 大纲 PDF");
                    break;
                case ScriptFileName:
                    ValidatePdf(file, "剧本 PDF");
                    break;
                case RoleVectorImageFileName:
                    ValidatePng(file, "角色矢量图", requireRoleVectorSize: true);
                    break;
                default:
                    ValidatePng(file, "项目资料截图", requireRoleVectorSize: false);
                    break;
            }
        }
    }

    /// <summary>
    /// Rebuilds a stale or partial generated upload folder exclusively from products
    /// that already exist on disk. This is used by final material validation after
    /// upgrades and never invokes AI, video processing, or screenshot capture.
    /// </summary>
    public static bool EnsureCurrentFromExistingOutputs(
        string workflowProjectDirectory,
        bool includeRoleSceneScreenshot = false,
        TikTokSourceFileInfoPackageSelection? selection = null,
        Action<string>? log = null)
    {
        selection = NormalizeAndValidateSelection(
            selection ?? TikTokSourceFileInfoPackageSelection.LegacyDefault(includeRoleSceneScreenshot));
        try
        {
            Validate(workflowProjectDirectory, includeRoleSceneScreenshot, selection);
            return false;
        }
        catch
        {
            // Continue with a local-only rebuild below.
        }

        var workflow = Path.GetFullPath(workflowProjectDirectory);
        MigrateLegacyProjectInfoScreenshot(workflow, log);
        var prerequisites = ValidateExistingPrerequisites(workflow, selection);
        Generate(
            workflow,
            prerequisites.OutlinePdf,
            prerequisites.ScriptPdf,
            log,
            includeRoleSceneScreenshot,
            selection,
            validateComplete: true);
        log?.Invoke("成片检查：已使用现有产物自动修复原始文件信息上传包，无需重新生成 AI 内容。");
        return true;
    }

    public static int RequiredFileCountFor(TikTokSourceFileInfoPackageSelection selection) =>
        ExpectedFileNames(NormalizeAndValidateSelection(selection)).Count;

    private static TikTokSourceFileInfoPackageSelection NormalizeAndValidateSelection(
        TikTokSourceFileInfoPackageSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var normalized = TikTokSourceFileInfoPackageSelection.WithPlatformMinimum(selection);
        var count = ExpectedFileNames(normalized).Count;
        if (count < RequiredFileCount)
        {
            throw new InvalidOperationException(
                $"TikTok“原始文件或素材文件信息”至少需要 {RequiredFileCount} 个文件，" +
                $"当前上传材料配置只能生成 {count} 个。请启用“生成AI大纲”和“生成剧本”，" +
                "或启用角色矢量图等可上传素材。");
        }
        return normalized;
    }

    private static IReadOnlyList<string> ExpectedFileNames(TikTokSourceFileInfoPackageSelection selection)
    {
        var names = new List<string>();
        if (selection.IncludeOutline) names.Add(OutlineFileName);
        if (selection.IncludeScript) names.Add(ScriptFileName);
        names.Add(ProjectInfoImageFileName);
        if (selection.IncludeRoleVector) names.Add(RoleVectorImageFileName);
        if (selection.IncludeRoleSceneScreenshot) names.Add(RoleSceneImageFileName);
        return names;
    }

    private static void DeleteFilesNotSelected(string outputDirectory, IReadOnlyList<string> expectedNames)
    {
        if (!Directory.Exists(outputDirectory)) return;
        var expected = expectedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(outputDirectory))
        {
            if (AllFileNames.Append(RoleSceneImageFileName).Contains(
                    Path.GetFileName(path),
                    StringComparer.OrdinalIgnoreCase) &&
                !expected.Contains(Path.GetFileName(path)))
            {
                File.Delete(path);
            }
        }
    }

    private static void MigrateLegacyProjectInfoScreenshot(
        string workflowProjectDirectory,
        Action<string>? log)
    {
        var outputDirectory = GetOutputDirectory(workflowProjectDirectory);
        var canonical = Path.Combine(outputDirectory, ProjectInfoImageFileName);
        if (File.Exists(canonical)) return;

        var candidates = new[]
        {
            Path.Combine(outputDirectory, LegacyProjectInfoImageFileName),
            Path.Combine(workflowProjectDirectory, "原始文件或素材文件信息", LegacyProjectInfoImageFileName),
            Path.Combine(workflowProjectDirectory, "原始文件信息截图", LegacyProjectInfoImageFileName),
        };
        var legacy = candidates.FirstOrDefault(File.Exists);
        if (legacy is null) return;

        Directory.CreateDirectory(outputDirectory);
        File.Copy(legacy, canonical, overwrite: true);
        if (string.Equals(
                Path.GetDirectoryName(legacy),
                outputDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(legacy);
        }
        log?.Invoke(
            $"原始文件信息上传包：已兼容旧版截图名称 {LegacyProjectInfoImageFileName} → " +
            ProjectInfoImageFileName + "。");
    }

    internal static string? FindScriptPdf(string workflowProjectDirectory)
    {
        var workflow = Path.GetFullPath(workflowProjectDirectory);
        if (!Directory.Exists(workflow)) return null;
        return Directory.EnumerateFiles(workflow, "*.pdf", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).Contains("剧本", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).Equals(
                TikTokAiScriptOutlineService.OutputFileName,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => Path.GetFileNameWithoutExtension(path)
                .EndsWith("前5集剧本", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string ResolveRequiredFile(
        string? preferred,
        string? fallback,
        string label,
        string recovery)
    {
        var candidate = string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
        if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
            throw new FileNotFoundException($"生成原始文件信息上传包失败：缺少{label}。{recovery}", candidate);
        return Path.GetFullPath(candidate);
    }

    private static string? ResolveOptionalFile(string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)
            ? Path.GetFullPath(candidate)
            : null;

    private static string? FirstExisting(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred)
            ? preferred
            : fallback;

    private static void ValidatePdf(string path, string label)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 5)
            throw new InvalidDataException($"{label}无效或为空：{path}");
        Span<byte> header = stackalloc byte[5];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) != header.Length || !header.SequenceEqual("%PDF-"u8))
            throw new InvalidDataException($"{label}不是有效 PDF：{path}");
    }

    private static void ValidatePng(string path, string label, bool requireRoleVectorSize)
    {
        var info = Image.Identify(path)
            ?? throw new InvalidDataException($"{label}不是有效图片：{path}");
        if (requireRoleVectorSize &&
            (info.Width != RoleVectorTemplateRenderer.CanvasWidth ||
             info.Height != RoleVectorTemplateRenderer.CanvasHeight))
        {
            throw new InvalidDataException(
                $"角色矢量图尺寸必须为 {RoleVectorTemplateRenderer.CanvasWidth}×" +
                $"{RoleVectorTemplateRenderer.CanvasHeight}，当前为 {info.Width}×{info.Height}。");
        }
        if (!requireRoleVectorSize && (info.Width < 800 || info.Height < 500))
            throw new InvalidDataException($"项目资料截图尺寸异常：{info.Width}×{info.Height}。");
    }

    private static void Copy(string source, string destination)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return;
        File.Copy(source, destination, overwrite: true);
    }

}
