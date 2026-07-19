using System.Text.Json;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public static class TikTokSubtitleRemovalService
{
    public static async Task RemoveAsync(string projectDir, string title, string originalTitle,
        ClientSettings settings, Action<string>? log, CancellationToken ct)
    {
        ValidateSettings(settings);
        var payload = TikTokUploadStagingService.BuildPayload(projectDir, title, originalTitle,
            rebuildStaging: false, repairSmallVideos: false, log, ct);
        if (payload.UploadPaths.Count == 0) throw new InvalidOperationException("去字幕失败：未找到上传视频。");

        var (manifest, manifestPath) = LoadVidManifest(projectDir, settings.VolcSubtitleVidManifestPath);
        var state = VideoLocalizationStateStore.Load(projectDir);
        var client = new VolcengineVodClient(settings);
        for (var i = 0; i < payload.UploadPaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = payload.UploadPaths[i];
            var episode = VideoLocalizationStateStore.GetEpisode(state, path);
            if (episode.SubtitleRemoved) continue;
            if (!manifest.TryGetValue(Path.GetFileName(path), out var vid) || string.IsNullOrWhiteSpace(vid))
            {
                log?.Invoke($"第 {i + 1} 集：未找到 Vid，开始自动上传火山 VOD。");
                var lastPercent = -1;
                vid = await client.UploadMediaAsync(path, progress =>
                {
                    var percent = (int)Math.Floor(progress * 10) * 10;
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        log?.Invoke($"第 {i + 1} 集：VOD 上传 {Math.Clamp(percent, 0, 100)}%。");
                    }
                }, ct).ConfigureAwait(false);
                manifest[Path.GetFileName(path)] = vid;
                SaveVidManifest(manifestPath, manifest);
                log?.Invoke($"第 {i + 1} 集：VOD 上传完成，Vid 已缓存。");
            }
            if (string.IsNullOrWhiteSpace(episode.VolcRunId))
            {
                log?.Invoke($"第 {i + 1} 集：提交火山精细化字幕擦除任务。");
                episode.VolcRunId = await client.StartSubtitleEraseAsync(vid, ct).ConfigureAwait(false);
                VideoLocalizationStateStore.Save(projectDir, state);
            }
            while (true)
            {
                var result = await client.GetExecutionAsync(episode.VolcRunId, ct).ConfigureAwait(false);
                if (string.Equals(result.Status, "Success", StringComparison.OrdinalIgnoreCase))
                {
                    episode.VolcOutputVid = result.Vid;
                    episode.VolcOutputFileName = result.FileName;
                    break;
                }
                if (string.Equals(result.Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(result.Status, "Error", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"第 {i + 1} 集火山字幕擦除失败：{result.Error}");
                log?.Invoke($"第 {i + 1} 集：火山字幕擦除状态 {result.Status}。");
                await Task.Delay(TimeSpan.FromSeconds(settings.VolcSubtitlePollSeconds), ct).ConfigureAwait(false);
            }
            var url = await client.GetPlayUrlAsync(episode.VolcOutputVid, ct).ConfigureAwait(false);
            var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileNameWithoutExtension(path)}.subtitle-erased.mp4");
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var output = File.Create(temp)) await input.CopyToAsync(output, ct).ConfigureAwait(false);
                await ValidateVideoAsync(path, temp, ct).ConfigureAwait(false);
                File.Move(temp, path, true);
                episode.SubtitleRemoved = true;
                VideoLocalizationStateStore.Save(projectDir, state);
                log?.Invoke($"第 {i + 1} 集：字幕擦除完成。");
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }
    }

    private static (Dictionary<string, string> Manifest, string Path) LoadVidManifest(string projectDir, string configuredPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(projectDir, ".tiktok", "volc-vod-vids.json")
            : (Path.IsPathRooted(configuredPath) ? configuredPath : Path.Combine(projectDir, configuredPath));
        if (!File.Exists(path)) return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), path);
        var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path),
                         new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                     ?? new Dictionary<string, string>();
        return (new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase), path);
    }
    private static void SaveVidManifest(string path, Dictionary<string, string> manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
    private static void ValidateSettings(ClientSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.VolcSubtitleAccessKeyId) || string.IsNullOrWhiteSpace(settings.VolcSubtitleSecretAccessKey))
            throw new InvalidOperationException("去字幕需要配置火山引擎 AccessKeyId 和 SecretAccessKey。");
        if (string.IsNullOrWhiteSpace(settings.VolcSubtitleSpaceName))
            throw new InvalidOperationException("去字幕自动上传需要配置火山 VOD 空间名。");
    }
    private static async Task ValidateVideoAsync(string original, string output, CancellationToken ct)
    {
        if (!File.Exists(output) || new FileInfo(output).Length < 1024) throw new InvalidOperationException("火山返回的擦除视频为空或不完整。");
        var probe = MediaBinaryResolver.ResolveFfprobe();
        var before = await FfmpegRunner.ProbeDurationSecondsAsync(probe, original, ct).ConfigureAwait(false);
        var after = await FfmpegRunner.ProbeDurationSecondsAsync(probe, output, ct).ConfigureAwait(false);
        if (after <= 0 || Math.Abs(before - after) > Math.Max(2, before * 0.03))
            throw new InvalidOperationException($"擦除视频时长校验失败：原视频 {before:F1}s，结果 {after:F1}s。");
    }
}
