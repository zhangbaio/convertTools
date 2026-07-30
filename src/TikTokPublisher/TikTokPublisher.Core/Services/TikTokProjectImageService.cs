using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Imaging;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class TikTokProjectImageService
{
    /// <summary>与版权材料「剪辑工程文件」对应，独立于 workflow 根目录。</summary>
    public const string OutputDirectoryName = "剪辑工程文件";
    public const int MinUploadImageCount = 4;

    private const string DocumentType = "tiktok_project_image_state";
    private const string LegacyInputStagingDirName = ".project_image_inputs";
    private const string SignatureVersion = "v4-dedicated-folder";
    private const string FileNamePattern = "工程图_*.png";

    public static string GetOutputDirectory(string workflowProjectDirectory) =>
        Path.Combine(Path.GetFullPath(workflowProjectDirectory), OutputDirectoryName);

    public static async Task GenerateAsync(
        QueueProjectItem item,
        ClientSettings settings,
        bool forceRerun,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        Directory.CreateDirectory(context.WorkflowProjectDir);
        var episodeCount = Math.Max(1, item.EpisodeCount > 0
            ? item.EpisodeCount
            : ProjectWorkspaceService.ResolveSourceEpisodeCount(context.SourceProjectDir));
        var workflowDir = ProjectWorkspaceService.EnsureWorkflowInfo(context.SourceProjectDir, episodeCount, log);

        var normalized = ClientSettingsStore.Load().Clone();
        ApplyProjectImageSettings(normalized, settings);
        var count = ResolveCount(normalized);
        var renderEpisodeLimit = ResolveRenderEpisodeLimit(normalized);
        var templateDir = ResolveTemplateDirectory(normalized);
        if (string.IsNullOrWhiteSpace(templateDir))
        {
            throw new DirectoryNotFoundException(
                $"未找到工程图模板：{normalized.TiktokProjectImageTemplateId}，请在系统设置里配置模板根目录。");
        }

        var sourceVideos = ProjectVideoResolver
            .ResolveSourceVideos(context.SourceProjectDir, allowStagedFallback: true)
            .Take(renderEpisodeLimit)
            .ToArray();
        if (sourceVideos.Length == 0)
        {
            throw new InvalidOperationException("生成工程图失败：未找到可用于截图的视频文件。");
        }
        log?.Invoke(
            $"工程图/输入扫描：可用视频={sourceVideos.Length}；" +
            $"渲染上限={renderEpisodeLimit}；目标图片={count}；模板目录={templateDir}。");

        var episodeNames = ResolveEpisodeNames(sourceVideos);
        var signature = ComputeSignature(context, normalized, templateDir, sourceVideos, episodeNames, count, renderEpisodeLimit);
        var outputDir = GetOutputDirectory(workflowDir);
        Directory.CreateDirectory(outputDir);
        if (!forceRerun && HasEnoughOutputs(workflowDir, count) && IsSavedSignatureCurrentOrMissing(context, signature))
        {
            SaveState(context, signature, templateDir, count, ListProjectImages(workflowDir));
            log?.Invoke($"工程图已存在 {CountProjectImages(workflowDir)}/{count} 张（{OutputDirectoryName}），跳过。");
            return;
        }

        if (forceRerun || HasSavedDifferentSignature(context, signature))
        {
            DeleteProjectImages(workflowDir);
        }

        if (TryDeleteLegacyInputDirectory(context.WorkflowProjectDir))
        {
            log?.Invoke("工程图/旧暂存清理：已删除旧版 .project_image_inputs 视频副本。");
        }
        log?.Invoke(
            $"工程图/直接输入：将直接读取 {sourceVideos.Length} 个原始视频，不再复制到临时目录。");
        var configPath = ClientSettingsWorkflowConfigWriter.WriteTempConfig(normalized);
        try
        {
            log?.Invoke(
                $"开始生成工程图：模板 {ClientSettingsDefaults.TiktokProjectImageTemplateName}，数量 {count}，取前 {sourceVideos.Length} 集 → {OutputDirectoryName}。");
            var result = await QueueInfrastructureServices.ProjectImages.GenerateAsync(
                new ProjectImageGenerateRequest(
                    ProjectDir: workflowDir,
                    InputDir: context.SourceProjectDir,
                    OutputDir: outputDir,
                    TemplateImageDir: templateDir,
                    ConfigFile: configPath,
                    Count: count,
                    Overwrite: true,
                    EpisodeNames: episodeNames,
                    SourceVideos: sourceVideos,
                    Progress: log),
                ct).ConfigureAwait(false);

            if (result.Count < count)
            {
                throw new InvalidOperationException($"生成工程图数量不足：{result.Count}/{count}");
            }

            // 清理旧版散落在 workflow 根目录的工程图，避免与独立目录混淆。
            DeleteLegacyRootProjectImages(workflowDir);

            SaveState(context, signature, templateDir, count, result.Outputs);
            log?.Invoke($"工程图生成完成：{result.Count} 张 → {outputDir}");
        }
        finally
        {
            TryDelete(configPath);
            log?.Invoke("工程图/清理：临时配置已清理。");
        }
    }

    public static bool NeedsGenerateProjectImages(QueueProjectItem item, ClientSettings? settings = null)
    {
        try
        {
            return !HasCurrentProjectImages(item.ProjectDir, settings);
        }
        catch
        {
            return false;
        }
    }

    public static bool HasCurrentProjectImages(string sourceProjectDir, ClientSettings? settings = null)
    {
        try
        {
            var context = ProjectWorkspaceService.LoadContext(sourceProjectDir);
            var normalized = ClientSettingsStore.Load().Clone();
            if (settings is not null)
            {
                ApplyProjectImageSettings(normalized, settings);
            }

            var count = ResolveCount(normalized);
            if (!HasEnoughOutputs(context.WorkflowProjectDir, count))
            {
                return false;
            }

            var templateDir = ResolveTemplateDirectory(normalized);
            if (string.IsNullOrWhiteSpace(templateDir))
            {
                return true;
            }

            var sourceVideos = ProjectVideoResolver
                .ResolveSourceVideos(context.SourceProjectDir, allowStagedFallback: true)
                .Take(ResolveRenderEpisodeLimit(normalized))
                .ToArray();
            if (sourceVideos.Length == 0)
            {
                return true;
            }

            var episodeNames = ResolveEpisodeNames(sourceVideos);
            var signature = ComputeSignature(
                context,
                normalized,
                templateDir,
                sourceVideos,
                episodeNames,
                count,
                ResolveRenderEpisodeLimit(normalized));
            return IsSavedSignatureCurrentOrMissing(context, signature);
        }
        catch
        {
            return false;
        }
    }

    public static int CountProjectImages(string workflowProjectDir)
    {
        if (string.IsNullOrWhiteSpace(workflowProjectDir) || !Directory.Exists(workflowProjectDir))
        {
            return 0;
        }

        return ListProjectImages(workflowProjectDir).Count;
    }

    public static bool HasCurrentOutput(string workflowProjectDir, int? requiredCount = null)
    {
        var required = requiredCount ?? MinUploadImageCount;
        return CountProjectImages(workflowProjectDir) >= required;
    }

    public static IReadOnlyList<string> ListGeneratedImages(string workflowProjectDir) =>
        ListProjectImages(workflowProjectDir);

    public static void TryDeleteOutput(string workflowProjectDir)
    {
        DeleteProjectImages(workflowProjectDir);
        DeleteLegacyRootProjectImages(workflowProjectDir);
        var dir = GetOutputDirectory(workflowProjectDir);
        if (!Directory.Exists(dir))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir, recursive: false);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static IReadOnlyList<string> ListProjectImages(string workflowProjectDir)
    {
        if (string.IsNullOrWhiteSpace(workflowProjectDir))
        {
            return Array.Empty<string>();
        }

        var outputDir = GetOutputDirectory(workflowProjectDir);
        if (Directory.Exists(outputDir))
        {
            var dedicated = Directory.EnumerateFiles(outputDir, FileNamePattern, SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (dedicated.Length > 0)
            {
                return dedicated;
            }
        }

        // 兼容旧版散落在 workflow 根目录的工程图（读取用；生成后会迁走/清理）。
        if (!Directory.Exists(workflowProjectDir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(workflowProjectDir, FileNamePattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool HasEnoughOutputs(string workflowProjectDir, int count) =>
        CountProjectImages(workflowProjectDir) >= count;

    private static bool IsSavedSignatureCurrentOrMissing(ProjectWorkspaceContext context, string signature)
    {
        var saved = LoadSavedSignature(context);
        return string.IsNullOrWhiteSpace(saved) ||
               string.Equals(saved, signature, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSavedDifferentSignature(ProjectWorkspaceContext context, string signature)
    {
        var saved = LoadSavedSignature(context);
        return !string.IsNullOrWhiteSpace(saved) &&
               !string.Equals(saved, signature, StringComparison.OrdinalIgnoreCase);
    }

    private static string LoadSavedSignature(ProjectWorkspaceContext context)
    {
        var state = ProjectStateDocumentStore.LoadDocument(
            context.WorkspaceRoot,
            context.SourceProjectDir,
            DocumentType);
        if (!state.TryGetValue("fingerprint", out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        return element.GetString()?.Trim() ?? "";
    }

    private static void SaveState(
        ProjectWorkspaceContext context,
        string signature,
        string templateDir,
        int count,
        IReadOnlyList<string> outputs)
    {
        var payload = new Dictionary<string, object?>
        {
            ["fingerprint"] = signature,
            ["template_id"] = ClientSettingsDefaults.TiktokProjectImageTemplateId,
            ["template_dir"] = templateDir,
            ["count"] = count,
            ["outputs"] = outputs.Select(Path.GetFullPath).ToArray(),
            ["generated_at"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        };
        ProjectStateDocumentStore.SaveDocument(
            context.WorkspaceRoot,
            context.SourceProjectDir,
            DocumentType,
            payload,
            context.WorkflowProjectDir);
    }

    private static string ResolveTemplateDirectory(ClientSettings settings)
    {
        var resolved = ProjectImageTemplateCatalog.ResolveTemplateDirectory(
            settings.TiktokProjectImageTemplateRoot,
            settings.TiktokProjectImageTemplateId,
            fallbackDirectory: "",
            projectRoot: null);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        var bundledRoot = Path.Combine(AppContext.BaseDirectory, "templates", "project-image");
        return ProjectImageTemplateCatalog.ResolveTemplateDirectory(
            bundledRoot,
            ClientSettingsDefaults.TiktokProjectImageTemplateId,
            fallbackDirectory: "",
            projectRoot: null);
    }

    private static int ResolveCount(ClientSettings settings) =>
        Math.Clamp(
            settings.TiktokProjectImageCount <= 0
                ? ClientSettingsDefaults.TiktokProjectImageCount
                : settings.TiktokProjectImageCount,
            1,
            20);

    private static int ResolveRenderEpisodeLimit(ClientSettings settings) =>
        Math.Clamp(
            settings.TiktokProjectImageRenderEpisodeLimit <= 0
                ? ClientSettingsDefaults.TiktokProjectImageRenderEpisodeLimit
                : settings.TiktokProjectImageRenderEpisodeLimit,
            1,
            200);

    private static void ApplyProjectImageSettings(ClientSettings target, ClientSettings source)
    {
        target.TiktokProjectImageGenerationMode = source.TiktokProjectImageGenerationMode;
        target.TiktokProjectImageTemplateRoot = source.TiktokProjectImageTemplateRoot;
        target.TiktokProjectImageTemplateId = source.TiktokProjectImageTemplateId;
        target.TiktokProjectImageCount = source.TiktokProjectImageCount;
        target.TiktokProjectImageRenderEpisodeLimit = source.TiktokProjectImageRenderEpisodeLimit;
        target.TiktokProjectImageSubtitleAiMode = source.TiktokProjectImageSubtitleAiMode;
    }

    private static IReadOnlyList<string> ResolveEpisodeNames(IReadOnlyList<string> sourceVideos)
    {
        // Python uses ep_path.stem, so keep the exact stem of the selected input video.
        return sourceVideos
            .Select(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty)
            .ToArray();
    }

    private static void DeleteProjectImages(string workflowDir)
    {
        foreach (var path in ListProjectImages(workflowDir))
        {
            TryDelete(path);
        }
    }

    private static void DeleteLegacyRootProjectImages(string workflowDir)
    {
        if (string.IsNullOrWhiteSpace(workflowDir) || !Directory.Exists(workflowDir))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(workflowDir, FileNamePattern, SearchOption.TopDirectoryOnly))
        {
            TryDelete(path);
        }
    }

    private static string ComputeSignature(
        ProjectWorkspaceContext context,
        ClientSettings settings,
        string templateDir,
        IReadOnlyList<string> sourceVideos,
        IReadOnlyList<string> episodeNames,
        int count,
        int renderEpisodeLimit)
    {
        var payload = new
        {
            version = SignatureVersion,
            template_id = settings.TiktokProjectImageTemplateId,
            template = DirectorySignature(templateDir),
            count,
            render_episode_limit = renderEpisodeLimit,
            subtitle_ai_mode = settings.TiktokProjectImageSubtitleAiMode,
            info = FileSignature(Path.Combine(context.WorkflowProjectDir, "短剧信息.txt")),
            videos = sourceVideos.Select(FileSignature).ToArray(),
            episode_names = episodeNames,
        };
        var json = JsonSerializer.Serialize(payload);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static object DirectorySignature(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Array.Empty<object>();
        }

        return Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetFileName(path), "template.json", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(FileSignature)
            .ToArray();
    }

    private static object FileSignature(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new object?[] { info.Name, info.Length, info.LastWriteTimeUtc.Ticks }
                : new object?[] { Path.GetFileName(path), 0, 0 };
        }
        catch
        {
            return new object?[] { Path.GetFileName(path), 0, 0 };
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static bool TryDeleteLegacyInputDirectory(string workflowProjectDir)
    {
        try
        {
            var inputDir = Path.Combine(workflowProjectDir, LegacyInputStagingDirName);
            var workflowFull = Path.GetFullPath(workflowProjectDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var inputFull = Path.GetFullPath(inputDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!inputFull.StartsWith(workflowFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(inputFull), LegacyInputStagingDirName, StringComparison.Ordinal))
            {
                return false;
            }

            if (Directory.Exists(inputFull))
            {
                Directory.Delete(inputFull, recursive: true);
                return true;
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }

        return false;
    }
}
