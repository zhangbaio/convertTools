using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

public sealed record QueueProjectMovePlan(
    string SourceWorkspaceRoot,
    string TargetWorkspaceRoot,
    QueueProjectItem SourceItem,
    string SourceProjectDir,
    string TargetProjectDir,
    string SourceWorkflowProjectDir,
    string TargetWorkflowProjectDir,
    bool MoveSourceDirectory,
    bool MoveWorkflowDirectory);

public sealed record QueueProjectMoveEntry(
    string OriginalProjectDir,
    string ProjectDir,
    string OriginalWorkflowProjectDir,
    string WorkflowProjectDir,
    QueueProjectItem Item);

public sealed record QueueProjectMoveResult(IReadOnlyList<QueueProjectMoveEntry> Entries)
{
    public int Count => Entries.Count;
}

public static class QueueProjectMoveService
{
    private const string LegacyManifestFile = "tiktok-upload-manifest.json";

    public static QueueProjectMovePlan PlanProjectMove(
        string sourceWorkspaceRoot,
        string targetWorkspaceRoot,
        QueueProjectItem item)
    {
        var sourceRoot = Path.GetFullPath(sourceWorkspaceRoot);
        var targetRoot = Path.GetFullPath(targetWorkspaceRoot);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"源账号工作目录不存在：{sourceRoot}");
        if (!Directory.Exists(targetRoot))
            throw new DirectoryNotFoundException($"目标账号工作目录不存在：{targetRoot}");
        if (string.IsNullOrWhiteSpace(item.ProjectDir))
            throw new InvalidOperationException("项目目录为空，无法移动。");

        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var sourceProjectDir = Path.GetFullPath(context.SourceProjectDir);
        if (!Directory.Exists(sourceProjectDir))
            throw new DirectoryNotFoundException($"项目目录不存在：{sourceProjectDir}");
        if (!WorkspaceProjectScanner.IsValidProjectDirectory(sourceProjectDir))
            throw new InvalidOperationException($"不是有效的项目目录：{sourceProjectDir}");

        var sourceName = Path.GetFileName(TrimDirectorySeparators(sourceProjectDir));
        if (string.IsNullOrWhiteSpace(sourceName))
            throw new InvalidOperationException($"项目目录不能是磁盘根目录：{sourceProjectDir}");

        var sameWorkspace = IsSamePath(sourceRoot, targetRoot);
        var targetProjectDir = sameWorkspace
            ? sourceProjectDir
            : Path.GetFullPath(Path.Combine(targetRoot, sourceName));
        EnsureTargetWithinRoot(targetRoot, targetProjectDir);

        var sourceWorkflowDir = Path.GetFullPath(context.WorkflowProjectDir);
        var workflowExists = Directory.Exists(sourceWorkflowDir);
        var workflowInsideSource = workflowExists && IsSameOrChildPath(sourceProjectDir, sourceWorkflowDir);
        var targetWorkflowDir = ResolveTargetWorkflowDir(
            targetRoot,
            sourceProjectDir,
            targetProjectDir,
            sourceWorkflowDir,
            workflowInsideSource,
            sameWorkspace);
        EnsureTargetWithinRoot(targetRoot, targetWorkflowDir);

        if (!sameWorkspace && Directory.Exists(targetProjectDir))
            throw new IOException($"目标账号工作目录已存在同名项目：{targetProjectDir}");
        if (!sameWorkspace &&
            !workflowInsideSource &&
            !IsSamePath(sourceWorkflowDir, targetWorkflowDir) &&
            Directory.Exists(targetWorkflowDir))
        {
            throw new IOException($"目标账号 workflow 已存在同名目录：{targetWorkflowDir}");
        }

        return new QueueProjectMovePlan(
            sourceRoot,
            targetRoot,
            item,
            sourceProjectDir,
            targetProjectDir,
            sourceWorkflowDir,
            targetWorkflowDir,
            MoveSourceDirectory: !IsSamePath(sourceProjectDir, targetProjectDir),
            MoveWorkflowDirectory: workflowExists &&
                                   !workflowInsideSource &&
                                   !IsSamePath(sourceWorkflowDir, targetWorkflowDir));
    }

    public static QueueProjectMoveEntry ExecuteMove(
        QueueProjectMovePlan plan,
        TikTokAccountProfile targetAccount)
    {
        var documents = ProjectStateDocumentStore.LoadProjectDocuments(
            plan.SourceWorkspaceRoot,
            plan.SourceProjectDir);
        var moved = new List<(string From, string To)>();

        try
        {
            if (plan.MoveWorkflowDirectory)
                MoveDirectory(plan.SourceWorkflowProjectDir, plan.TargetWorkflowProjectDir, moved);
            if (plan.MoveSourceDirectory)
                MoveDirectory(plan.SourceProjectDir, plan.TargetProjectDir, moved);

            ProjectWorkspaceService.UpdateMovedWorkspaceMetadata(
                plan.TargetProjectDir,
                plan.TargetWorkflowProjectDir);
            SaveMovedStateDocuments(plan, targetAccount, documents);

            var item = BuildMovedQueueItem(plan.SourceItem, plan.TargetProjectDir, targetAccount);
            return new QueueProjectMoveEntry(
                plan.SourceProjectDir,
                plan.TargetProjectDir,
                plan.SourceWorkflowProjectDir,
                plan.TargetWorkflowProjectDir,
                item);
        }
        catch
        {
            RollBackMovedDirectories(moved);
            throw;
        }
    }

    public static QueueProjectItem BuildMovedQueueItem(
        QueueProjectItem source,
        string targetProjectDir,
        TikTokAccountProfile targetAccount)
    {
        var item = QueueProjectItem.FromPayload(source.ToPayload());
        item.ProjectDir = Path.GetFullPath(targetProjectDir);
        item.AccountProfileId = targetAccount.Id;
        item.AccountProfileName = targetAccount.DisplayName;
        item.Enabled = true;
        ResetUploadStateForNewAccount(item);
        ProjectWorkspaceService.RefreshQueueItemMetadata(item);
        item.NormalizeStepStates();
        return item;
    }

    private static void MoveDirectory(string source, string target, List<(string From, string To)> moved)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(source, target);
        moved.Add((source, target));
    }

    private static void RollBackMovedDirectories(List<(string From, string To)> moved)
    {
        for (var i = moved.Count - 1; i >= 0; i--)
        {
            var (from, to) = moved[i];
            try
            {
                if (!Directory.Exists(to) || Directory.Exists(from))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(from)!);
                Directory.Move(to, from);
            }
            catch
            {
                // Best effort rollback. The original exception is more useful to callers.
            }
        }
    }

    private static void SaveMovedStateDocuments(
        QueueProjectMovePlan plan,
        TikTokAccountProfile targetAccount,
        Dictionary<string, Dictionary<string, object?>> documents)
    {
        foreach (var (documentType, payload) in documents)
        {
            if (string.Equals(documentType, ProjectStateDocumentStore.UploadStateDocumentType, StringComparison.Ordinal))
                continue;

            var movedPayload = RewriteDocumentPaths(
                payload,
                plan.SourceProjectDir,
                plan.TargetProjectDir,
                plan.SourceWorkflowProjectDir,
                plan.TargetWorkflowProjectDir);
            if (string.Equals(documentType, TikTokUploadManifestService.DocumentType, StringComparison.Ordinal))
                UpdateManifestForTargetAccount(
                    movedPayload,
                    plan.TargetWorkspaceRoot,
                    plan.TargetProjectDir,
                    plan.TargetWorkflowProjectDir,
                    targetAccount);

            ProjectStateDocumentStore.SaveDocument(
                plan.TargetWorkspaceRoot,
                plan.TargetProjectDir,
                documentType,
                movedPayload,
                plan.TargetWorkflowProjectDir);
        }

        UpdateLegacyManifestIfPresent(plan, targetAccount);
        TikTokUploadStateStore.SaveState(plan.TargetWorkflowProjectDir, new Dictionary<string, object?>());

        if (plan.MoveSourceDirectory || !IsSamePath(plan.SourceWorkspaceRoot, plan.TargetWorkspaceRoot))
            ProjectStateDocumentStore.DeleteProjectDocuments(plan.SourceWorkspaceRoot, plan.SourceProjectDir);
    }

    private static void UpdateLegacyManifestIfPresent(
        QueueProjectMovePlan plan,
        TikTokAccountProfile targetAccount)
    {
        var manifestPath = Path.Combine(plan.TargetWorkflowProjectDir, LegacyManifestFile);
        if (!File.Exists(manifestPath))
            return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            var payload = doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value), StringComparer.Ordinal);
            payload = RewriteDocumentPaths(
                payload,
                plan.SourceProjectDir,
                plan.TargetProjectDir,
                plan.SourceWorkflowProjectDir,
                plan.TargetWorkflowProjectDir);
            UpdateManifestForTargetAccount(
                payload,
                plan.TargetWorkspaceRoot,
                plan.TargetProjectDir,
                plan.TargetWorkflowProjectDir,
                targetAccount);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A stale legacy manifest should not block moving the project.
        }
    }

    private static void UpdateManifestForTargetAccount(
        Dictionary<string, object?> payload,
        string targetWorkspaceRoot,
        string targetProjectDir,
        string targetWorkflowProjectDir,
        TikTokAccountProfile targetAccount)
    {
        payload["project_dir"] = targetProjectDir;
        payload["workflow_project_dir"] = targetWorkflowProjectDir;
        payload["series_url"] = FirstNonEmpty(targetAccount.TiktokSeriesUrl, TikTokUrls.DefaultSeriesDraftUrl);
        var publishConfig = TikTokUploadManifestService.BuildPublishConfigSnapshot(
            targetAccount,
            targetWorkflowProjectDir);
        publishConfig["upload_profile_path"] = targetWorkspaceRoot;
        payload["publish_config"] = publishConfig;
        payload["web_upload_pending"] = false;
    }

    private static void ResetUploadStateForNewAccount(QueueProjectItem item)
    {
        var uploadStatus = item.StepStates.GetValueOrDefault(QueueStepKeys.UploadSeries, QueueStepStatus.Pending);
        var uploadHadState =
            !string.Equals(uploadStatus, QueueStepStatus.Pending, StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(item.UploadCompletedAt) ||
            !string.IsNullOrWhiteSpace(item.ManualUploadStatus) ||
            string.Equals(item.CurrentStep, QueueStepKeys.UploadSeries, StringComparison.Ordinal);

        item.StepStates[QueueStepKeys.UploadSeries] = QueueStepStatus.Pending;
        item.UploadCompletedAt = "";
        item.ManualUploadStatus = "";

        if (string.Equals(item.CurrentStep, QueueStepKeys.UploadSeries, StringComparison.Ordinal) ||
            string.Equals(item.StatusText, QueueStepStatus.Completed, StringComparison.Ordinal) ||
            string.Equals(item.StatusText, QueueStepStatus.WaitingUploadSlot, StringComparison.Ordinal) ||
            string.Equals(item.StatusText, QueueStepStatus.ManualIntervention, StringComparison.Ordinal) ||
            string.Equals(item.StatusText, QueueStepStatus.Running, StringComparison.Ordinal) ||
            uploadHadState && string.Equals(item.StatusText, QueueStepStatus.Failed, StringComparison.Ordinal))
        {
            item.StatusText = QueueStepStatus.Pending;
            item.CurrentStep = "";
            item.LastError = "";
        }
    }

    private static string ResolveTargetWorkflowDir(
        string targetRoot,
        string sourceProjectDir,
        string targetProjectDir,
        string sourceWorkflowDir,
        bool workflowInsideSource,
        bool sameWorkspace)
    {
        if (sameWorkspace)
            return sourceWorkflowDir;

        if (workflowInsideSource)
        {
            var relative = Path.GetRelativePath(sourceProjectDir, sourceWorkflowDir);
            return Path.GetFullPath(Path.Combine(targetProjectDir, relative));
        }

        var workflowName = Path.GetFileName(TrimDirectorySeparators(sourceWorkflowDir));
        if (string.IsNullOrWhiteSpace(workflowName))
            workflowName = Path.GetFileName(TrimDirectorySeparators(sourceProjectDir));
        return Path.GetFullPath(Path.Combine(targetRoot, "workflow", workflowName));
    }

    private static Dictionary<string, object?> RewriteDocumentPaths(
        Dictionary<string, object?> payload,
        string sourceProjectDir,
        string targetProjectDir,
        string sourceWorkflowDir,
        string targetWorkflowDir)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in payload)
        {
            result[key] = RewriteValue(
                value,
                (sourceProjectDir, targetProjectDir),
                (sourceWorkflowDir, targetWorkflowDir));
        }

        return result;
    }

    private static object? RewriteValue(object? value, params (string From, string To)[] mappings)
    {
        return value switch
        {
            string text => RewritePathText(text, mappings),
            Dictionary<string, object?> dict => dict.ToDictionary(
                pair => pair.Key,
                pair => RewriteValue(pair.Value, mappings),
                StringComparer.Ordinal),
            IEnumerable<object?> list => list.Select(item => RewriteValue(item, mappings)).ToList(),
            _ => value,
        };
    }

    private static string RewritePathText(string text, params (string From, string To)[] mappings)
    {
        if (string.IsNullOrWhiteSpace(text) || !Path.IsPathFullyQualified(text))
            return text;

        try
        {
            var full = Path.GetFullPath(text);
            foreach (var (from, to) in mappings)
            {
                if (!IsSameOrChildPath(from, full))
                    continue;

                if (IsSamePath(from, full))
                    return to;

                var relative = Path.GetRelativePath(from, full);
                return Path.GetFullPath(Path.Combine(to, relative));
            }
        }
        catch
        {
            return text;
        }

        return text;
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

    private static void EnsureTargetWithinRoot(string targetRoot, string targetPath)
    {
        if (!IsSameOrChildPath(targetRoot, targetPath))
            throw new InvalidOperationException($"目标路径不在目标账号工作目录内：{targetPath}");
    }

    private static bool IsSamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrChildPath(string parent, string child)
    {
        var parentFull = Path.GetFullPath(parent);
        var childFull = Path.GetFullPath(child);
        if (IsSamePath(parentFull, childFull))
            return true;

        var relative = Path.GetRelativePath(parentFull, childFull);
        return !string.IsNullOrWhiteSpace(relative) &&
               !relative.StartsWith("..", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static string TrimDirectorySeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

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
