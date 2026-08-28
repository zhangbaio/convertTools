using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class PublishedRecoveryOriginalTitleCompletionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static bool CanComplete(QueueProjectItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.Equals(
                item.QueueEntryDramaType,
                DeletedCopyrightProofPublishedVideoRecoveryService.RecoverySourceType,
                StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
            return IsRecoveryMetadata(ReadMetadata(Path.Combine(
                       context.SourceProjectDir,
                       "shortdrama-project.json"))) ||
                   IsRecoveryMetadata(ReadMetadata(Path.Combine(
                       context.WorkflowProjectDir,
                       "shortdrama-project.json"))) ||
                   Path.GetFileName(Path.TrimEndingDirectorySeparator(context.SourceProjectDir))
                       .EndsWith("_版权恢复", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       Path.GetFileName(Path.TrimEndingDirectorySeparator(context.SourceProjectDir)),
                       "_版权恢复",
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool Complete(
        QueueProjectItem item,
        string originalTitle,
        DramaSearchItem source,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(source);
        var title = (originalTitle ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("原剧名不能为空。", nameof(originalTitle));
        if (!CanComplete(item))
            throw new InvalidOperationException($"当前项目不是 TikTok 已发布视频恢复项目：{item.ProjectDir}");

        var currentOriginal = (item.OriginalTitle ?? string.Empty).Trim();
        if (!IsUnknownOriginalTitle(currentOriginal) &&
            !string.Equals(currentOriginal, title, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"恢复项目已有不同的真实原剧名「{currentOriginal}」，拒绝覆盖为「{title}」。");
        }

        var uploadCompletedAt = item.UploadCompletedAt;
        var uploadStatus = item.StepStates.GetValueOrDefault(
            QueueStepKeys.UploadSeries,
            QueueStepStatus.Completed);
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        foreach (var directory in new[] { context.SourceProjectDir, context.WorkflowProjectDir }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            CompleteMetadata(directory, title, source);
            CompleteInfoFile(directory, title, source.Intro);
        }

        item.OriginalTitle = title;
        if (!string.IsNullOrWhiteSpace(source.Intro)) item.Description = source.Intro.Trim();
        item.Remark = $"已确认恢复项目原剧名：{title}；保留 TikTok 已上传状态";
        item.Enabled = true;
        item.CurrentStep = string.Empty;
        item.StatusText = QueueStepStatus.Pending;
        item.LastError = string.Empty;
        foreach (var step in new[]
                 {
                     QueueStepKeys.GenerateEpisodeScript,
                     QueueStepKeys.GenerateAiDramaMaterials,
                     QueueStepKeys.GenerateAiScriptOutline,
                     QueueStepKeys.GenerateProjectImages,
                     QueueStepKeys.GenerateProofMaterial,
                     QueueStepKeys.MaterialValidate,
                 })
        {
            item.StepStates[step] = QueueStepStatus.Pending;
        }
        item.StepStates[QueueStepKeys.UploadSeries] = uploadStatus;
        item.UploadCompletedAt = uploadCompletedAt;
        item.NormalizeStepStates();

        log?.Invoke(
            $"已补全恢复项目原剧名：{title}；bookId={source.BookId}；" +
            $"声明总集数={source.EpisodeTotal}；不会重置上传状态或重新上传。");
        return true;
    }

    private static void CompleteMetadata(
        string directory,
        string originalTitle,
        DramaSearchItem source)
    {
        Directory.CreateDirectory(directory);
        var metadataPath = Path.Combine(directory, "shortdrama-project.json");
        var metadata = ReadMetadata(metadataPath);
        metadata["originalTitle"] = originalTitle;
        metadata["recoveryOriginalTitleConfirmed"] = true;
        metadata["recoveryOriginalTitleConfirmedAt"] = DateTimeOffset.Now.ToString("o");
        if (!string.IsNullOrWhiteSpace(source.BookId)) metadata["bookId"] = source.BookId.Trim();
        if (!string.IsNullOrWhiteSpace(source.Intro))
        {
            metadata["intro"] = source.Intro.Trim();
            metadata["description"] = source.Intro.Trim();
        }
        if (!string.IsNullOrWhiteSpace(source.Category)) metadata["category"] = source.Category.Trim();
        if (source.EpisodeTotal > 0) metadata["declaredEpisodeCount"] = source.EpisodeTotal;
        metadata["updatedAt"] = DateTimeOffset.Now.ToString("o");
        File.WriteAllText(metadataPath, metadata.ToJsonString(JsonOptions), Encoding.UTF8);
    }

    private static void CompleteInfoFile(string directory, string originalTitle, string intro)
    {
        var infoPath = Path.Combine(directory, "短剧信息.txt");
        if (!File.Exists(infoPath)) return;
        ProjectWorkspaceService.UpdateProjectInfoField(infoPath, "原剧名", originalTitle);
        if (!string.IsNullOrWhiteSpace(intro))
            ProjectWorkspaceService.UpdateProjectInfoFieldIfBlank(infoPath, "简介", intro.Trim());
    }

    private static bool IsUnknownOriginalTitle(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(
            value.Trim(),
            DeletedCopyrightProofPublishedVideoRecoveryService.UnknownOriginalTitle,
            StringComparison.Ordinal) ||
        value.Trim().StartsWith("未知（TikTok", StringComparison.Ordinal);

    private static bool IsRecoveryMetadata(JsonObject metadata) =>
        ReadBool(metadata, "tiktokPublishedRecovery") ||
        string.Equals(
            ReadString(metadata, "queueEntryDramaType", "queue_entry_drama_type"),
            DeletedCopyrightProofPublishedVideoRecoveryService.RecoverySourceType,
            StringComparison.Ordinal) ||
        string.Equals(
            ReadString(metadata, "importMode"),
            DeletedCopyrightProofPublishedVideoRecoveryService.RecoverySourceType,
            StringComparison.Ordinal);

    private static JsonObject ReadMetadata(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static string ReadString(JsonObject metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!metadata.TryGetPropertyValue(key, out var node) || node is null) continue;
            try { return node.GetValue<string>()?.Trim() ?? string.Empty; }
            catch { return node.ToJsonString().Trim('"').Trim(); }
        }
        return string.Empty;
    }

    private static bool ReadBool(JsonObject metadata, string key)
    {
        if (!metadata.TryGetPropertyValue(key, out var node) || node is null) return false;
        try { return node.GetValue<bool>(); }
        catch { return bool.TryParse(ReadString(metadata, key), out var value) && value; }
    }
}
