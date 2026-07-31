using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public sealed record TikTokPublishedVideoRecoverySource(
    string SeriesId,
    string DetailUrl,
    string StagingDirectory,
    int PlatformEpisodeCount,
    int DownloadedEpisodeCount);

/// <summary>
/// Rebuilds a copyright-proof-only queue project from videos downloaded from the
/// account's already-published TikTok series. This path is used when the original
/// source title is unknown and must never be treated as an original-source download.
/// </summary>
public static class DeletedCopyrightProofPublishedVideoRecoveryService
{
    public const string UnknownOriginalTitle = "未知（TikTok已发布视频恢复）";
    public const string RecoverySourceType = "tiktok_published_recovery";

    private const string MetadataFileName = "shortdrama-project.json";
    private const string RecoveryDirectorySuffix = "_版权恢复";

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int ResolveRequiredEpisodeCount(
        ClientSettings settings,
        TikTokAccountProfile account)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(account);

        var materialTypes = TikTokPublishConstants.NormalizeCopyrightMaterialTypes(
            account.TiktokCopyrightMaterialTypes);
        var needsAi = materialTypes.Contains(
            TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
            StringComparer.Ordinal);
        var needsEditing = materialTypes.Contains(
            TikTokPublishConstants.EditingProjectFilesMaterialType,
            StringComparer.Ordinal);
        var needsSourceInfo = materialTypes.Contains(
            TikTokPublishConstants.SourceFileInformationMaterialType,
            StringComparer.Ordinal);

        var required = TikTokProofMaterialService.ResolveTemporaryVideoEpisodeCount(
            needsAi,
            needsEditing,
            settings);
        return needsSourceInfo && required == 0 ? 1 : Math.Max(0, required);
    }

    public static string ResolveStagingDirectory(
        string workspaceRoot,
        string newTitle,
        string? seriesId = null)
    {
        var workspace = Path.GetFullPath(workspaceRoot);
        var identity = $"{(newTitle ?? string.Empty).Trim()}\n{(seriesId ?? string.Empty).Trim()}";
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..16];
        return Path.Combine(
            workspace,
            "config",
            "proof-video-recovery",
            $"{SanitizeFileName(newTitle)}-{hash}");
    }

    public static DeletedCopyrightProofProjectRecoveryResult Recover(
        string workspaceRoot,
        TikTokExecutionProjectSnapshot snapshot,
        TikTokPublishedVideoRecoverySource source,
        TikTokAccountProfile account,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(account);

        var workspace = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(workspace))
            return Fail($"当前工作目录不存在：{workspace}");

        var history = snapshot.Item;
        var newTitle = (history.NewTitle ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(newTitle))
            return Fail("恢复 TikTok 已发布视频时缺少新剧名。");

        var staging = Path.GetFullPath(source.StagingDirectory);
        if (!Directory.Exists(staging))
            return Fail($"TikTok 已发布视频暂存目录不存在：{staging}");

        var stagedVideos = EnumerateVideos(staging);
        if (stagedVideos.Count == 0)
            return Fail($"TikTok 已发布项目「{newTitle}」没有下载到可用视频。");

        var projectDir = Path.Combine(
            workspace,
            SanitizeFileName(newTitle) + RecoveryDirectorySuffix);
        try
        {
            EnsureRecoverableProjectDirectory(projectDir, newTitle, source.SeriesId);
            // Persist the recovery identity before copying. If a copy/import is
            // interrupted, the next run can safely recognize and resume this project.
            WriteRecoveryMetadata(projectDir, newTitle, source, stagedVideos.Count);
            var videosDir = Path.Combine(projectDir, "videos");
            Directory.CreateDirectory(videosDir);
            CopyDownloadedVideos(stagedVideos, videosDir, log);

            var import = LocalManualDramaImportService.Import(workspace, projectDir, log);
            WorkspaceQueueService.AddProjectsToQueue(workspace, [import.SourceProjectDir]);

            var projects = WorkspaceQueueService.ScanProjects(workspace).ToList();
            var recovered = projects.FirstOrDefault(item =>
                PathsEqual(item.ProjectDir, import.SourceProjectDir));
            if (recovered is null)
                return Fail($"平台视频已恢复，但未能加入当前队列：{projectDir}");

            recovered.Enabled = true;
            recovered.Archived = false;
            recovered.DisplayName = newTitle;
            recovered.OriginalTitle = UnknownOriginalTitle;
            recovered.NewTitle = newTitle;
            // This recovery project deliberately contains only the episodes needed by
            // the selected proof materials. Keep the queue count aligned with the
            // files that actually exist; the full TikTok count remains in metadata.
            recovered.EpisodeCount = stagedVideos.Count;
            recovered.AccountProfileId = account.Id;
            recovered.AccountProfileName = account.DisplayName;
            recovered.QueueEntryDramaType = RecoverySourceType;
            recovered.QueuedAt = DateTimeOffset.Now.ToString("o");
            recovered.UploadCompletedAt = string.IsNullOrWhiteSpace(history.UploadCompletedAt)
                ? DateTimeOffset.Now.ToString("o")
                : history.UploadCompletedAt;
            recovered.ProofMaterialStatementDate = history.ProofMaterialStatementDate;
            recovered.Remark =
                $"由 TikTok 已发布视频恢复，仅用于补全版权证明；平台项目 {source.SeriesId}；" +
                $"已恢复 {stagedVideos.Count} 集";
            recovered.CurrentStep = string.Empty;
            recovered.StatusText = QueueStepStatus.Pending;
            recovered.LastError = string.Empty;
            recovered.ManualUploadStatus = string.Empty;
            recovered.StepStates = CompletedProofOnlyStepStates();
            recovered.NormalizeStepStates();

            WorkspaceQueueService.SaveRunOptions(
                workspace,
                projects,
                WorkspaceQueueService.LoadRunOptions(workspace));
            log?.Invoke(
                $"已从 TikTok 已发布视频重建版权项目：{newTitle}；" +
                $"下载 {stagedVideos.Count} 集；不会重新上传剧集。");
            return new DeletedCopyrightProofProjectRecoveryResult(
                true,
                $"已从 TikTok 已发布视频恢复：{newTitle}",
                recovered);
        }
        catch (Exception ex)
        {
            return Fail($"从 TikTok 已发布视频恢复「{newTitle}」失败：{ex.Message}");
        }
    }

    private static Dictionary<string, string> CompletedProofOnlyStepStates() =>
        new()
        {
            [QueueStepKeys.Download] = QueueStepStatus.Completed,
            [QueueStepKeys.RewriteInfo] = QueueStepStatus.Completed,
            [QueueStepKeys.GeneratePoster] = QueueStepStatus.Completed,
            [QueueStepKeys.GenerateProjectImages] = QueueStepStatus.Completed,
            [QueueStepKeys.GenerateProofMaterial] = QueueStepStatus.Pending,
            [QueueStepKeys.SmallVideoRepair] = QueueStepStatus.Completed,
            [QueueStepKeys.VideoTranslate] = QueueStepStatus.Completed,
            [QueueStepKeys.SilenceDetect] = QueueStepStatus.Completed,
            [QueueStepKeys.SilenceRepair] = QueueStepStatus.Completed,
            [QueueStepKeys.MaterialValidate] = QueueStepStatus.Completed,
            [QueueStepKeys.DeleteSourceVideos] = QueueStepStatus.Completed,
            [QueueStepKeys.UploadSeries] = QueueStepStatus.Completed,
        };

    private static void EnsureRecoverableProjectDirectory(
        string projectDir,
        string newTitle,
        string seriesId)
    {
        if (!Directory.Exists(projectDir))
        {
            Directory.CreateDirectory(projectDir);
            return;
        }

        var metadataPath = Path.Combine(projectDir, MetadataFileName);
        if (!File.Exists(metadataPath))
        {
            if (Directory.EnumerateFileSystemEntries(projectDir).Any())
            {
                throw new InvalidOperationException(
                    $"目标目录已存在且不是平台恢复项目：{projectDir}");
            }
            return;
        }

        var metadata = ReadMetadata(metadataPath);
        var recovery = ReadBool(metadata, "tiktokPublishedRecovery");
        var existingTitle = ReadString(metadata, "newTitle");
        var existingSeriesId = ReadString(metadata, "tiktokSeriesId");
        if (!recovery ||
            !string.Equals(existingTitle, newTitle, StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(seriesId) &&
             !string.IsNullOrWhiteSpace(existingSeriesId) &&
             !string.Equals(existingSeriesId, seriesId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"目标目录已被其他项目占用：{projectDir}");
        }
    }

    private static void CopyDownloadedVideos(
        IReadOnlyList<string> sourceVideos,
        string destinationDir,
        Action<string>? log)
    {
        for (var index = 0; index < sourceVideos.Count; index++)
        {
            var source = sourceVideos[index];
            var extension = Path.GetExtension(source);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".mp4";
            var destination = Path.Combine(
                destinationDir,
                $"第{index + 1:D3}集{extension.ToLowerInvariant()}");
            if (File.Exists(destination) &&
                new FileInfo(destination).Length == new FileInfo(source).Length)
            {
                continue;
            }

            File.Copy(source, destination, overwrite: true);
            log?.Invoke(
                $"平台视频写入项目 [{index + 1}/{sourceVideos.Count}]：{Path.GetFileName(destination)}");
        }
    }

    private static void WriteRecoveryMetadata(
        string projectDir,
        string newTitle,
        TikTokPublishedVideoRecoverySource source,
        int downloadedCount)
    {
        var metadataPath = Path.Combine(projectDir, MetadataFileName);
        var metadata = ReadMetadata(metadataPath);
        metadata["displayName"] = newTitle;
        metadata["sourceName"] = newTitle;
        metadata["title"] = newTitle;
        metadata["originalTitle"] = UnknownOriginalTitle;
        metadata["newTitle"] = newTitle;
        metadata["episodeCount"] = downloadedCount;
        metadata["effectiveEpisodeCount"] = downloadedCount;
        metadata["queueEntryDramaType"] = RecoverySourceType;
        metadata["importMode"] = "tiktok_published_recovery";
        metadata["localImported"] = true;
        metadata["localManualImport"] = true;
        metadata["downloadDisabled"] = true;
        metadata["tiktokPublishedRecovery"] = true;
        metadata["tiktokSeriesId"] = source.SeriesId;
        metadata["tiktokDetailUrl"] = source.DetailUrl;
        metadata["tiktokPlatformEpisodeCount"] = source.PlatformEpisodeCount;
        metadata["tiktokDownloadedEpisodeCount"] = downloadedCount;
        metadata["updatedAt"] = DateTimeOffset.Now.ToString("o");
        File.WriteAllText(
            metadataPath,
            metadata.ToJsonString(JsonOptions),
            Encoding.UTF8);

        File.WriteAllText(
            Path.Combine(projectDir, "短剧信息.txt"),
            string.Join(
                Environment.NewLine,
                $"新剧名: {newTitle}",
                $"原剧名: {UnknownOriginalTitle}",
                $"剧名: {newTitle}",
                $"集数: {downloadedCount}",
                "素材来源: TikTok 已发布视频恢复",
                $"TikTok剧集ID: {source.SeriesId}") + Environment.NewLine,
            Encoding.UTF8);
    }

    private static IReadOnlyList<string> EnumerateVideos(string directory) =>
        Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => VideoExtensions.Contains(Path.GetExtension(path)))
            .Where(path => new FileInfo(path).Length > 0)
            .OrderBy(path => NaturalSortKey(Path.GetFileName(path)), StringComparer.Ordinal)
            .ToArray();

    private static string NaturalSortKey(string fileName) =>
        Regex.Replace(
            fileName,
            @"\d+",
            match => match.Value.PadLeft(12, '0'),
            RegexOptions.CultureInvariant);

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

    private static string ReadString(JsonObject metadata, string key)
    {
        if (!metadata.TryGetPropertyValue(key, out var node) || node is null)
            return string.Empty;
        try
        {
            return node.GetValue<string>()?.Trim() ?? string.Empty;
        }
        catch
        {
            return node.ToJsonString().Trim('"').Trim();
        }
    }

    private static bool ReadBool(JsonObject metadata, string key)
    {
        if (!metadata.TryGetPropertyValue(key, out var node) || node is null)
            return false;
        try
        {
            return node.GetValue<bool>();
        }
        catch
        {
            return bool.TryParse(ReadString(metadata, key), out var value) && value;
        }
    }

    private static string SanitizeFileName(string? value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string((value ?? string.Empty)
                .Trim()
                .Select(ch => invalid.Contains(ch) ? '_' : ch)
                .ToArray())
            .Trim()
            .Trim('.');
        sanitized = Regex.Replace(sanitized, @"\s+", " ");
        return string.IsNullOrWhiteSpace(sanitized) ? "已发布剧集" : sanitized;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static DeletedCopyrightProofProjectRecoveryResult Fail(string message) =>
        new(false, message);
}
