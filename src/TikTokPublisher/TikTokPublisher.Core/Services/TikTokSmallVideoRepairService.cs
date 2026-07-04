namespace TikTokPublisher.Core.Services;

public static class TikTokSmallVideoRepairService
{
    public static void Repair(
        string sourceProjectDir,
        string title,
        string originalTitle,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var preview = TikTokUploadStagingService.BuildPayload(
            sourceProjectDir, title, originalTitle,
            rebuildStaging: false, repairSmallVideos: false, log, ct);
        var smallCount = preview.SourcePaths.Count(TikTokSmallVideoPaddingService.NeedsPadding);
        if (smallCount == 0)
        {
            log?.Invoke($"跳过：未发现小于 5MB 的视频，共检查 {preview.SourcePaths.Count} 个源视频。");
            return;
        }

        log?.Invoke($"开始：检测到 {smallCount} 个小于 5MB 的视频，重建上传副本…");
        var rebuilt = TikTokUploadStagingService.BuildPayload(
            sourceProjectDir, title, originalTitle,
            rebuildStaging: true, repairSmallVideos: true, log, ct);

        if (rebuilt.UploadPaths.Count != rebuilt.SourcePaths.Count)
            throw new InvalidOperationException("TikTok 上传副本数量异常，无法执行小文件修复");

        var issues = new List<string>();
        for (var index = 0; index < rebuilt.UploadPaths.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var uploadPath = rebuilt.UploadPaths[index];
            var sourcePath = rebuilt.SourcePaths[index];
            long size;
            try { size = new FileInfo(uploadPath).Length; }
            catch (Exception ex)
            {
                issues.Add($"第{index + 1}集 | {Path.GetFileName(sourcePath)} | 读取上传副本大小失败（{ex.Message}）");
                continue;
            }
            if (size < TikTokVideoConstraints.MinSizeBytes)
                issues.Add($"第{index + 1}集 | {Path.GetFileName(sourcePath)} | 自动修复后仍小于 5MB（{TikTokSmallVideoPaddingService.FormatSize(size)}）");
        }

        if (issues.Count > 0)
        {
            foreach (var message in issues)
                log?.Invoke($"失败：{message}");
            throw new InvalidOperationException($"TikTok 小文件修复失败：{string.Join("；", issues.Take(5))}");
        }

        log?.Invoke($"通过：共检查 {rebuilt.UploadPaths.Count} 个上传副本，小文件修复输出已就绪。");
    }
}
