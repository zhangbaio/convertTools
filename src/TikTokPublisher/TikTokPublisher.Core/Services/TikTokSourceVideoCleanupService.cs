namespace TikTokPublisher.Core.Services;

/// <summary>对齐 Python <c>source_video_cleanup_service.py</c>：确认上传副本完整后删除源视频。</summary>
public static class TikTokSourceVideoCleanupService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm",
    };

    public static void DeleteSourceVideos(
        string sourceProjectDir,
        string workflowProjectDir,
        string? title,
        string? originalTitle,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(sourceProjectDir);
        var workflow = Path.GetFullPath(workflowProjectDir);
        var sourceVideoPaths = CollectSourceVideoPaths(source);
        if (sourceVideoPaths.Count == 0)
        {
            log?.Invoke("源视频已不存在或无可删文件，跳过本步骤。");
            return;
        }

        var payload = TikTokUploadStagingService.BuildPayload(
            source,
            title,
            originalTitle,
            rebuildStaging: false,
            repairSmallVideos: false,
            log);

        var uploadVideoPaths = payload.UploadPaths.ToList();
        var expectedCount = Math.Max(sourceVideoPaths.Count, payload.SourcePaths.Count);
        if (uploadVideoPaths.Count < sourceVideoPaths.Count)
        {
            ct.ThrowIfCancellationRequested();
            log?.Invoke($"上传副本不足（{uploadVideoPaths.Count}/{sourceVideoPaths.Count}），用现有源视频重建上传副本…");
            payload = TikTokUploadStagingService.BuildPayload(
                source,
                title,
                originalTitle,
                rebuildStaging: true,
                repairSmallVideos: false,
                log);
            uploadVideoPaths = payload.UploadPaths.ToList();
        }

        if (uploadVideoPaths.Count < expectedCount)
        {
            throw new InvalidOperationException(
                $"上传副本数量不足，拒绝删除源视频：短剧总集数 {expectedCount}，" +
                $"源视频 {sourceVideoPaths.Count} 个，上传副本 {uploadVideoPaths.Count} 个");
        }

        var invalidUploadPath = uploadVideoPaths.FirstOrDefault(path =>
            !File.Exists(path) || new FileInfo(path).Length <= 0);
        if (invalidUploadPath is not null)
            throw new InvalidOperationException($"上传副本不完整，拒绝删除源视频：{Path.GetFileName(invalidUploadPath)}");

        log?.Invoke($"已确认上传副本完整，开始删除 {sourceVideoPaths.Count} 个源视频。");
        var deletedCount = 0;
        foreach (var path in sourceVideoPaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                File.Delete(path);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException($"删除源视频失败（文件被占用）：{Path.GetFileName(path)}", ex);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"删除源视频失败：{Path.GetFileName(path)}（{ex.Message}）", ex);
            }

            deletedCount++;
            log?.Invoke($"已删除源视频：{Path.GetFileName(path)}");
        }

        var sourceVideosDir = Path.Combine(source, "videos");
        if (Directory.Exists(sourceVideosDir))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(sourceVideosDir).Any())
                {
                    Directory.Delete(sourceVideosDir);
                    log?.Invoke("源视频目录 videos 已清空并删除。");
                }
            }
            catch
            {
                // ignore rmdir failures
            }
        }

        log?.Invoke($"删除源视频完成：共删除 {deletedCount} 个文件。");
    }

    private static List<string> CollectSourceVideoPaths(string sourceProjectDir)
    {
        var candidates = new List<string>();
        foreach (var root in new[] { sourceProjectDir, Path.Combine(sourceProjectDir, "videos") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root))
            {
                if (!VideoExtensions.Contains(Path.GetExtension(path))) continue;
                candidates.Add(Path.GetFullPath(path));
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var path in candidates.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(path))
                ordered.Add(path);
        }

        return ordered;
    }
}
