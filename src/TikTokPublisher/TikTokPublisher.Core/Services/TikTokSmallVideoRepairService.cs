namespace TikTokPublisher.Core.Services;

public static class TikTokSmallVideoRepairService
{
    public static bool NeedsRepair(string sourceProjectDir)
    {
        try
        {
            var uploadPaths = ProjectVideoResolver.ResolveUploadVideos(
                sourceProjectDir,
                allowStagedFallback: true);
            return uploadPaths.Count == 0 || uploadPaths.Any(TikTokSmallVideoPaddingService.NeedsPadding);
        }
        catch
        {
            // A completed queue flag must not suppress repair when the files cannot be
            // resolved on this computer. Let the step run and report the concrete error.
            return true;
        }
    }

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
        var sourceVideos = ProjectVideoResolver.ResolveSourceVideos(
            sourceProjectDir,
            allowStagedFallback: false).ToList();
        var uploadPaths = preview.UploadPaths.Count > 0 ? preview.UploadPaths : preview.SourcePaths;
        var smallSourcePaths = sourceVideos.Where(TikTokSmallVideoPaddingService.NeedsPadding).ToList();
        var smallUploadPaths = uploadPaths.Where(TikTokSmallVideoPaddingService.NeedsPadding).ToList();
        var checkedCount = Math.Max(sourceVideos.Count, uploadPaths.Count);
        if (smallSourcePaths.Count == 0 && smallUploadPaths.Count == 0)
        {
            log?.Invoke($"跳过：未发现小于 5MB 的视频，共检查 {checkedCount} 个待上传视频。");
            return;
        }

        IReadOnlyList<string> repairedUploadPaths;
        IReadOnlyList<string> labelPaths;
        if (sourceVideos.Count > 0)
        {
            var smallCount = Math.Max(smallSourcePaths.Count, smallUploadPaths.Count);
            log?.Invoke($"开始：检测到 {smallCount} 个小于 5MB 的源视频或上传副本，重建上传副本…");
            var rebuilt = TikTokUploadStagingService.BuildPayload(
                sourceProjectDir, title, originalTitle,
                rebuildStaging: true, repairSmallVideos: true, log, ct);

            if (rebuilt.UploadPaths.Count == 0)
                throw new InvalidOperationException("TikTok 小文件修复失败：未生成上传副本。");
            if (rebuilt.UploadPaths.Count != rebuilt.SourcePaths.Count)
                throw new InvalidOperationException("TikTok 上传副本数量异常，无法执行小文件修复。");

            repairedUploadPaths = rebuilt.UploadPaths;
            labelPaths = rebuilt.SourcePaths;
        }
        else
        {
            if (uploadPaths.Count == 0)
            {
                throw new InvalidOperationException(
                    "TikTok 小文件修复失败：检测到小文件，但源视频已不存在，且未找到可修复的上传副本。");
            }

            log?.Invoke(
                $"开始：源视频已不存在，直接修复现有上传副本中的 {smallUploadPaths.Count} 个小于 5MB 的视频…");
            RepairExistingUploadCopies(smallUploadPaths, log, ct);
            repairedUploadPaths = uploadPaths;
            labelPaths = uploadPaths;
        }

        var issues = ValidateRepairedUploadPaths(repairedUploadPaths, labelPaths, ct);
        if (issues.Count > 0)
        {
            foreach (var message in issues)
                log?.Invoke($"失败：{message}");
            throw new InvalidOperationException($"TikTok 小文件修复失败：{string.Join("；", issues.Take(5))}");
        }

        log?.Invoke($"通过：共检查 {repairedUploadPaths.Count} 个上传副本，小文件修复输出已就绪。");
    }

    private static void RepairExistingUploadCopies(
        IReadOnlyList<string> smallUploadPaths,
        Action<string>? log,
        CancellationToken ct)
    {
        var issues = new List<string>();
        foreach (var uploadPath in smallUploadPaths)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(uploadPath);
            if (!TikTokSmallVideoPaddingService.SupportsPadding(uploadPath))
            {
                issues.Add($"{name} 不支持无损补齐，请重新生成上传副本。");
                continue;
            }

            try
            {
                TikTokSmallVideoPaddingService
                    .PadWithoutReencodeAsync(uploadPath, log, ct)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                issues.Add($"{name} 自动补齐失败（{ex.Message}）");
            }
        }

        if (issues.Count > 0)
            throw new InvalidOperationException(string.Join("；", issues.Take(5)));
    }

    private static List<string> ValidateRepairedUploadPaths(
        IReadOnlyList<string> uploadPaths,
        IReadOnlyList<string> labelPaths,
        CancellationToken ct)
    {
        var issues = new List<string>();
        if (uploadPaths.Count == 0)
        {
            issues.Add("修复后没有可上传视频。");
            return issues;
        }

        for (var index = 0; index < uploadPaths.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var uploadPath = uploadPaths[index];
            var labelPath = index < labelPaths.Count ? labelPaths[index] : uploadPath;
            long size;
            try { size = new FileInfo(uploadPath).Length; }
            catch (Exception ex)
            {
                issues.Add($"第{index + 1}集 | {Path.GetFileName(labelPath)} | 读取上传副本大小失败（{ex.Message}）");
                continue;
            }

            if (size < TikTokVideoConstraints.MinSizeBytes)
            {
                issues.Add(
                    $"第{index + 1}集 | {Path.GetFileName(labelPath)} | 自动修复后仍小于 5MB（{TikTokSmallVideoPaddingService.FormatSize(size)}）");
            }
        }

        return issues;
    }
}
