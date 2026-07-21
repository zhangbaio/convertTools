using System.Text.Json;
using System.Text.RegularExpressions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

public sealed record QueueProjectTitleRenameResult(
    string OldTitle,
    string NewTitle,
    string SourceProjectDir,
    string OldWorkflowProjectDir,
    string NewWorkflowProjectDir,
    bool WorkflowDirectoryRenamed,
    IReadOnlyList<string> UpdatedFiles,
    bool ResetPoster,
    bool ResetMaterialValidate,
    bool ResetProofMaterial,
    bool ResetUpload);

/// <summary>手动修改队列项目的新剧名，并同步依赖该名称的本地状态。</summary>
public static class QueueProjectTitleRenameService
{
    private const string MetadataFile = "shortdrama-project.json";
    private const string DramaInfoFile = "短剧信息.txt";
    private const string LegacyManifestFile = "tiktok-upload-manifest.json";
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi", ".flv", ".wmv",
    };

    private static readonly Regex EpisodePattern = new(@"第\s*(\d+)\s*集", RegexOptions.Compiled);
    private static readonly Regex ForbiddenFileNameChars = new(@"[<>:""/\\|?*\x00-\x1f]", RegexOptions.Compiled);

    public static QueueProjectTitleRenameResult RenameNewTitle(
        string workspaceRoot,
        string projectDir,
        string newTitle)
    {
        var root = RequireWorkspaceRoot(workspaceRoot);
        var requestedProjectDir = Path.GetFullPath(projectDir);
        var title = NormalizeTitle(newTitle);
        ValidateTitle(title);

        var queueState = WorkspaceQueueDatabase.Load(root);
        var items = WorkspaceQueueService.ScanProjects(root).ToList();
        var target = items.FirstOrDefault(item =>
            string.Equals(Path.GetFullPath(item.ProjectDir), requestedProjectDir, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("未在当前工作目录队列中找到该项目。");

        EnsureNoDuplicateTitle(items, target, title);

        var context = ProjectWorkspaceService.LoadContext(target.ProjectDir);
        var sourceDir = Path.GetFullPath(context.SourceProjectDir);
        var oldWorkflowDir = Path.GetFullPath(context.WorkflowProjectDir);
        var oldTitle = FirstNonEmpty(
            target.NewTitle,
            ReadInfoValue(Path.Combine(oldWorkflowDir, DramaInfoFile), "新剧名", "剧名", "短标题"),
            target.Title,
            target.OriginalTitle,
            Path.GetFileName(sourceDir));

        if (string.Equals(oldTitle, title, StringComparison.Ordinal))
            throw new InvalidOperationException("新剧名没有变化。");

        var updatedFiles = new List<string>();
        UpdateWorkflowInfo(oldWorkflowDir, oldTitle, title, updatedFiles);
        UpdateSourceInfoIfNeeded(sourceDir, oldTitle, title, updatedFiles);

        var newWorkflowDir = ProjectWorkspaceService.SyncWorkflowProjectDirName(sourceDir, title);
        newWorkflowDir = Path.GetFullPath(newWorkflowDir);
        var workflowRenamed = !string.Equals(oldWorkflowDir, newWorkflowDir, StringComparison.OrdinalIgnoreCase);
        var renamedUploadVideoPaths = RenameStagedUploadVideos(
            oldWorkflowDir,
            newWorkflowDir,
            title,
            updatedFiles);

        UpdateProjectMetadata(Path.Combine(sourceDir, MetadataFile), title, newWorkflowDir, sourceDir, updatedFiles);
        UpdateProjectMetadata(Path.Combine(newWorkflowDir, MetadataFile), title, newWorkflowDir, sourceDir, updatedFiles);
        UpdateUploadState(newWorkflowDir, oldTitle, title, updatedFiles);
        UpdateManifest(root, sourceDir, oldWorkflowDir, newWorkflowDir, title, renamedUploadVideoPaths, updatedFiles);
        target.NewTitle = title;
        ProjectWorkspaceService.RefreshQueueItemMetadata(target);
        target.NewTitle = title;
        var reset = ResetDependentStepStates(target, renamedUploadVideoPaths.Count > 0);
        if (reset.ProofMaterial)
            DeleteProofMaterialOutput(newWorkflowDir);
        target.NormalizeStepStates();

        WorkspaceQueueDatabase.Save(root, items, queueState.Options);

        return new QueueProjectTitleRenameResult(
            OldTitle: oldTitle,
            NewTitle: title,
            SourceProjectDir: sourceDir,
            OldWorkflowProjectDir: oldWorkflowDir,
            NewWorkflowProjectDir: newWorkflowDir,
            WorkflowDirectoryRenamed: workflowRenamed,
            UpdatedFiles: updatedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ResetPoster: reset.Poster,
            ResetMaterialValidate: reset.MaterialValidate,
            ResetProofMaterial: reset.ProofMaterial,
            ResetUpload: reset.Upload);
    }

    private static string RequireWorkspaceRoot(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new InvalidOperationException("工作目录为空。");

        var root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root))
            throw new InvalidOperationException($"工作目录不存在：{root}");
        return root;
    }

    private static string NormalizeTitle(string? value) => (value ?? "").Trim();

    private static void ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("新剧名不能为空。");

        if (title.Length > 80)
            throw new InvalidOperationException("新剧名过长，请控制在 80 个字符以内。");

        var invalid = Path.GetInvalidFileNameChars().Where(title.Contains).Distinct().ToArray();
        if (invalid.Length > 0)
            throw new InvalidOperationException($"新剧名包含不能用于目录名的字符：{string.Join(' ', invalid)}");
    }

    private static void EnsureNoDuplicateTitle(
        IReadOnlyList<QueueProjectItem> items,
        QueueProjectItem target,
        string newTitle)
    {
        var targetDir = Path.GetFullPath(target.ProjectDir);
        var duplicate = items.FirstOrDefault(item =>
            !string.Equals(Path.GetFullPath(item.ProjectDir), targetDir, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(FirstNonEmpty(item.NewTitle, item.Title).Trim(), newTitle, StringComparison.Ordinal));
        if (duplicate is not null)
            throw new InvalidOperationException($"新剧名与队列中其它项目重复：{duplicate.OriginalTitle}");
    }

    private static void UpdateWorkflowInfo(
        string workflowDir,
        string oldTitle,
        string newTitle,
        List<string> updatedFiles)
    {
        Directory.CreateDirectory(workflowDir);
        var path = Path.Combine(workflowDir, DramaInfoFile);
        var existing = ProjectInfoTextHelper.ParseInfoFile(path);
        var updates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["新剧名"] = newTitle,
            ["短标题"] = newTitle,
        };

        AddIfRelated(existing, updates, "剧名", oldTitle, newTitle);
        AddIfRelated(existing, updates, "标题", oldTitle, newTitle);
        ProjectInfoTextHelper.UpdateFields(path, updates);
        updatedFiles.Add(path);
    }

    private static void UpdateSourceInfoIfNeeded(
        string sourceDir,
        string oldTitle,
        string newTitle,
        List<string> updatedFiles)
    {
        var path = Path.Combine(sourceDir, DramaInfoFile);
        var existing = ProjectInfoTextHelper.ParseInfoFile(path);
        if (existing.Count == 0) return;

        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        AddIfRelated(existing, updates, "新剧名", oldTitle, newTitle);
        AddIfRelated(existing, updates, "短标题", oldTitle, newTitle);
        AddIfRelated(existing, updates, "标题", oldTitle, newTitle);
        if (updates.Count == 0) return;

        ProjectInfoTextHelper.UpdateFields(path, updates);
        updatedFiles.Add(path);
    }

    private static void AddIfRelated(
        IReadOnlyDictionary<string, string> existing,
        IDictionary<string, string> updates,
        string key,
        string oldTitle,
        string newTitle)
    {
        if (!existing.TryGetValue(key, out var value)) return;
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), oldTitle, StringComparison.Ordinal) ||
            string.Equals(value.Trim(), newTitle, StringComparison.Ordinal))
        {
            updates[key] = newTitle;
        }
    }

    private static void UpdateProjectMetadata(
        string path,
        string newTitle,
        string workflowDir,
        string sourceDir,
        List<string> updatedFiles)
    {
        var payload = ReadJsonObject(path);
        payload["newTitle"] = newTitle;
        payload["new_title"] = newTitle;
        payload["workflowDirName"] = Path.GetFileName(workflowDir);
        payload["workflowProjectDir"] = workflowDir;
        payload.TryAdd("sourceProjectDir", sourceDir);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        updatedFiles.Add(path);
    }

    private static void UpdateUploadState(
        string workflowDir,
        string oldTitle,
        string newTitle,
        List<string> updatedFiles)
    {
        var state = TikTokUploadStateStore.LoadState(workflowDir)
            .ToDictionary(pair => pair.Key, pair => JsonElementToObject(pair.Value), StringComparer.Ordinal);
        if (state.Count == 0)
            return;

        state["new_title"] = newTitle;
        ReplaceStringIfRelated(state, "title", oldTitle, newTitle);
        ReplaceStringIfRelated(state, "display_title", oldTitle, newTitle);
        ReplaceStringIfRelated(state, "last_upload_title", oldTitle, newTitle);

        if (state.TryGetValue("platform_series_lookup", out var lookupRaw) &&
            lookupRaw is Dictionary<string, object?> lookup)
        {
            var status = lookup.GetValueOrDefault("status")?.ToString() ?? "";
            if (string.Equals(status, "not_found", StringComparison.OrdinalIgnoreCase))
            {
                state.Remove("platform_series_lookup");
            }
            else
            {
                ReplaceStringIfRelated(lookup, "matched_title", oldTitle, newTitle);
                lookup["searched_titles"] = ReplaceTitleList(lookup.GetValueOrDefault("searched_titles"), oldTitle, newTitle);
            }
        }

        TikTokUploadStateStore.SaveState(workflowDir, state);
        updatedFiles.Add(TikTokUploadStateStore.StateFilePath(workflowDir));
    }

    private static IReadOnlyDictionary<string, string> RenameStagedUploadVideos(
        string oldWorkflowDir,
        string workflowDir,
        string newTitle,
        List<string> updatedFiles)
    {
        var stagingRoot = Path.Combine(workflowDir, TikTokUploadStagingService.StagingDirName);
        if (!Directory.Exists(stagingRoot))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var safeTitle = SanitizeFileTitle(newTitle);
        var plan = new List<(string Source, string Temp, string Final)>();
        foreach (var source in Directory.EnumerateFiles(stagingRoot, "*.*", SearchOption.AllDirectories)
                     .Where(path => VideoExtensions.Contains(Path.GetExtension(path))))
        {
            var final = BuildRenamedEpisodePath(source, safeTitle);
            if (string.IsNullOrWhiteSpace(final) ||
                string.Equals(Path.GetFullPath(source), Path.GetFullPath(final), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(final))
                throw new InvalidOperationException($"无法修改上传视频名称，目标文件已存在：{final}");

            var temp = Path.Combine(Path.GetDirectoryName(source)!, $".rename-{Guid.NewGuid():N}{Path.GetExtension(source)}");
            plan.Add((source, temp, final));
        }

        if (plan.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in plan)
            File.Move(step.Source, step.Temp);
        foreach (var step in plan)
            File.Move(step.Temp, step.Final);

        var renamedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in plan)
        {
            var final = Path.GetFullPath(step.Final);
            var currentSource = Path.GetFullPath(step.Source);
            renamedPaths[currentSource] = final;

            var legacySource = RewriteWorkflowRoot(currentSource, workflowDir, oldWorkflowDir);
            renamedPaths[legacySource] = final;
            updatedFiles.Add(final);
        }

        return renamedPaths;
    }

    private static string BuildRenamedEpisodePath(string sourcePath, string safeTitle)
    {
        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        var match = EpisodePattern.Match(fileName);
        if (!match.Success)
            return "";

        var episode = match.Groups[1].Value;
        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".mp4";

        return Path.Combine(Path.GetDirectoryName(sourcePath)!, $"{safeTitle}-第{episode}集{extension.ToLowerInvariant()}");
    }

    private static void UpdateManifest(
        string workspaceRoot,
        string sourceDir,
        string oldWorkflowDir,
        string workflowDir,
        string newTitle,
        IReadOnlyDictionary<string, string> renamedUploadVideoPaths,
        List<string> updatedFiles)
    {
        var document = ProjectStateDocumentStore.LoadDocument(
                workspaceRoot,
                sourceDir,
                TikTokUploadManifestService.DocumentType)
            .ToDictionary(pair => pair.Key, pair => JsonElementToObject(pair.Value), StringComparer.Ordinal);
        if (document.Count > 0)
        {
            document["display_title"] = newTitle;
            document["workflow_project_dir"] = workflowDir;
            RewritePathList(document, "upload_video_paths", oldWorkflowDir, workflowDir, renamedUploadVideoPaths);
            RewritePathList(document, "video_paths", oldWorkflowDir, workflowDir, renamedUploadVideoPaths);
            RewritePathValue(document, "poster_path", oldWorkflowDir, workflowDir, renamedUploadVideoPaths);
            ProjectStateDocumentStore.SaveDocument(
                workspaceRoot,
                sourceDir,
                TikTokUploadManifestService.DocumentType,
                document,
                workflowDir);
        }

        var path = Path.Combine(workflowDir, LegacyManifestFile);
        var legacy = ReadJsonObject(path, mustExist: true);
        if (legacy.Count == 0) return;

        legacy["display_title"] = newTitle;
        legacy["workflow_project_dir"] = workflowDir;
        RewritePathList(legacy, "upload_video_paths", oldWorkflowDir, workflowDir, renamedUploadVideoPaths);
        RewritePathList(legacy, "video_paths", oldWorkflowDir, workflowDir, renamedUploadVideoPaths);
        RewritePathValue(legacy, "poster_path", oldWorkflowDir, workflowDir, renamedUploadVideoPaths);
        File.WriteAllText(path, JsonSerializer.Serialize(legacy, new JsonSerializerOptions { WriteIndented = true }));
        updatedFiles.Add(path);
    }

    private static (bool Poster, bool MaterialValidate, bool ProofMaterial, bool Upload) ResetDependentStepStates(
        QueueProjectItem item,
        bool stagedUploadVideosRenamed)
    {
        var uploadCompleted = item.StepStates.GetValueOrDefault(QueueStepKeys.UploadSeries) == QueueStepStatus.Completed;
        item.StepStates[QueueStepKeys.RewriteInfo] = QueueStepStatus.Completed;
        var resetPoster = ResetCompletedStep(item, QueueStepKeys.GeneratePoster);
        var resetProofMaterial = ResetCompletedStep(item, QueueStepKeys.GenerateProofMaterial);
        var resetMaterialValidate = (resetPoster || resetProofMaterial || stagedUploadVideosRenamed) &&
                                    ResetCompletedStep(item, QueueStepKeys.MaterialValidate);

        if (uploadCompleted)
        {
            item.StatusText = QueueStepStatus.Completed;
            item.CurrentStep = "";
            item.LastError = "";
            return (resetPoster, resetMaterialValidate, resetProofMaterial, false);
        }

        if (resetPoster || resetProofMaterial || resetMaterialValidate)
        {
            item.StatusText = QueueStepStatus.Pending;
            item.CurrentStep = "";
            item.LastError = "";
        }

        return (resetPoster, resetMaterialValidate, resetProofMaterial, false);
    }

    private static void DeleteProofMaterialOutput(string workflowDir)
    {
        var path = Path.Combine(workflowDir, TikTokProofMaterialService.ProofPdfFileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    private static bool ResetCompletedStep(QueueProjectItem item, string stepKey)
    {
        if (item.StepStates.GetValueOrDefault(stepKey) != QueueStepStatus.Completed)
            return false;

        item.StepStates[stepKey] = QueueStepStatus.Pending;
        return true;
    }

    private static Dictionary<string, object?> ReadJsonObject(string path, bool mustExist = false)
    {
        if (!File.Exists(path))
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(path))
                   ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        catch
        {
            if (mustExist)
                return new Dictionary<string, object?>(StringComparer.Ordinal);
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }

    private static void ReplaceStringIfRelated(
        IDictionary<string, object?> payload,
        string key,
        string oldTitle,
        string newTitle)
    {
        if (!payload.TryGetValue(key, out var raw)) return;
        var value = raw?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), oldTitle, StringComparison.Ordinal) ||
            string.Equals(value.Trim(), newTitle, StringComparison.Ordinal))
        {
            payload[key] = newTitle;
        }
    }

    private static List<object?> ReplaceTitleList(object? raw, string oldTitle, string newTitle)
    {
        var values = raw switch
        {
            IEnumerable<string> strings => strings,
            IEnumerable<object?> objects => objects.Select(item => item?.ToString() ?? ""),
            _ => Array.Empty<string>(),
        };

        var result = new List<object?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var text = (value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (string.Equals(text, oldTitle, StringComparison.Ordinal))
                text = newTitle;
            if (seen.Add(text))
                result.Add(text);
        }
        if (seen.Add(newTitle))
            result.Add(newTitle);
        return result;
    }

    private static void RewritePathList(
        IDictionary<string, object?> payload,
        string key,
        string oldWorkflowDir,
        string workflowDir,
        IReadOnlyDictionary<string, string> renamedPaths)
    {
        if (!payload.TryGetValue(key, out var raw)) return;

        var values = ExtractStringList(raw);
        if (values.Count == 0) return;

        payload[key] = values
            .Select(value => RewritePath(value, oldWorkflowDir, workflowDir, renamedPaths))
            .ToList();
    }

    private static void RewritePathValue(
        IDictionary<string, object?> payload,
        string key,
        string oldWorkflowDir,
        string workflowDir,
        IReadOnlyDictionary<string, string> renamedPaths)
    {
        if (!payload.TryGetValue(key, out var raw)) return;

        var value = ExtractString(raw);
        if (string.IsNullOrWhiteSpace(value)) return;

        payload[key] = RewritePath(value, oldWorkflowDir, workflowDir, renamedPaths);
    }

    private static List<string> ExtractStringList(object? raw)
    {
        if (raw is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
                return new List<string>();

            return element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "")
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
        }

        if (raw is IEnumerable<string> strings)
            return strings.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();

        if (raw is string)
            return new List<string>();

        if (raw is IEnumerable<object?> objects)
            return objects
                .Select(ExtractString)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList()!;

        return new List<string>();
    }

    private static string? ExtractString(object? raw) => raw switch
    {
        null => null,
        string value => value,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => raw.ToString(),
    };

    private static string RewritePath(
        string value,
        string oldWorkflowDir,
        string workflowDir,
        IReadOnlyDictionary<string, string> renamedPaths)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        string fullPath;
        try
        {
            if (!Path.IsPathFullyQualified(value))
                return value;
            fullPath = Path.GetFullPath(value);
        }
        catch
        {
            return value;
        }

        if (renamedPaths.TryGetValue(fullPath, out var renamed))
            return renamed;

        var rewritten = RewriteWorkflowRoot(fullPath, oldWorkflowDir, workflowDir);
        return renamedPaths.TryGetValue(rewritten, out renamed) ? renamed : rewritten;
    }

    private static string RewriteWorkflowRoot(string path, string oldRoot, string newRoot)
    {
        var fullPath = Path.GetFullPath(path);
        var oldFull = Path.GetFullPath(oldRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var newFull = Path.GetFullPath(newRoot);

        if (string.Equals(fullPath, oldFull, StringComparison.OrdinalIgnoreCase))
            return newFull;

        var prefix = oldFull + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return fullPath;

        var relative = Path.GetRelativePath(oldFull, fullPath);
        return Path.GetFullPath(Path.Combine(newFull, relative));
    }

    private static string SanitizeFileTitle(string value)
    {
        var text = ForbiddenFileNameChars.Replace(value.Trim(), " ");
        text = Regex.Replace(text, @"\s+", " ").Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(text)) return "短剧视频";
        return text.Length > 80 ? text[..80] : text;
    }

    private static string ReadInfoValue(string infoPath, params string[] keys)
    {
        var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        foreach (var key in keys)
        {
            if (info.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return "";
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

    private static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
        _ => null,
    };
}
