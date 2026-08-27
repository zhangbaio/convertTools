using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Imaging;
using SixLabors.ImageSharp;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services.ProjectImages.FableCut;

namespace TikTokPublisher.Core.Services;

public static class TikTokProjectImageService
{
    /// <summary>与版权材料“剪辑工程文件”对应，独立于 workflow 根目录。</summary>
    public const string OutputDirectoryName = "剪辑工程文件";
    public const int MinUploadImageCount = 4;
    public const string ImageTemplateMode = "image_template";
    public const string FableCutMode = "fablecut";
    public const string StateVersionToken = "v5-mode-aware-fablecut";

    private const string DocumentType = "tiktok_project_image_state";
    private const string LegacyInputStagingDirName = ".project_image_inputs";
    private const string FileNamePattern = "工程图_*.png";
    private const string StageDirectoryPrefix = ".staging-";
    private const string BackupDirectoryPrefix = ".backup-";
    private const string ObsoleteBackupDirectoryPrefix = ".obsolete-backup-";
    private const string FableCutRendererSchema = "fablecut-csharp-renderer-v1";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public static string GetOutputDirectory(string workflowProjectDirectory) =>
        Path.Combine(Path.GetFullPath(workflowProjectDirectory), OutputDirectoryName);

    public static async Task GenerateAsync(
        QueueProjectItem item,
        ClientSettings settings,
        bool forceRerun,
        Action<string>? log,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();

        var projectKey = Path.GetFullPath(item.ProjectDir);
        var projectLock = ProjectLocks.GetOrAdd(projectKey, static _ => new SemaphoreSlim(1, 1));
        await projectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await GenerateCoreAsync(item, NormalizeProjectImageSettings(settings.Clone()), forceRerun, log, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            projectLock.Release();
        }
    }

    private static async Task GenerateCoreAsync(
        QueueProjectItem item,
        ClientSettings settings,
        bool forceRerun,
        Action<string>? log,
        CancellationToken ct)
    {
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        Directory.CreateDirectory(context.WorkflowProjectDir);
        var episodeCount = Math.Max(1, item.EpisodeCount > 0
            ? item.EpisodeCount
            : ProjectWorkspaceService.ResolveSourceEpisodeCount(context.SourceProjectDir));
        var workflowDir = ProjectWorkspaceService.EnsureWorkflowInfo(context.SourceProjectDir, episodeCount, log);
        var mode = ResolveGenerationMode(settings);
        var count = ResolveCount(settings);
        var renderEpisodeLimit = ResolveRenderEpisodeLimit(settings);

        var templateDir = "";
        var fableCutRoot = "";
        var resourceFingerprint = "";
        if (mode == FableCutMode)
        {
            fableCutRoot = FableCutAssetResolver.Resolve(settings.TiktokProjectImageFableCutRoot);
            resourceFingerprint = FableCutAssetResolver.ComputeFingerprint(fableCutRoot);
        }
        else
        {
            templateDir = ResolveTemplateDirectory(settings, log);
            if (string.IsNullOrWhiteSpace(templateDir))
            {
                throw new DirectoryNotFoundException(
                    $"未找到工程图模板：{settings.TiktokProjectImageTemplateId}，请在系统设置里配置模板根目录。");
            }

            resourceFingerprint = ComputeDirectoryFingerprint(templateDir);
        }

        var sourceVideos = ProjectVideoResolver
            .ResolveMaterialVideos(context.SourceProjectDir, allowStagedFallback: true)
            .Take(renderEpisodeLimit)
            .ToArray();
        if (sourceVideos.Length == 0)
            throw new InvalidOperationException("生成工程图失败：未找到可用于截图的视频文件。");

        var episodeNames = ResolveEpisodeNames(sourceVideos);
        var signature = ComputeSignature(
            context,
            settings,
            mode,
            resourceFingerprint,
            sourceVideos,
            episodeNames,
            count,
            renderEpisodeLimit);
        var outputDir = GetOutputDirectory(workflowDir);
        Directory.CreateDirectory(outputDir);
        RecoverInterruptedOutput(outputDir, log);

        log?.Invoke(
            $"工程图输入：模式={DisplayMode(mode)}，视频={sourceVideos.Length}，" +
            $"渲染上限={renderEpisodeLimit}，目标图片={count}。");
        if (!forceRerun && HasEnoughOutputs(workflowDir, count) && IsSavedSignatureCurrent(context, signature))
        {
            log?.Invoke($"工程图已是当前配置：{CountProjectImages(workflowDir)}/{count} 张，跳过。");
            return;
        }

        if (TryDeleteLegacyInputDirectory(context.WorkflowProjectDir))
            log?.Invoke("工程图旧暂存清理：已删除旧版 .project_image_inputs 视频副本。");

        var stageDir = Path.Combine(outputDir, StageDirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stageDir);
        string? configPath = null;
        try
        {
            ProjectImageGenerateResult result;
            if (mode == FableCutMode)
            {
                var title = ResolveProjectTitle(item, context);
                result = await FableCutProjectImageBackend.GenerateAsync(
                    context.SourceProjectDir,
                    stageDir,
                    sourceVideos,
                    title,
                    count,
                    settings.TiktokProjectImageFableCutClipCount,
                    fableCutRoot,
                    settings,
                    log,
                    ct).ConfigureAwait(false);
            }
            else
            {
                configPath = ClientSettingsWorkflowConfigWriter.WriteTempConfig(settings);
                log?.Invoke(
                    $"开始生成工程图：模板={settings.TiktokProjectImageTemplateId}，" +
                    $"数量={count}，视频={sourceVideos.Length}。");
                result = await QueueInfrastructureServices.ProjectImages.GenerateAsync(
                    new ProjectImageGenerateRequest(
                        ProjectDir: workflowDir,
                        InputDir: context.SourceProjectDir,
                        OutputDir: stageDir,
                        TemplateImageDir: templateDir,
                        ConfigFile: configPath,
                        Count: count,
                        Overwrite: true,
                        EpisodeNames: episodeNames,
                        SourceVideos: sourceVideos,
                        Progress: log),
                    ct).ConfigureAwait(false);
            }

            var stagedOutputs = await ValidateStagedOutputsAsync(stageDir, result, count, ct)
                .ConfigureAwait(false);
            var committedOutputs = CommitStagedOutputs(outputDir, stagedOutputs);

            // 清理旧版散落在 workflow 根目录的工程图，避免与独立目录混淆。
            DeleteLegacyRootProjectImages(workflowDir);
            SaveState(
                context,
                signature,
                mode,
                mode == FableCutMode ? fableCutRoot : templateDir,
                resourceFingerprint,
                count,
                committedOutputs,
                settings);
            log?.Invoke($"工程图生成完成：{committedOutputs.Count} 张 → {outputDir}");
        }
        finally
        {
            TryDelete(configPath);
            TryDeleteDirectory(stageDir);
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
            return true;
        }
    }

    public static bool HasCurrentProjectImages(string sourceProjectDir, ClientSettings? settings = null)
    {
        try
        {
            var context = ProjectWorkspaceService.LoadContext(sourceProjectDir);
            var normalized = NormalizeProjectImageSettings((settings ?? ClientSettingsStore.Load()).Clone());
            var count = ResolveCount(normalized);
            if (!HasEnoughOutputs(context.WorkflowProjectDir, count))
                return false;

            var mode = ResolveGenerationMode(normalized);
            string resourceFingerprint;
            if (mode == FableCutMode)
            {
                var root = FableCutAssetResolver.Resolve(normalized.TiktokProjectImageFableCutRoot);
                resourceFingerprint = FableCutAssetResolver.ComputeFingerprint(root);
            }
            else
            {
                var templateDir = ResolveTemplateDirectory(normalized, log: null);
                if (string.IsNullOrWhiteSpace(templateDir))
                    return false;
                resourceFingerprint = ComputeDirectoryFingerprint(templateDir);
            }

            var renderEpisodeLimit = ResolveRenderEpisodeLimit(normalized);
            var sourceVideos = ProjectVideoResolver
                .ResolveMaterialVideos(context.SourceProjectDir, allowStagedFallback: true)
                .Take(renderEpisodeLimit)
                .ToArray();
            if (sourceVideos.Length == 0)
                return false;

            var episodeNames = ResolveEpisodeNames(sourceVideos);
            var signature = ComputeSignature(
                context,
                normalized,
                mode,
                resourceFingerprint,
                sourceVideos,
                episodeNames,
                count,
                renderEpisodeLimit);
            return IsSavedSignatureCurrent(context, signature);
        }
        catch
        {
            return false;
        }
    }

    public static int CountProjectImages(string workflowProjectDir)
    {
        if (string.IsNullOrWhiteSpace(workflowProjectDir) || !Directory.Exists(workflowProjectDir))
            return 0;
        return ListProjectImages(workflowProjectDir).Count;
    }

    public static bool HasCurrentOutput(string workflowProjectDir, int? requiredCount = null)
    {
        var required = requiredCount ?? MinUploadImageCount;
        return HasEnoughOutputs(workflowProjectDir, required);
    }

    public static IReadOnlyList<string> ListGeneratedImages(string workflowProjectDir) =>
        ListProjectImages(workflowProjectDir);

    public static void TryDeleteOutput(string workflowProjectDir)
    {
        DeleteProjectImages(workflowProjectDir);
        DeleteLegacyRootProjectImages(workflowProjectDir);
        var dir = GetOutputDirectory(workflowProjectDir);
        if (!Directory.Exists(dir))
            return;

        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir, recursive: false);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static IReadOnlyList<string> ListProjectImages(string workflowProjectDir)
    {
        if (string.IsNullOrWhiteSpace(workflowProjectDir))
            return Array.Empty<string>();

        var outputDir = GetOutputDirectory(workflowProjectDir);
        if (Directory.Exists(outputDir))
        {
            var dedicated = SortProjectImages(
                Directory.EnumerateFiles(outputDir, FileNamePattern, SearchOption.TopDirectoryOnly));
            if (dedicated.Count > 0)
                return dedicated;
        }

        // 兼容旧版散落在 workflow 根目录的工程图；生成成功后会清理。
        if (!Directory.Exists(workflowProjectDir))
            return Array.Empty<string>();
        return SortProjectImages(
            Directory.EnumerateFiles(workflowProjectDir, FileNamePattern, SearchOption.TopDirectoryOnly));
    }

    private static IReadOnlyList<string> SortProjectImages(IEnumerable<string> paths) =>
        paths
            .OrderBy(ProjectImageOrdinal)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int ProjectImageOrdinal(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var separator = stem.LastIndexOf('_');
        return separator >= 0 && int.TryParse(stem[(separator + 1)..], out var value)
            ? value
            : int.MaxValue;
    }

    private static bool HasEnoughOutputs(string workflowProjectDir, int count)
    {
        var outputs = ListProjectImages(workflowProjectDir);
        if (outputs.Count < count)
            return false;
        foreach (var path in outputs.Take(count))
        {
            try
            {
                if (new FileInfo(path).Length == 0)
                    return false;
                var image = Image.Identify(path);
                if (image is null || image.Width <= 0 || image.Height <= 0)
                    return false;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSavedSignatureCurrent(ProjectWorkspaceContext context, string signature)
    {
        var saved = LoadSavedSignature(context);
        return !string.IsNullOrWhiteSpace(saved) &&
               string.Equals(saved, signature, StringComparison.OrdinalIgnoreCase);
    }

    private static string LoadSavedSignature(ProjectWorkspaceContext context)
    {
        var state = ProjectStateDocumentStore.LoadDocument(
            context.WorkspaceRoot,
            context.SourceProjectDir,
            DocumentType);
        if (!state.TryGetValue("fingerprint", out var element) || element.ValueKind != JsonValueKind.String)
            return "";
        return element.GetString()?.Trim() ?? "";
    }

    private static void SaveState(
        ProjectWorkspaceContext context,
        string signature,
        string mode,
        string resourceDirectory,
        string resourceFingerprint,
        int count,
        IReadOnlyList<string> outputs,
        ClientSettings settings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["fingerprint"] = signature,
            ["signature_version"] = StateVersionToken,
            ["generation_mode"] = mode,
            ["template_id"] = mode == ImageTemplateMode ? settings.TiktokProjectImageTemplateId : "",
            ["resource_dir"] = resourceDirectory,
            ["resource_fingerprint"] = resourceFingerprint,
            ["fablecut_clip_count"] = mode == FableCutMode ? settings.TiktokProjectImageFableCutClipCount : null,
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

    private static string ResolveTemplateDirectory(ClientSettings settings, Action<string>? log)
    {
        var bundledRoot = Path.Combine(AppContext.BaseDirectory, "templates", "project-image");
        return ResolveTemplateDirectoryFromRoots(settings, bundledRoot, log);
    }

    internal static string ResolveTemplateDirectoryFromRoots(
        ClientSettings settings,
        string bundledRoot,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var selectedId = (settings.TiktokProjectImageTemplateId ?? string.Empty).Trim();
        if (selectedId.Length == 0)
            selectedId = ClientSettingsDefaults.TiktokProjectImageTemplateId;

        var explicitRoot = (settings.TiktokProjectImageTemplateRoot ?? string.Empty).Trim();
        var explicitDirectory = FindSelectedTemplateDirectory(explicitRoot, selectedId);
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
            return explicitDirectory;

        var bundledDirectory = FindSelectedTemplateDirectory(bundledRoot, selectedId);
        if (!string.IsNullOrWhiteSpace(bundledDirectory))
        {
            if (!string.IsNullOrWhiteSpace(explicitRoot))
            {
                log?.Invoke(
                    $"配置模板根目录未找到所选模板“{selectedId}”：{explicitRoot}；" +
                    $"已回落到内置同 ID 模板：{bundledDirectory}");
            }

            return bundledDirectory;
        }

        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            log?.Invoke(
                $"配置模板根目录与内置模板目录均未找到所选模板“{selectedId}”，不会回退到其他模板。");
        }

        return string.Empty;
    }

    private static string FindSelectedTemplateDirectory(string? root, string selectedId)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(selectedId))
            return string.Empty;

        string resolvedRoot;
        try
        {
            resolvedRoot = Path.GetFullPath(root.Trim());
            if (File.Exists(resolvedRoot) &&
                string.Equals(Path.GetFileName(resolvedRoot), "template.json", StringComparison.OrdinalIgnoreCase))
            {
                resolvedRoot = Path.GetDirectoryName(resolvedRoot) ?? resolvedRoot;
            }
        }
        catch
        {
            return string.Empty;
        }

        if (!Directory.Exists(resolvedRoot))
            return string.Empty;

        return ProjectImageTemplateCatalog.Discover(resolvedRoot)
                   .FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                   ?.TemplateDirectory
               ?? string.Empty;
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

    private static ClientSettings NormalizeProjectImageSettings(ClientSettings settings)
    {
        settings.TiktokProjectImageGenerationMode = ResolveGenerationMode(settings);
        settings.TiktokProjectImageTemplateRoot = (settings.TiktokProjectImageTemplateRoot ?? "").Trim();
        settings.TiktokProjectImageTemplateId = string.IsNullOrWhiteSpace(settings.TiktokProjectImageTemplateId)
            ? ClientSettingsDefaults.TiktokProjectImageTemplateId
            : settings.TiktokProjectImageTemplateId.Trim();
        settings.TiktokProjectImageCount = ResolveCount(settings);
        settings.TiktokProjectImageRenderEpisodeLimit = ResolveRenderEpisodeLimit(settings);
        settings.TiktokProjectImageFableCutRoot = (settings.TiktokProjectImageFableCutRoot ?? "").Trim();
        settings.TiktokProjectImageFableCutClipCount = Math.Clamp(
            settings.TiktokProjectImageFableCutClipCount <= 0
                ? ClientSettingsDefaults.TiktokProjectImageFableCutClipCount
                : settings.TiktokProjectImageFableCutClipCount,
            12,
            36);
        return settings;
    }

    private static string ResolveGenerationMode(ClientSettings settings) =>
        (settings.TiktokProjectImageGenerationMode ?? "").Trim().ToLowerInvariant() switch
        {
            "fablecut" or "fablecut_editor" => FableCutMode,
            _ => ImageTemplateMode,
        };

    private static string DisplayMode(string mode) =>
        mode == FableCutMode ? "FableCut真实工程" : "图片模板";

    private static string ResolveProjectTitle(QueueProjectItem item, ProjectWorkspaceContext context)
    {
        foreach (var candidate in new[] { item.Title, item.DisplayName, Path.GetFileName(context.SourceProjectDir) })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate.Trim();
        }

        return "短剧工程";
    }

    private static IReadOnlyList<string> ResolveEpisodeNames(IReadOnlyList<string> sourceVideos) =>
        sourceVideos
            .Select(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty)
            .ToArray();

    private static void DeleteProjectImages(string workflowDir)
    {
        foreach (var path in ListProjectImages(workflowDir))
            TryDelete(path);
    }

    private static void DeleteLegacyRootProjectImages(string workflowDir)
    {
        if (string.IsNullOrWhiteSpace(workflowDir) || !Directory.Exists(workflowDir))
            return;
        foreach (var path in Directory.EnumerateFiles(workflowDir, FileNamePattern, SearchOption.TopDirectoryOnly))
            TryDelete(path);
    }

    private static string ComputeSignature(
        ProjectWorkspaceContext context,
        ClientSettings settings,
        string mode,
        string resourceFingerprint,
        IReadOnlyList<string> sourceVideos,
        IReadOnlyList<string> episodeNames,
        int count,
        int renderEpisodeLimit)
    {
        var payload = new
        {
            version = StateVersionToken,
            generation_mode = mode,
            resource = resourceFingerprint,
            template_id = mode == ImageTemplateMode ? settings.TiktokProjectImageTemplateId : "",
            count,
            render_episode_limit = renderEpisodeLimit,
            subtitle_ai_mode = mode == ImageTemplateMode ? settings.TiktokProjectImageSubtitleAiMode : "",
            fablecut = mode == FableCutMode
                ? new
                {
                    renderer = FableCutRendererSchema,
                    clip_count = settings.TiktokProjectImageFableCutClipCount,
                    asr = FableCutTranscriptCache.ComputeSettingsFingerprint(settings),
                }
                : null,
            info = FileSignature(Path.Combine(context.WorkflowProjectDir, "短剧信息.txt")),
            videos = sourceVideos.Select(FileSignature).ToArray(),
            episode_names = episodeNames,
        };
        var json = JsonSerializer.Serialize(payload);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    internal static string ComputeDirectoryFingerprint(string directory)
    {
        var payload = DirectorySignature(directory);
        var json = JsonSerializer.Serialize(payload);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static object DirectorySignature(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Array.Empty<object>();
        var root = Path.GetFullPath(directory);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                Path = path,
                RelativePath = Path.GetRelativePath(root, path).Replace('\\', '/'),
            })
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(item => RelativeFileSignature(item.Path, item.RelativePath))
            .ToArray();
    }

    private static object RelativeFileSignature(string path, string relativePath)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return new object?[] { relativePath, 0, 0, string.Empty };

            using var stream = File.OpenRead(path);
            var contentHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return new object?[] { relativePath, info.Length, info.LastWriteTimeUtc.Ticks, contentHash };
        }
        catch
        {
            return new object?[] { relativePath, 0, 0, string.Empty };
        }
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

    private static async Task<IReadOnlyList<string>> ValidateStagedOutputsAsync(
        string stageDirectory,
        ProjectImageGenerateResult result,
        int count,
        CancellationToken ct)
    {
        if (result.Count < count || result.Outputs.Count < count)
            throw new InvalidOperationException($"生成工程图数量不足：{result.Count}/{count}");

        var stageRoot = Path.GetFullPath(stageDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var outputs = new List<string>(count);
        foreach (var path in result.Outputs.Take(count))
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(stageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
            {
                throw new InvalidOperationException($"工程图暂存输出无效：{path}");
            }

            var image = await Image.IdentifyAsync(fullPath, ct).ConfigureAwait(false);
            if (image is null || image.Width <= 0 || image.Height <= 0)
                throw new InvalidOperationException($"工程图不是有效图片：{Path.GetFileName(fullPath)}");
            outputs.Add(fullPath);
        }

        return outputs;
    }

    private static IReadOnlyList<string> CommitStagedOutputs(
        string outputDirectory,
        IReadOnlyList<string> stagedOutputs)
    {
        var backupDirectory = Path.Combine(outputDirectory, BackupDirectoryPrefix + Guid.NewGuid().ToString("N"));
        ResilientFileSystem.EnsureDirectory(backupDirectory);
        var committed = new List<string>(stagedOutputs.Count);
        var cleanupBackup = false;
        var cleanupDirectory = backupDirectory;
        try
        {
            foreach (var existing in Directory.EnumerateFiles(outputDirectory, FileNamePattern, SearchOption.TopDirectoryOnly))
                ResilientFileSystem.MoveFile(
                    existing,
                    Path.Combine(backupDirectory, Path.GetFileName(existing)),
                    overwrite: true);

            for (var index = 0; index < stagedOutputs.Count; index++)
            {
                var target = Path.Combine(outputDirectory, $"工程图_{index + 1}.png");
                ResilientFileSystem.MoveFile(stagedOutputs[index], target, overwrite: true);
                committed.Add(Path.GetFullPath(target));
            }

            // Rename is atomic on the same volume. Recovery only considers
            // .backup-* directories, so a cleanup failure can never make a
            // successfully committed old set look like an interrupted commit.
            cleanupDirectory = Path.Combine(
                outputDirectory,
                ObsoleteBackupDirectoryPrefix + Guid.NewGuid().ToString("N"));
            ResilientFileSystem.MoveDirectory(backupDirectory, cleanupDirectory);
            cleanupBackup = true;
            return committed;
        }
        catch (Exception commitError)
        {
            foreach (var target in committed)
                TryDelete(target);
            var restoreErrors = new List<Exception>();
            if (Directory.Exists(backupDirectory))
            {
                foreach (var backup in Directory.EnumerateFiles(backupDirectory, FileNamePattern, SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        // Copy first so a later restore failure still leaves a complete
                        // recoverable backup set on disk.
                        ResilientFileSystem.CopyFile(
                            backup,
                            Path.Combine(outputDirectory, Path.GetFileName(backup)),
                            overwrite: true);
                    }
                    catch (Exception restoreError)
                    {
                        restoreErrors.Add(restoreError);
                    }
                }
            }

            if (restoreErrors.Count > 0)
            {
                restoreErrors.Insert(0, commitError);
                throw new IOException(
                    $"工程图提交失败且旧图未能完整恢复；备份已保留在：{backupDirectory}",
                    new AggregateException(restoreErrors));
            }

            cleanupBackup = true;
            throw;
        }
        finally
        {
            if (cleanupBackup)
                TryDeleteDirectory(cleanupDirectory);
        }
    }

    internal static void RecoverInterruptedOutput(string outputDirectory, Action<string>? log)
    {
        var backupDirectories = Directory
            .EnumerateDirectories(outputDirectory, BackupDirectoryPrefix + "*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => new DirectoryInfo(path).LastWriteTimeUtc)
            .ToArray();
        if (backupDirectories.Length > 1)
        {
            throw new InvalidOperationException(
                "检测到多个未完成的工程图备份，无法安全判断恢复顺序，请保留目录并人工确认：" +
                string.Join("；", backupDirectories));
        }

        if (backupDirectories.Length == 1)
        {
            var backupDirectory = backupDirectories[0];
            var backupFiles = SortProjectImages(
                Directory.EnumerateFiles(backupDirectory, FileNamePattern, SearchOption.TopDirectoryOnly));
            if (backupFiles.Count > 0)
            {
                try
                {
                    foreach (var current in Directory.EnumerateFiles(
                                 outputDirectory,
                                 FileNamePattern,
                                 SearchOption.TopDirectoryOnly))
                    {
                        ResilientFileSystem.DeleteFile(current);
                    }

                    foreach (var backup in backupFiles)
                    {
                        ResilientFileSystem.CopyFile(
                            backup,
                            Path.Combine(outputDirectory, Path.GetFileName(backup)),
                            overwrite: true);
                    }
                }
                catch (Exception ex)
                {
                    throw new IOException(
                        $"恢复上次中断的工程图失败；完整备份仍保留在：{backupDirectory}",
                        ex);
                }

                log?.Invoke($"工程图恢复：已从中断备份恢复 {backupFiles.Count} 张旧图。");
            }

            TryDeleteDirectory(backupDirectory);
        }

        foreach (var stageDirectory in Directory.EnumerateDirectories(
                     outputDirectory,
                     StageDirectoryPrefix + "*",
                     SearchOption.TopDirectoryOnly))
        {
            TryDeleteDirectory(stageDirectory);
        }

        foreach (var obsoleteBackup in Directory.EnumerateDirectories(
                     outputDirectory,
                     ObsoleteBackupDirectoryPrefix + "*",
                     SearchOption.TopDirectoryOnly))
        {
            TryDeleteDirectory(obsoleteBackup);
        }
    }

    private static void TryDelete(string? path)
        => ResilientFileSystem.TryDeleteFile(path);

    private static void TryDeleteDirectory(string? path)
        => ResilientFileSystem.TryDeleteDirectory(path);

    private static bool TryDeleteLegacyInputDirectory(string workflowProjectDir)
    {
        try
        {
            var inputDir = Path.Combine(workflowProjectDir, LegacyInputStagingDirName);
            var workflowFull = Path.GetFullPath(workflowProjectDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var inputFull = Path.GetFullPath(inputDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
