using System.Text.Json;
using TikTokPublisher.Core.Services;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: SubtitleRemovalBatch <project-dir> [project-dir ...]");
    return 2;
}

var settings = ClientSettingsStore.Load();
settings.SubtitleRemovalProvider = "volc";
settings.VolcSubtitleEraseMode = "aideo";
if (string.IsNullOrWhiteSpace(settings.VolcSubtitleSpaceName))
    throw new InvalidOperationException("未配置火山 VOD 空间名，请先在系统设置中完成配置。");
Console.WriteLine($"火山配置：AK={(string.IsNullOrWhiteSpace(settings.VolcSubtitleAccessKeyId) ? "缺失" : "已配置")}，SK={(string.IsNullOrWhiteSpace(settings.VolcSubtitleSecretAccessKey) ? "缺失" : "已配置")}，空间名={(string.IsNullOrWhiteSpace(settings.VolcSubtitleSpaceName) ? "缺失" : settings.VolcSubtitleSpaceName)}，播放域名={(string.IsNullOrWhiteSpace(settings.VolcSubtitlePlaybackDomain) ? "缺失" : settings.VolcSubtitlePlaybackDomain)}");

foreach (var projectDir in args)
{
    var fullPath = Path.GetFullPath(projectDir);
    if (!Directory.Exists(fullPath))
        throw new DirectoryNotFoundException(fullPath);

    var metadataPath = Path.Combine(fullPath, "shortdrama-project.json");
    var title = Path.GetFileName(fullPath);
    if (File.Exists(metadataPath))
    {
        using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
        if (document.RootElement.TryGetProperty("title", out var value) && !string.IsNullOrWhiteSpace(value.GetString()))
            title = value.GetString()!;
    }

    Console.WriteLine($"[{DateTimeOffset.Now:O}] 开始：{fullPath}");
    var stagingPath = Path.Combine(TikTokUploadStateStore.ResolveWorkflowProjectDir(fullPath),
        TikTokUploadStagingService.StagingDirName);
    var hasStagedVideos = Directory.Exists(stagingPath) && Directory.EnumerateFiles(stagingPath, "*.mp4").Any();
    var payload = TikTokUploadStagingService.BuildPayload(fullPath, title, title,
        rebuildStaging: !hasStagedVideos, repairSmallVideos: false, Console.WriteLine);
    Console.WriteLine($"[{DateTimeOffset.Now:O}] 工作副本：{payload.UploadPaths.Count} 个");
    await TikTokSubtitleRemovalService.RemoveAsync(fullPath, title, title, settings, Console.WriteLine, CancellationToken.None);
    Console.WriteLine($"[{DateTimeOffset.Now:O}] 完成：{fullPath}");
}

return 0;
