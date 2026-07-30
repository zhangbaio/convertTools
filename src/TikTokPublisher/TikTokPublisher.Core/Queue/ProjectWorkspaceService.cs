using System.Text.Json;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

public sealed record ProjectWorkspaceContext(
    string SourceProjectDir,
    string WorkflowProjectDir,
    string WorkspaceRoot);

/// <summary>对齐 Python <c>project_workspace.py</c> 的源/workflow 目录解析与准备。</summary>
public static class ProjectWorkspaceService
{
    private const string MetadataFile = "shortdrama-project.json";
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm",
    };

    private static readonly HashSet<string> IgnoredVideoDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "workflow", "archive", TikTokUploadStagingService.StagingDirName,
    };

    public static ProjectWorkspaceContext LoadContext(string projectDir)
    {
        var resolved = Path.GetFullPath(projectDir);
        if (IsWorkflowProjectDir(resolved))
        {
            var workspaceRoot = Directory.GetParent(resolved)?.Parent?.FullName ?? resolved;
            var source = ResolveSourceFromWorkflowMetadata(resolved, workspaceRoot) ?? resolved;
            return new ProjectWorkspaceContext(source, resolved, workspaceRoot);
        }

        var metadata = ReadMetadata(resolved);
        var workflowDir = ResolveWorkflowProjectDir(resolved, metadata);
        var workspaceRoot2 = ResolveWorkspaceRoot(resolved);
        return new ProjectWorkspaceContext(resolved, workflowDir, workspaceRoot2);
    }

    /// <summary>
    /// 校验 source/workflow 的双向归属，避免陈旧或复制来的元数据把当前项目指向另一项目目录。
    /// </summary>
    public static void ValidateContextOwnership(ProjectWorkspaceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var source = Path.GetFullPath(context.SourceProjectDir);
        var workflow = Path.GetFullPath(context.WorkflowProjectDir);
        var workspace = Path.GetFullPath(context.WorkspaceRoot);
        var workflowRoot = Path.Combine(workspace, "workflow");
        if (!IsDirectChildOf(workflow, workflowRoot))
        {
            throw new InvalidDataException(
                $"当前项目 workflow 目录越界：{workflow}；预期位于：{workflowRoot}。");
        }

        // 直接以 workflow 项目作为入口时，它本身就是本次操作的边界，不存在 source 跨项目映射。
        if (PathsEqual(source, workflow))
            return;

        if (!IsDirectChildOf(source, workspace))
        {
            throw new InvalidDataException(
                $"当前项目源目录不属于工作区：{source}；工作区：{workspace}。");
        }

        var sourceMetadata = ReadMetadata(source);
        if (TryRepairMovedProjectMetadata(source, workflow, sourceMetadata))
            sourceMetadata = ReadMetadata(source);

        var sourceDeclaredBySource = sourceMetadata.GetValueOrDefault("sourceProjectDir");
        if (!string.IsNullOrWhiteSpace(sourceDeclaredBySource) &&
            !PathsEqual(ResolveMetadataPath(sourceDeclaredBySource, source), source))
        {
            throw new InvalidDataException(
                $"当前项目元数据中的 sourceProjectDir 与实际目录不一致：{sourceDeclaredBySource}；实际：{source}。");
        }

        if (!Directory.Exists(workflow))
            return;

        var workflowMetadata = ReadMetadata(workflow);
        var sourceDeclaredByWorkflow = workflowMetadata.GetValueOrDefault("sourceProjectDir");
        if (!string.IsNullOrWhiteSpace(sourceDeclaredByWorkflow))
        {
            var declaredSource = ResolveMetadataPath(sourceDeclaredByWorkflow, workflow);
            if (!PathsEqual(declaredSource, source))
            {
                throw new InvalidDataException(
                    $"workflow 目录属于另一项目：{workflow}；其 sourceProjectDir 为 {declaredSource}，" +
                    $"当前项目为 {source}。");
            }
            return;
        }

        var defaultWorkflow = Path.Combine(workflowRoot, Path.GetFileName(source));
        if (!PathsEqual(workflow, defaultWorkflow))
        {
            throw new InvalidDataException(
                $"自定义 workflow 目录缺少项目归属元数据：{workflow}。请重新同步当前项目后再上传证明材料。");
        }
    }

    private static bool TryRepairMovedProjectMetadata(
        string sourceProjectDir,
        string workflowProjectDir,
        IReadOnlyDictionary<string, string> sourceMetadata)
    {
        var declaredSourceValue = sourceMetadata.GetValueOrDefault("sourceProjectDir");
        if (string.IsNullOrWhiteSpace(declaredSourceValue))
            return false;

        var declaredSource = ResolveMetadataPath(declaredSourceValue, sourceProjectDir);
        if (PathsEqual(declaredSource, sourceProjectDir))
            return false;

        // Only heal a parent-folder move. A changed project directory name may indicate
        // copied metadata from another drama and must continue to fail ownership checks.
        if (Directory.Exists(declaredSource) ||
            !SameDirectoryName(declaredSource, sourceProjectDir))
        {
            return false;
        }

        var declaredWorkflowValue = sourceMetadata.GetValueOrDefault("workflowProjectDir");
        if (!string.IsNullOrWhiteSpace(declaredWorkflowValue))
        {
            var declaredWorkflow = ResolveMetadataPath(declaredWorkflowValue, sourceProjectDir);
            if (!PathsEqual(declaredWorkflow, workflowProjectDir) &&
                (Directory.Exists(declaredWorkflow) ||
                 !SameDirectoryName(declaredWorkflow, workflowProjectDir)))
            {
                return false;
            }
        }

        if (Directory.Exists(workflowProjectDir))
        {
            var workflowMetadata = ReadMetadata(workflowProjectDir);
            var workflowDeclaredSourceValue = workflowMetadata.GetValueOrDefault("sourceProjectDir");
            if (!string.IsNullOrWhiteSpace(workflowDeclaredSourceValue))
            {
                var workflowDeclaredSource = ResolveMetadataPath(
                    workflowDeclaredSourceValue,
                    workflowProjectDir);
                if (!PathsEqual(workflowDeclaredSource, sourceProjectDir) &&
                    !PathsEqual(workflowDeclaredSource, declaredSource))
                {
                    return false;
                }
            }

            UpdateWorkflowMetadata(
                sourceProjectDir,
                workflowProjectDir,
                overwriteSourceProjectDir: true);
            return true;
        }

        // Do not create a missing workflow directory merely to repair source metadata.
        UpdateMetadataFile(
            Path.Combine(sourceProjectDir, MetadataFile),
            workflowProjectDir,
            sourceProjectDir,
            overwriteSourceProjectDir: true);
        return true;
    }

    private static bool SameDirectoryName(string left, string right) =>
        string.Equals(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(left)),
            Path.GetFileName(Path.TrimEndingDirectorySeparator(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static string ResolveWorkflowProjectDir(string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir)) return "";
        var context = LoadContext(projectDir);
        return context.WorkflowProjectDir;
    }

    public static string EnsureWorkflowProjectDir(string sourceProjectDir)
    {
        var context = LoadContext(sourceProjectDir);
        Directory.CreateDirectory(context.WorkflowProjectDir);
        return context.WorkflowProjectDir;
    }

    public static string PrepareWorkflowProject(string projectDir, Action<string>? log = null)
    {
        var context = LoadContext(projectDir);
        var workflowDir = EnsureWorkflowProjectDir(context.SourceProjectDir);

        foreach (var entry in Directory.EnumerateFiles(context.SourceProjectDir))
        {
            var name = Path.GetFileName(entry);
            if (name.StartsWith(".", StringComparison.Ordinal)) continue;
            if (!ShouldSyncSourceFile(entry)) continue;

            var target = Path.Combine(workflowDir, name);
            if (File.Exists(target)) continue;

            LinkOrCopy(entry, target);
            log?.Invoke($"同步到 workflow: {name}");
        }

        UpdateWorkflowMetadata(context.SourceProjectDir, workflowDir);
        return workflowDir;
    }

    public static string SyncWorkflowProjectDirName(string projectDir, string displayTitle, Action<string>? log = null)
    {
        var context = LoadContext(projectDir);
        var sourceProjectDir = Path.GetFullPath(context.SourceProjectDir);
        var currentWorkflowDir = Path.GetFullPath(context.WorkflowProjectDir);
        var desiredName = "_" + SanitizeWorkflowName(FirstNonEmpty(displayTitle, Path.GetFileName(sourceProjectDir)));
        var desiredWorkflowDir = Path.Combine(ResolveWorkflowRoot(sourceProjectDir), desiredName);

        if (!string.Equals(currentWorkflowDir, desiredWorkflowDir, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(desiredWorkflowDir)!);
            if (Directory.Exists(desiredWorkflowDir))
                throw new InvalidOperationException($"目标 workflow 目录已存在: {desiredWorkflowDir}");

            if (Directory.Exists(currentWorkflowDir))
                Directory.Move(currentWorkflowDir, desiredWorkflowDir);
            else
                Directory.CreateDirectory(desiredWorkflowDir);

            log?.Invoke($"workflow 目录已切换为：{desiredWorkflowDir}");
        }
        else
        {
            Directory.CreateDirectory(desiredWorkflowDir);
        }

        UpdateWorkflowMetadata(sourceProjectDir, desiredWorkflowDir);
        return desiredWorkflowDir;
    }

    public static string EnsureWorkflowInfo(
        string projectDir,
        int episodeCount,
        Action<string>? log = null)
    {
        var context = LoadContext(projectDir);
        var workflowDir = PrepareWorkflowProject(context.SourceProjectDir, log);
        var infoPath = Path.Combine(workflowDir, "短剧信息.txt");
        var metadata = ReadMetadata(context.SourceProjectDir);
        var synopsis = FirstNonEmpty(
            metadata.GetValueOrDefault("intro"),
            metadata.GetValueOrDefault("description"));
        var sourceInfo = ProjectInfoTextHelper.ParseInfoFile(
            Path.Combine(context.SourceProjectDir, "短剧信息.txt"));
        var companyName = FirstNonEmpty(
            sourceInfo.GetValueOrDefault("制作公司"),
            metadata.GetValueOrDefault("companyName"),
            metadata.GetValueOrDefault("productionCompany"),
            "未填写公司");

        if (!File.Exists(infoPath))
        {
            var originalTitle = FirstNonEmpty(
                metadata.GetValueOrDefault("title"),
                metadata.GetValueOrDefault("originalTitle"),
                metadata.GetValueOrDefault("displayName"),
                Path.GetFileName(context.SourceProjectDir));
            var newTitle = FirstNonEmpty(
                metadata.GetValueOrDefault("newTitle"),
                originalTitle);
            WriteMinimalProjectInfo(infoPath, newTitle, originalTitle, Math.Max(1, episodeCount), synopsis);
            log?.Invoke("已生成短剧信息.txt");
        }
        else
        {
            UpdateProjectInfoField(infoPath, "集数", Math.Max(1, episodeCount).ToString());
            UpdateProjectInfoFieldIfBlank(infoPath, "简介", synopsis);
            UpdateProjectInfoFieldIfBlank(infoPath, "制作公司", companyName);
        }

        return workflowDir;
    }

    public static int ResolveSourceEpisodeCount(string projectDir)
    {
        var context = LoadContext(projectDir);
        var candidates = new List<int>();

        foreach (var infoPath in new[]
                 {
                     Path.Combine(context.SourceProjectDir, "短剧信息.txt"),
                     Path.Combine(context.WorkflowProjectDir, "短剧信息.txt"),
                 })
        {
            if (!File.Exists(infoPath)) continue;
            foreach (var value in ParseEpisodeCountFromInfo(infoPath))
                candidates.Add(value);
        }

        var metadata = ReadMetadata(context.SourceProjectDir);
        if (TryReadPositiveInt(metadata, out var effectiveCount, "effectiveEpisodeCount", "effective_episode_count", "downloadEpisodeLimit", "download_episode_limit"))
            return effectiveCount;

        if (metadata.TryGetValue("episodeCount", out var episodeRaw) &&
            int.TryParse(new string(episodeRaw.Where(char.IsDigit).ToArray()), out var metaCount) &&
            metaCount > 0)
        {
            candidates.Add(metaCount);
        }

        var videoCount = CountVideoFiles(context.SourceProjectDir, context.WorkflowProjectDir);
        if (videoCount > 0) candidates.Add(videoCount);
        return candidates.Count > 0 ? candidates.Max() : 1;
    }

    private static bool TryReadPositiveInt(
        Dictionary<string, string> metadata,
        out int value,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!metadata.TryGetValue(key, out var raw))
                continue;

            var digits = new string((raw ?? "").Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out value) && value > 0)
                return true;
        }

        value = 0;
        return false;
    }

    public static void RefreshQueueItemMetadata(QueueProjectItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ProjectDir)) return;
        var project = WorkspaceProjectScanner.BuildProject(item.ProjectDir);
        item.DisplayName = FirstNonEmpty(project.DisplayName, item.DisplayName, Path.GetFileName(item.ProjectDir));
        item.OriginalTitle = FirstNonEmpty(project.OriginalTitle, item.OriginalTitle);
        item.NewTitle = FirstNonEmpty(project.NewTitle, item.NewTitle);
        item.Description = FirstNonEmpty(project.Description, item.Description);
        item.GenreCategory = FirstNonEmpty(project.GenreCategory, item.GenreCategory);
        if (project.EpisodeCount > 0) item.EpisodeCount = project.EpisodeCount;
        item.PrimaryVideoPath = project.PrimaryVideoPath;
        item.CoverPath = project.CoverPath;
    }

    public static void UpdateMovedWorkspaceMetadata(string sourceProjectDir, string workflowProjectDir)
    {
        UpdateWorkflowMetadata(sourceProjectDir, workflowProjectDir, overwriteSourceProjectDir: true);
    }

    public static string? FindPosterInputFile(string sourceProjectDir, string workflowProjectDir)
    {
        foreach (var root in new[] { sourceProjectDir, workflowProjectDir })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var ext in ImageExtensions)
            {
                var preferred = Path.Combine(root, $"海报图片{ext}");
                if (File.Exists(preferred)) return preferred;
            }

            var candidate = Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
                .Where(path =>
                {
                    var fileName = Path.GetFileNameWithoutExtension(path);
                    return !fileName.StartsWith("工程图_", StringComparison.Ordinal) &&
                           !fileName.StartsWith("成本报表", StringComparison.Ordinal) &&
                           !fileName.StartsWith("seal.prepared", StringComparison.Ordinal);
                })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (candidate is not null) return candidate;
        }

        return null;
    }

    private static bool IsWorkflowProjectDir(string projectDir)
    {
        var parent = Directory.GetParent(projectDir);
        return parent is not null &&
               string.Equals(parent.Name, "workflow", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveWorkspaceRoot(string sourceProjectDir)
    {
        if (IsWorkflowProjectDir(sourceProjectDir))
        {
            return Directory.GetParent(sourceProjectDir)?.Parent?.FullName ?? sourceProjectDir;
        }

        return Directory.GetParent(sourceProjectDir)?.FullName ?? sourceProjectDir;
    }

    private static string ResolveWorkflowProjectDir(string sourceProjectDir, Dictionary<string, string> metadata)
    {
        if (IsWorkflowProjectDir(sourceProjectDir))
            return sourceProjectDir;

        var configuredPath = metadata.GetValueOrDefault("workflowProjectDir");
        if (!string.IsNullOrWhiteSpace(configuredPath) && Directory.Exists(configuredPath))
            return Path.GetFullPath(configuredPath);

        var configuredName = metadata.GetValueOrDefault("workflowDirName");
        if (!string.IsNullOrWhiteSpace(configuredName))
        {
            return Path.Combine(ResolveWorkflowRoot(sourceProjectDir), configuredName);
        }

        return Path.Combine(ResolveWorkflowRoot(sourceProjectDir), Path.GetFileName(sourceProjectDir));
    }

    private static string ResolveWorkflowRoot(string sourceProjectDir) =>
        Path.Combine(ResolveWorkspaceRoot(sourceProjectDir), "workflow");

    private static bool IsDirectChildOf(string path, string parent) =>
        PathsEqual(Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty, Path.GetFullPath(parent));

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string ResolveMetadataPath(string value, string metadataDirectory) =>
        Path.IsPathFullyQualified(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(value, metadataDirectory);

    private static string? ResolveSourceFromWorkflowMetadata(string workflowProjectDir, string workspaceRoot)
    {
        var metadata = ReadMetadata(workflowProjectDir);
        var configuredSource = metadata.GetValueOrDefault("sourceProjectDir");
        if (!string.IsNullOrWhiteSpace(configuredSource) && Directory.Exists(configuredSource))
            return Path.GetFullPath(configuredSource);

        foreach (var key in new[] { "projectKey", "sourceName", "title", "displayName" })
        {
            var name = metadata.GetValueOrDefault(key);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var candidate = Path.Combine(workspaceRoot, name);
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        var byName = Path.Combine(workspaceRoot, Path.GetFileName(workflowProjectDir));
        if (Directory.Exists(byName))
            return Path.GetFullPath(byName);

        var stripped = Path.GetFileName(workflowProjectDir).TrimStart('_');
        if (!string.IsNullOrWhiteSpace(stripped))
        {
            var candidate = Path.Combine(workspaceRoot, stripped);
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static void UpdateWorkflowMetadata(
        string sourceProjectDir,
        string workflowProjectDir,
        bool overwriteSourceProjectDir = false)
    {
        UpdateMetadataFile(
            Path.Combine(sourceProjectDir, MetadataFile),
            workflowProjectDir,
            sourceProjectDir,
            overwriteSourceProjectDir);
        UpdateMetadataFile(
            Path.Combine(workflowProjectDir, MetadataFile),
            workflowProjectDir,
            sourceProjectDir,
            overwriteSourceProjectDir);
    }

    private static void UpdateMetadataFile(
        string metadataPath,
        string workflowProjectDir,
        string sourceProjectDir,
        bool overwriteSourceProjectDir)
    {
        Dictionary<string, object?> payload;
        if (File.Exists(metadataPath))
        {
            try
            {
                payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(metadataPath))
                          ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            }
            catch
            {
                payload = new Dictionary<string, object?>(StringComparer.Ordinal);
            }
        }
        else
        {
            payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        payload["workflowDirName"] = Path.GetFileName(workflowProjectDir);
        payload["workflowProjectDir"] = workflowProjectDir;
        if (overwriteSourceProjectDir ||
            !payload.ContainsKey("sourceProjectDir") ||
            string.IsNullOrWhiteSpace(payload["sourceProjectDir"]?.ToString()))
        {
            payload["sourceProjectDir"] = sourceProjectDir;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string SanitizeWorkflowName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string((name ?? "").Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray())
            .Trim()
            .Trim('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "workflow-project" : sanitized;
    }

    private static bool ShouldSyncSourceFile(string path)
    {
        var name = Path.GetFileName(path);
        var lowerName = name.ToLowerInvariant();
        if (string.Equals(name, MetadataFile, StringComparison.Ordinal) ||
            string.Equals(name, ".weixin-channel-download-state.json", StringComparison.Ordinal))
        {
            return true;
        }

        if (name is "短剧信息.txt" or "成本报表.png" or "weixin-channel-autogen.json" or
            "weixin-channel-material.json" or ".weixin-channel-material-state.json" or
            ".weixin-channel-publish-state.json")
        {
            return false;
        }

        if (name.StartsWith("工程图集", StringComparison.Ordinal) || lowerName.StartsWith("tmp_pil_"))
            return false;

        return ImageExtensions.Contains(Path.GetExtension(path));
    }

    private static Dictionary<string, string> ReadMetadata(string projectDir)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(projectDir, MetadataFile);
        if (!File.Exists(path)) return result;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? "",
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => property.Value.GetRawText(),
                };
            }
        }
        catch
        {
            // ignore invalid metadata
        }

        return result;
    }

    private static IEnumerable<int> ParseEpisodeCountFromInfo(string infoPath)
    {
        foreach (var line in File.ReadAllLines(infoPath))
        {
            var trimmed = line.Trim();
            var sepIndex = ProjectInfoTextHelper.FindFieldSeparatorIndex(trimmed);
            if (sepIndex <= 0) continue;
            var key = trimmed[..sepIndex].Trim();
            var valuePart = trimmed[(sepIndex + 1)..].Trim();
            if (key is not ("集数" or "总集数" or "剧集数")) continue;
            var digits = new string(valuePart.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var count) && count > 0)
                yield return count;
        }
    }

    private static int CountVideoFiles(params string[] roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var sub in new[] { root, Path.Combine(root, "videos") })
            {
                if (!Directory.Exists(sub)) continue;
                foreach (var path in EnumerateVideoFiles(sub))
                    seen.Add(Path.GetFullPath(path));
            }
        }

        return seen.Count;
    }

    private static IEnumerable<string> EnumerateVideoFiles(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly))
        {
            if (VideoExtensions.Contains(Path.GetExtension(path)))
                yield return path;
        }

        foreach (var child in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(child);
            if (string.IsNullOrWhiteSpace(name) ||
                name.StartsWith(".", StringComparison.Ordinal) ||
                IgnoredVideoDirectoryNames.Contains(name))
            {
                continue;
            }

            foreach (var path in EnumerateVideoFiles(child))
                yield return path;
        }
    }

    private static void WriteMinimalProjectInfo(
        string infoPath,
        string newTitle,
        string originalTitle,
        int episodeCount,
        string synopsis)
    {
        var lines = new List<string>
        {
            $"新剧名: {newTitle}",
            $"原剧名: {originalTitle}",
        };
        if (!string.IsNullOrWhiteSpace(synopsis))
            lines.Add($"简介: {synopsis.Trim()}");
        lines.AddRange([
            $"集数: {episodeCount}",
            "制作公司: 未填写公司",
        ]);
        File.WriteAllText(infoPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    public static void UpdateProjectInfoField(string infoPath, string key, string value)
    {
        if (!File.Exists(infoPath)) return;
        var lines = File.ReadAllLines(infoPath).ToList();
        var prefixColon = $"{key}:";
        var prefixFull = $"{key}：";
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(prefixFull, StringComparison.Ordinal) ||
                trimmed.StartsWith(prefixColon, StringComparison.Ordinal))
            {
                lines[i] = $"{key}: {value}";
                File.WriteAllLines(infoPath, lines);
                return;
            }
        }

        lines.Add($"{key}: {value}");
        File.WriteAllLines(infoPath, lines);
    }

    private static void UpdateProjectInfoFieldIfBlank(string infoPath, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var existing = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        if (existing.TryGetValue(key, out var current) && !string.IsNullOrWhiteSpace(current))
            return;

        UpdateProjectInfoField(infoPath, key, value);
    }

    private static void LinkOrCopy(string sourcePath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        if (File.Exists(targetPath)) return;
        try
        {
            File.Copy(sourcePath, targetPath, overwrite: false);
        }
        catch
        {
            File.Copy(sourcePath, targetPath, overwrite: true);
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }
}
