using SixLabors.ImageSharp;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 为 TikTok「原始文件或素材文件信息」整理固定的四文件上传包：
/// AI 大纲、剧本、项目资料截图和角色矢量图。
/// </summary>
public static class TikTokSourceFileInfoUploadPackageService
{
    public const string OutputDirectoryName = "原始文件信息上传";
    public const int RequiredFileCount = 4;
    public const string OutlineFileName = "AI剧本大纲.pdf";
    public const string ScriptFileName = "剧本.pdf";
    public const string ProjectInfoImageFileName = "01_剧本与项目资料.png";
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

    public static IReadOnlyList<string> GetExpectedOutputPaths(string workflowProjectDirectory)
    {
        var outputDirectory = GetOutputDirectory(workflowProjectDirectory);
        return FileNames.Select(name => Path.Combine(outputDirectory, name)).ToArray();
    }

    public static IReadOnlyList<string> ListFiles(string workflowProjectDirectory) =>
        GetExpectedOutputPaths(workflowProjectDirectory).Where(File.Exists).ToArray();

    public static bool HasCurrentOutput(string workflowProjectDirectory)
    {
        try
        {
            Validate(workflowProjectDirectory);
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
        Action<string>? log = null)
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
        var projectInfo = ResolveRequiredFile(
            null,
            Path.Combine(
                TikTokSourceFileInfoScreenshotService.GetOutputDirectory(workflow),
                ProjectInfoImageFileName),
            "项目资料截图",
            "请先执行“生成证明材料”步骤。");
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
        ValidatePng(roleVector, "角色矢量图", requireRoleVectorSize: true);

        var outputDirectory = GetOutputDirectory(workflow);
        TryDeleteDirectory(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var outputs = GetExpectedOutputPaths(workflow);
        Copy(outline, outputs[0]);
        Copy(script, outputs[1]);
        Copy(projectInfo, outputs[2]);
        Copy(roleVector, outputs[3]);
        Validate(workflow);
        log?.Invoke(
            $"原始文件信息上传包已生成：AI 大纲、剧本、项目资料截图、角色矢量图 → {outputDirectory}");
        return outputs;
    }

    public static void Validate(string workflowProjectDirectory)
    {
        var files = GetExpectedOutputPaths(workflowProjectDirectory);
        var outputDirectory = GetOutputDirectory(workflowProjectDirectory);
        var actualFiles = Directory.Exists(outputDirectory)
            ? Directory.EnumerateFiles(outputDirectory).ToArray()
            : [];
        if (files.Count != RequiredFileCount || files.Any(path => !File.Exists(path)) ||
            actualFiles.Length != RequiredFileCount)
            throw new FileNotFoundException(
                $"原始文件信息上传包必须包含 {RequiredFileCount} 个固定文件：{string.Join("、", FileNames)}。");
        ValidatePdf(files[0], "AI 大纲 PDF");
        ValidatePdf(files[1], "剧本 PDF");
        ValidatePng(files[2], "项目资料截图", requireRoleVectorSize: false);
        ValidatePng(files[3], "角色矢量图", requireRoleVectorSize: true);
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
            .OrderByDescending(path => Path.GetFileName(path).EndsWith(
                TikTokEpisodeScriptService.OutputSuffix,
                StringComparison.OrdinalIgnoreCase))
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

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex)
        {
            throw new IOException($"无法清理旧的原始文件信息上传目录：{path}", ex);
        }
    }
}
