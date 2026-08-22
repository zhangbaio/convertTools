using SixLabors.ImageSharp;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 为 TikTok「原始文件或素材文件信息」整理上传包：四个必传文件，
/// 以及账号可选的角色场景素材截图。
/// </summary>
public static class TikTokSourceFileInfoUploadPackageService
{
    public const string OutputDirectoryName = "原始文件信息上传";
    public const int RequiredFileCount = 4;
    public const string OutlineFileName = "AI剧本大纲.pdf";
    public const string ScriptFileName = "剧本.pdf";
    public const string ProjectInfoImageFileName = "01_剧本与项目资料.png";
    public const string RoleSceneImageFileName = "02_角色场景或项目素材.png";
    public const string RoleVectorImageFileName = "角色矢量图.png";

    private static readonly string[] FileNames =
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
        bool includeRoleSceneScreenshot = false)
    {
        var outputDirectory = GetOutputDirectory(workflowProjectDirectory);
        var names = includeRoleSceneScreenshot
            ? FileNames.Append(RoleSceneImageFileName)
            : FileNames;
        return names.Select(name => Path.Combine(outputDirectory, name)).ToArray();
    }

    public static IReadOnlyList<string> ListFiles(
        string workflowProjectDirectory,
        bool includeRoleSceneScreenshot = false) =>
        GetExpectedOutputPaths(workflowProjectDirectory, includeRoleSceneScreenshot)
            .Where(File.Exists)
            .ToArray();

    internal static (string OutlinePdf, string ScriptPdf, string RoleVectorImage)
        ValidateExistingPrerequisites(string workflowProjectDirectory)
    {
        var workflow = Path.GetFullPath(workflowProjectDirectory);
        var outline = ResolveRequiredFile(
            null,
            Path.Combine(workflow, TikTokAiScriptOutlineService.OutputFileName),
            "AI 大纲 PDF",
            "请先执行“生成AI大纲”步骤。");
        var script = ResolveRequiredFile(
            null,
            FindScriptPdf(workflow),
            "剧本 PDF",
            "请先执行“生成剧本”步骤。");
        var roleVector = ResolveRequiredFile(
            null,
            Path.Combine(
                TikTokReferenceSourcePackageService.GetRoot(workflow),
                TikTokReferenceSourcePackageService.CharacterWorkbenchFileName),
            "角色矢量图",
            "请先执行“生成角色矢量图”步骤。");

        ValidatePdf(outline, "AI 大纲 PDF");
        ValidatePdf(script, "剧本 PDF");
        ValidatePng(roleVector, "角色矢量图", requireRoleVectorSize: true);
        return (outline, script, roleVector);
    }

    public static bool HasCurrentOutput(
        string workflowProjectDirectory,
        bool includeRoleSceneScreenshot = false)
    {
        try
        {
            Validate(workflowProjectDirectory, includeRoleSceneScreenshot);
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
        bool includeRoleSceneScreenshot = false)
    {
        var workflow = Path.GetFullPath(workflowProjectDirectory);
        var outline = ResolveRequiredFile(
            outlinePdfPath,
            Path.Combine(workflow, TikTokAiScriptOutlineService.OutputFileName),
            "AI 大纲 PDF",
            "请先执行“生成AI大纲”步骤。");
        var script = ResolveRequiredFile(
            scriptPdfPath,
            FindScriptPdf(workflow),
            "剧本 PDF",
            "请先执行“生成剧本”步骤。");
        var outputDirectory = GetOutputDirectory(workflow);
        var projectInfo = ResolveRequiredFile(
            null,
            Path.Combine(outputDirectory, ProjectInfoImageFileName),
            "项目资料截图",
            "请先执行“生成证明材料”步骤。");
        var roleScene = includeRoleSceneScreenshot
            ? ResolveRequiredFile(
                null,
                Path.Combine(outputDirectory, RoleSceneImageFileName),
                "角色场景素材截图",
                "请先执行“生成证明材料”步骤。")
            : string.Empty;
        var roleVector = ResolveRequiredFile(
            null,
            Path.Combine(
                TikTokReferenceSourcePackageService.GetRoot(workflow),
                TikTokReferenceSourcePackageService.CharacterWorkbenchFileName),
            "角色矢量图",
            "请先执行“生成角色矢量图”步骤。");

        ValidatePdf(outline, "AI 大纲 PDF");
        ValidatePdf(script, "剧本 PDF");
        ValidatePng(projectInfo, "项目资料截图", requireRoleVectorSize: false);
        if (includeRoleSceneScreenshot)
            ValidatePng(roleScene, "角色场景素材截图", requireRoleVectorSize: false);
        ValidatePng(roleVector, "角色矢量图", requireRoleVectorSize: true);

        Directory.CreateDirectory(outputDirectory);
        var outputs = GetExpectedOutputPaths(workflow, includeRoleSceneScreenshot);
        Copy(outline, outputs[0]);
        Copy(script, outputs[1]);
        Copy(projectInfo, outputs[2]);
        Copy(roleVector, outputs[3]);
        if (includeRoleSceneScreenshot)
            Copy(roleScene, outputs[4]);
        Validate(workflow, includeRoleSceneScreenshot);
        log?.Invoke(
            $"原始文件信息上传包已生成：AI 大纲、剧本、项目资料截图、角色矢量图" +
            $"{(includeRoleSceneScreenshot ? "、角色场景素材截图" : string.Empty)} → {outputDirectory}");
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
        ValidatePng(roleVectorSource, "角色矢量图", requireRoleVectorSize: true);
        SyncRoleVectorCopy(roleVectorSource, Path.Combine(outputDirectory, RoleVectorImageFileName));
        cancellationToken.ThrowIfCancellationRequested();
        TikTokSourceFileInfoScreenshotService.RefreshRoleSceneScreenshot(
            workflow, log, cancellationToken);
        log?.Invoke("原始文件信息上传：角色矢量图与角色场景截图已同步更新。");
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
        bool includeRoleSceneScreenshot = false)
    {
        var files = GetExpectedOutputPaths(workflowProjectDirectory, includeRoleSceneScreenshot);
        var outputDirectory = GetOutputDirectory(workflowProjectDirectory);
        var actualFiles = Directory.Exists(outputDirectory)
            ? Directory.EnumerateFiles(outputDirectory).ToArray()
            : [];
        var expectedCount = RequiredFileCount + (includeRoleSceneScreenshot ? 1 : 0);
        var allowedNames = FileNames.Append(RoleSceneImageFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (files.Count != expectedCount || files.Any(path => !File.Exists(path)) ||
            actualFiles.Any(path => !allowedNames.Contains(Path.GetFileName(path))))
            throw new FileNotFoundException(
                $"原始文件信息上传包必须包含 {expectedCount} 个上传文件：" +
                $"{string.Join("、", GetExpectedOutputPaths(workflowProjectDirectory, includeRoleSceneScreenshot).Select(Path.GetFileName))}。");
        ValidatePdf(files[0], "AI 大纲 PDF");
        ValidatePdf(files[1], "剧本 PDF");
        ValidatePng(files[2], "项目资料截图", requireRoleVectorSize: false);
        ValidatePng(files[3], "角色矢量图", requireRoleVectorSize: true);
        if (includeRoleSceneScreenshot)
            ValidatePng(files[4], "角色场景素材截图", requireRoleVectorSize: false);
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
            .OrderByDescending(File.GetLastWriteTimeUtc)
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
