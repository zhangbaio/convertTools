using System.Text.Json;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public static class TikTokSubtitleRemovalService
{
    public static async Task RemoveAsync(string projectDir, string title, string originalTitle,
        ClientSettings settings, Action<string>? log, CancellationToken ct)
    {
        if (string.Equals(settings.SubtitleRemovalProvider, "baidu", StringComparison.OrdinalIgnoreCase))
        {
            await RemoveWithBaiduAsync(projectDir, title, originalTitle, settings, log, ct).ConfigureAwait(false);
            return;
        }
        if (string.Equals(settings.SubtitleRemovalProvider, "aliyun", StringComparison.OrdinalIgnoreCase))
        {
            await RemoveWithAliyunAsync(projectDir, title, originalTitle, settings, log, ct).ConfigureAwait(false);
            return;
        }
        await RemoveWithVolcAsync(projectDir, title, originalTitle, settings, log, ct).ConfigureAwait(false);
    }

    private static async Task RemoveWithAliyunAsync(string projectDir, string title, string originalTitle,
        ClientSettings settings, Action<string>? log, CancellationToken ct)
    {
        ValidateAliyunSettings(settings);
        var payload = TikTokUploadStagingService.BuildPayload(projectDir, title, originalTitle,
            rebuildStaging: false, repairSmallVideos: false, log, ct);
        if (payload.UploadPaths.Count == 0)
            throw new InvalidOperationException("去字幕失败：未找到上传视频。");

        var (manifest, manifestPath) = LoadAliyunManifest(projectDir, settings.AliyunSubtitleObjectManifestPath);
        var state = VideoLocalizationStateStore.Load(projectDir);
        var client = new AliyunImsSubtitleClient(settings);
        for (var i = 0; i < payload.UploadPaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = payload.UploadPaths[i];
            var episode = VideoLocalizationStateStore.GetEpisode(state, path);
            if (episode.SubtitleRemoved) continue;

            var fileName = Path.GetFileName(path);
            if (!manifest.TryGetValue(fileName, out var inputKey) || string.IsNullOrWhiteSpace(inputKey))
            {
                inputKey = BuildAliyunObjectKey(settings.AliyunSubtitleOssPrefix, "input", fileName);
                log?.Invoke($"第 {i + 1} 集：上传到阿里云 OSS。");
                await client.UploadAsync(path, inputKey, progress =>
                {
                    if (progress >= 1) log?.Invoke($"第 {i + 1} 集：OSS 上传 100%。");
                }, ct).ConfigureAwait(false);
                manifest[fileName] = inputKey;
                SaveMediaManifest(manifestPath, manifest);
            }
            episode.AliyunInputObjectKey = inputKey;
            if (string.IsNullOrWhiteSpace(episode.AliyunOutputObjectKey))
                episode.AliyunOutputObjectKey = BuildAliyunObjectKey(settings.AliyunSubtitleOssPrefix,
                    "output", Path.GetFileNameWithoutExtension(fileName) + ".mp4");

            if (string.IsNullOrWhiteSpace(episode.AliyunTaskId))
            {
                var model = settings.AliyunSubtitleAdvancedModel ? "高级版" : "普通版";
                log?.Invoke($"第 {i + 1} 集：提交阿里云 IMS {model}字幕擦除任务。");
                episode.AliyunTaskId = await client.StartSubtitleRemovalAsync(inputKey,
                    episode.AliyunOutputObjectKey, ct).ConfigureAwait(false);
                VideoLocalizationStateStore.Save(projectDir, state);
            }

            string outputUrl;
            while (true)
            {
                var result = await client.GetSubtitleRemovalAsync(episode.AliyunTaskId, ct).ConfigureAwait(false);
                if (string.Equals(result.Status, "Success", StringComparison.OrdinalIgnoreCase))
                {
                    outputUrl = string.IsNullOrWhiteSpace(result.OutputUrl)
                        ? client.CreateDownloadUri(episode.AliyunOutputObjectKey).ToString()
                        : result.OutputUrl;
                    break;
                }
                if (string.Equals(result.Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(result.Status, "Error", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"第 {i + 1} 集阿里云字幕擦除失败：{result.Error}");
                log?.Invoke($"第 {i + 1} 集：阿里云字幕擦除状态 {result.Status}。");
                await Task.Delay(TimeSpan.FromSeconds(settings.AliyunSubtitlePollSeconds), ct).ConfigureAwait(false);
            }

            var temp = Path.Combine(Path.GetDirectoryName(path)!,
                $".{Path.GetFileNameWithoutExtension(path)}.aliyun-subtitle-erased.mp4");
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                using var response = await http.GetAsync(outputUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(episode.AliyunOutputObjectKey))
                {
                    response.Dispose();
                    outputUrl = client.CreateDownloadUri(episode.AliyunOutputObjectKey).ToString();
                    using var retry = await http.GetAsync(outputUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);
                    retry.EnsureSuccessStatusCode();
                    await CopyResponseToFileAsync(retry, temp, ct).ConfigureAwait(false);
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    await CopyResponseToFileAsync(response, temp, ct).ConfigureAwait(false);
                }
                await ValidateVideoAsync(path, temp, ct).ConfigureAwait(false);
                File.Move(temp, path, true);
                episode.SubtitleRemoved = true;
                VideoLocalizationStateStore.Save(projectDir, state);
                log?.Invoke($"第 {i + 1} 集：阿里云字幕擦除完成。");
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }
    }

    private static async Task RemoveWithVolcAsync(string projectDir, string title, string originalTitle,
        ClientSettings settings, Action<string>? log, CancellationToken ct)
    {
        ValidateSettings(settings);
        var payload = TikTokUploadStagingService.BuildPayload(projectDir, title, originalTitle,
            rebuildStaging: false, repairSmallVideos: false, log, ct);
        if (payload.UploadPaths.Count == 0) throw new InvalidOperationException("去字幕失败：未找到上传视频。");

        var (manifest, manifestPath) = LoadVidManifest(projectDir, settings.VolcSubtitleVidManifestPath);
        var state = VideoLocalizationStateStore.Load(projectDir);
        var client = new VolcengineVodClient(settings);
        var useAideo = string.Equals(settings.VolcSubtitleEraseMode, "aideo", StringComparison.OrdinalIgnoreCase);
        if (useAideo)
        {
            log?.Invoke("Aideo Agent 批量提交阶段开始。");
            for (var i = 0; i < payload.UploadPaths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var path = payload.UploadPaths[i];
                var episode = VideoLocalizationStateStore.GetEpisode(state, path);
                if (episode.SubtitleRemoved || !string.IsNullOrWhiteSpace(episode.VolcAideoTaskId)) continue;
                if (!manifest.TryGetValue(Path.GetFileName(path), out var vid) || string.IsNullOrWhiteSpace(vid))
                {
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
                log?.Invoke($"第 {i + 1} 集：提交火山 Aideo Agent 字幕擦除任务。");
                episode.VolcAideoTaskId = await client.StartAideoSubtitleEraseAsync(vid, ct).ConfigureAwait(false);
                VideoLocalizationStateStore.Save(projectDir, state);
            }
            log?.Invoke("Aideo Agent 批量提交阶段完成，开始轮询并下载产物。");
        }
        for (var i = 0; i < payload.UploadPaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = payload.UploadPaths[i];
            var episode = VideoLocalizationStateStore.GetEpisode(state, path);
            if (episode.SubtitleRemoved) continue;
            if (!manifest.TryGetValue(Path.GetFileName(path), out var vid) || string.IsNullOrWhiteSpace(vid))
            {
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
            string url;
            if (useAideo)
            {
                if (string.IsNullOrWhiteSpace(episode.VolcAideoTaskId))
                {
                    log?.Invoke($"第 {i + 1} 集：提交火山 Aideo Agent 字幕擦除任务。");
                    episode.VolcAideoTaskId = await client.StartAideoSubtitleEraseAsync(vid, ct).ConfigureAwait(false);
                    VideoLocalizationStateStore.Save(projectDir, state);
                }
                while (true)
                {
                    var result = await client.GetAideoTaskResultAsync(episode.VolcAideoTaskId, ct).ConfigureAwait(false);
                    if (string.Equals(result.Status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(result.Status, "Success", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(result.FileName))
                            throw new InvalidOperationException($"第 {i + 1} 集 Aideo Agent 已完成，但未返回产物 FileName。");
                        episode.VolcOutputVid = result.Vid;
                        episode.VolcOutputFileName = result.FileName;
                        VideoLocalizationStateStore.Save(projectDir, state);
                        break;
                    }
                    if (string.Equals(result.Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(result.Status, "Error", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"第 {i + 1} 集 Aideo Agent 字幕擦除失败：{result.Error}");
                    log?.Invoke($"第 {i + 1} 集：Aideo Agent 字幕擦除状态 {result.Status}。");
                    await Task.Delay(TimeSpan.FromSeconds(settings.VolcSubtitlePollSeconds), ct).ConfigureAwait(false);
                }
                url = await client.GetDirectUrlAsync(episode.VolcOutputFileName, ct).ConfigureAwait(false);
            }
            else
            {
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
                        VideoLocalizationStateStore.Save(projectDir, state);
                        break;
                    }
                    if (string.Equals(result.Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(result.Status, "Error", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"第 {i + 1} 集火山字幕擦除失败：{result.Error}");
                    log?.Invoke($"第 {i + 1} 集：火山字幕擦除状态 {result.Status}。");
                    await Task.Delay(TimeSpan.FromSeconds(settings.VolcSubtitlePollSeconds), ct).ConfigureAwait(false);
                }
                url = await client.GetPlayUrlAsync(episode.VolcOutputVid, ct).ConfigureAwait(false);
            }
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

    private static async Task RemoveWithBaiduAsync(string projectDir, string title, string originalTitle,
        ClientSettings settings, Action<string>? log, CancellationToken ct)
    {
        ValidateBaiduSettings(settings);
        var payload = TikTokUploadStagingService.BuildPayload(projectDir, title, originalTitle,
            rebuildStaging: false, repairSmallVideos: false, log, ct);
        if (payload.UploadPaths.Count == 0) throw new InvalidOperationException("去字幕失败：未找到上传视频。");

        var (manifest, manifestPath) = LoadMediaManifest(projectDir, settings.BaiduSubtitleMediaManifestPath);
        var state = VideoLocalizationStateStore.Load(projectDir);
        var client = new BaiduVodSubtitleClient(settings);
        for (var i = 0; i < payload.UploadPaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = payload.UploadPaths[i];
            var episode = VideoLocalizationStateStore.GetEpisode(state, path);
            if (episode.SubtitleRemoved) continue;

            var fileName = Path.GetFileName(path);
            if (!manifest.TryGetValue(fileName, out var mediaId) || string.IsNullOrWhiteSpace(mediaId))
            {
                log?.Invoke($"第 {i + 1} 集：未找到百度 MediaId，开始自动上传百度 VOD。");
                mediaId = await client.UploadMediaAsync(path, progress =>
                {
                    if (progress >= 1) log?.Invoke($"第 {i + 1} 集：百度 VOD 上传 100%。");
                }, ct).ConfigureAwait(false);
                manifest[fileName] = mediaId;
                SaveMediaManifest(manifestPath, manifest);
                episode.BaiduMediaId = mediaId;
                VideoLocalizationStateStore.Save(projectDir, state);
            }
            else episode.BaiduMediaId = mediaId;

            if (string.IsNullOrWhiteSpace(episode.BaiduTaskId))
            {
                log?.Invoke($"第 {i + 1} 集：提交百度 {settings.BaiduSubtitleModelType.ToUpperInvariant()} 字幕擦除任务。");
                episode.BaiduTaskId = await client.StartSubtitleRemovalAsync(mediaId, ct).ConfigureAwait(false);
                VideoLocalizationStateStore.Save(projectDir, state);
            }

            while (true)
            {
                var result = await client.GetSubtitleRemovalAsync(episode.BaiduTaskId, ct).ConfigureAwait(false);
                if (string.Equals(result.Status, "FINISHED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(result.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(result.TargetUrl))
                        throw new InvalidOperationException($"第 {i + 1} 集百度字幕擦除完成，但未返回结果地址。");
                    episode.BaiduOutputUrl = result.TargetUrl;
                    break;
                }
                if (string.Equals(result.Status, "FAILED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(result.Status, "ERROR", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"第 {i + 1} 集百度字幕擦除失败：{result.Error}");
                log?.Invoke($"第 {i + 1} 集：百度字幕擦除状态 {result.Status}。");
                await Task.Delay(TimeSpan.FromSeconds(settings.BaiduSubtitlePollSeconds), ct).ConfigureAwait(false);
            }

            var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileNameWithoutExtension(path)}.baidu-subtitle-erased.mp4");
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                using var response = await http.GetAsync(episode.BaiduOutputUrl,
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var output = File.Create(temp))
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                await ValidateVideoAsync(path, temp, ct).ConfigureAwait(false);
                File.Move(temp, path, true);
                episode.SubtitleRemoved = true;
                VideoLocalizationStateStore.Save(projectDir, state);
                log?.Invoke($"第 {i + 1} 集：百度字幕擦除完成。");
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
    private static (Dictionary<string, string> Manifest, string Path) LoadMediaManifest(string projectDir, string configuredPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(projectDir, ".tiktok", "baidu-vod-media-ids.json")
            : (Path.IsPathRooted(configuredPath) ? configuredPath : Path.Combine(projectDir, configuredPath));
        if (!File.Exists(path)) return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), path);
        var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path),
                         new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                     ?? new Dictionary<string, string>();
        return (new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase), path);
    }
    private static void SaveMediaManifest(string path, Dictionary<string, string> manifest) =>
        SaveVidManifest(path, manifest);

    private static (Dictionary<string, string> Manifest, string Path) LoadAliyunManifest(
        string projectDir, string configuredPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(projectDir, ".tiktok", "aliyun-oss-objects.json")
            : (Path.IsPathRooted(configuredPath) ? configuredPath : Path.Combine(projectDir, configuredPath));
        if (!File.Exists(path)) return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), path);
        var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path),
                         new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                     ?? new Dictionary<string, string>();
        return (new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase), path);
    }

    private static string BuildAliyunObjectKey(string prefix, string category, string fileName) =>
        $"{prefix.Trim('/')}/{category}/{Guid.NewGuid():N}-{fileName}".Replace('\\', '/');

    private static async Task CopyResponseToFileAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(path);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
    }

    private static void ValidateAliyunSettings(ClientSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AliyunSubtitleAccessKeyId) ||
            string.IsNullOrWhiteSpace(settings.AliyunSubtitleAccessKeySecret))
            throw new InvalidOperationException("阿里云去字幕需要配置 AccessKeyId 和 AccessKeySecret。");
        if (string.IsNullOrWhiteSpace(settings.AliyunSubtitleRegion))
            throw new InvalidOperationException("阿里云去字幕需要配置地域，例如 cn-shanghai。");
        if (string.IsNullOrWhiteSpace(settings.AliyunSubtitleOssBucket))
            throw new InvalidOperationException("阿里云去字幕需要配置与 IMS 同地域的 OSS Bucket。");
        _ = AliyunImsSubtitleClient.BuildJobParams(settings.AliyunSubtitleLimitRegion);
    }

    private static void ValidateBaiduSettings(ClientSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BaiduSubtitleAccessKeyId) ||
            string.IsNullOrWhiteSpace(settings.BaiduSubtitleSecretAccessKey))
            throw new InvalidOperationException("百度去字幕需要配置 AccessKeyId 和 SecretAccessKey。");
        if (!Uri.TryCreate(settings.BaiduSubtitleEndpoint, UriKind.Absolute, out _))
            throw new InvalidOperationException("百度 VOD Endpoint 配置无效。");
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
