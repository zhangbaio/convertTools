using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public static class TikTokVideoDubbingService
{
    public static async Task DubAsync(string projectDir, string title, string originalTitle,
        ClientSettings settings, Action<string>? log, CancellationToken ct)
    {
        if (string.Equals(settings.VideoDubbingProvider, "volc", StringComparison.OrdinalIgnoreCase))
        {
            await DubWithVolcAsync(projectDir, title, originalTitle, settings, log, ct).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(settings.ElevenLabsApiKey))
            throw new InvalidOperationException("视频配音需要 ElevenLabs API Key（系统服务 → 视频本地化）。");

        var payload = TikTokUploadStagingService.BuildPayload(
            projectDir, title, originalTitle, rebuildStaging: false, repairSmallVideos: false, log, ct);
        if (payload.UploadPaths.Count == 0) throw new InvalidOperationException("视频配音失败：未找到上传视频。");

        var state = VideoLocalizationStateStore.Load(projectDir);
        var client = new ElevenLabsDubbingClient(settings);
        for (var i = 0; i < payload.UploadPaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = payload.UploadPaths[i];
            var episode = VideoLocalizationStateStore.GetEpisode(state, path);
            if (!episode.SubtitleRemoved)
                throw new InvalidOperationException($"第 {i + 1} 集尚未完成去字幕，不能开始视频配音。");

            if (string.IsNullOrWhiteSpace(episode.ElevenLabsDubbingId))
            {
                log?.Invoke($"第 {i + 1} 集：正在上传并创建 ElevenLabs 配音任务…");
                episode.ElevenLabsDubbingId = await client.CreateAsync(path, ct).ConfigureAwait(false);
                episode.DubbingStatus = "queued";
                VideoLocalizationStateStore.Save(projectDir, state);
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var (status, error) = await client.GetStatusAsync(episode.ElevenLabsDubbingId, ct).ConfigureAwait(false);
                episode.DubbingStatus = status;
                VideoLocalizationStateStore.Save(projectDir, state);
                if (string.Equals(status, "dubbed", StringComparison.OrdinalIgnoreCase)) break;
                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"第 {i + 1} 集 ElevenLabs 配音失败：{error}");
                log?.Invoke($"第 {i + 1} 集：ElevenLabs 状态 {status}…");
                await Task.Delay(TimeSpan.FromSeconds(settings.ElevenLabsPollSeconds), ct).ConfigureAwait(false);
            }

            var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileNameWithoutExtension(path)}.dubbed.mp4");
            try
            {
                await client.DownloadVideoAsync(episode.ElevenLabsDubbingId, temp, ct).ConfigureAwait(false);
                await ValidateVideoAsync(path, temp, ct).ConfigureAwait(false);
                File.Move(temp, path, true);
                episode.DubbedVideoDownloaded = true;
                episode.DubbingProvider = "elevenlabs";
                var rateResult = await TikTokSpeechRateRepairService.ProcessAsync(
                    path, episode.ElevenLabsDubbingId, settings, client, log, ct).ConfigureAwait(false);
                episode.SpeechRateChecked = true;
                episode.SpeechRateRepaired = rateResult.Repaired;
                episode.FastSegmentCount = rateResult.FastSegmentCount;
                episode.FinalSrtPath = rateResult.FinalSrtPath;
                episode.CleanDubbedVideoPath = SaveCleanDubbedVideo(path);
                VideoLocalizationStateStore.Save(projectDir, state);
                log?.Invoke($"第 {i + 1} 集：英文配音完成。");
            }
            finally { TryDelete(temp); }
        }
    }

    private static async Task DubWithVolcAsync(string projectDir, string title, string originalTitle,
        ClientSettings settings, Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.VolcSubtitleAccessKeyId) ||
            string.IsNullOrWhiteSpace(settings.VolcSubtitleSecretAccessKey))
            throw new InvalidOperationException("火山视频翻译需要配置 AccessKeyId 和 SecretKey。");
        if (string.IsNullOrWhiteSpace(settings.VolcSubtitleSpaceName))
            throw new InvalidOperationException("火山视频翻译需要配置 VOD 空间名。");

        var payload = TikTokUploadStagingService.BuildPayload(
            projectDir, title, originalTitle, rebuildStaging: false, repairSmallVideos: false, log, ct);
        if (payload.UploadPaths.Count == 0) throw new InvalidOperationException("视频配音失败：未找到上传视频。");

        var state = VideoLocalizationStateStore.Load(projectDir);
        var client = new VolcengineVodClient(settings);
        var episodes = payload.UploadPaths
            .Select(path => VideoLocalizationStateStore.GetEpisode(state, path))
            .ToArray();
        for (var i = 0; i < episodes.Length; i++)
        {
            if (!episodes[i].SubtitleRemoved)
                throw new InvalidOperationException($"第 {i + 1} 集尚未完成去字幕，不能开始视频翻译。");
        }

        // Upload/submit is a separate batch phase. This allows several episodes to enter the
        // cloud processing queue together instead of waiting for episode N to finish before
        // episode N+1 is even uploaded. Persist the Vid before task submission so cancellation
        // between those two operations remains resumable without another upload.
        using (var stateLock = new SemaphoreSlim(1, 1))
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, payload.UploadPaths.Count),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (i, token) =>
                {
                    var episode = episodes[i];
                    if (!string.IsNullOrWhiteSpace(episode.VolcTranslationTaskId)) return;
                    var path = payload.UploadPaths[i];
                    var itemClient = new VolcengineVodClient(settings);
                    var inputVid = episode.VolcOutputVid;
                    if (string.IsNullOrWhiteSpace(inputVid))
                    {
                        log?.Invoke($"第 {i + 1} 集：上传无字幕视频到火山 VOD…");
                        inputVid = await itemClient.UploadMediaAsync(path, null, token).ConfigureAwait(false);
                        await stateLock.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            episode.VolcOutputVid = inputVid;
                            VideoLocalizationStateStore.Save(projectDir, state);
                        }
                        finally { stateLock.Release(); }
                    }
                }).ConfigureAwait(false);

            // Keep cloud task order deterministic even though the preceding uploads are parallel.
            for (var i = 0; i < payload.UploadPaths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var episode = episodes[i];
                if (!string.IsNullOrWhiteSpace(episode.VolcTranslationTaskId)) continue;
                if (string.IsNullOrWhiteSpace(episode.VolcOutputVid))
                    throw new InvalidOperationException($"第 {i + 1} 集缺少火山 VOD Vid，无法提交视频翻译任务。");

                var videoTitle = Path.GetFileName(payload.UploadPaths[i]);
                await client.SetMediaTitleAsync(episode.VolcOutputVid, videoTitle, ct).ConfigureAwait(false);
                log?.Invoke($"第 {i + 1} 集：提交火山声影智译（字幕翻译＋语音翻译）任务…");
                episode.VolcTranslationTaskId = await client
                    .StartAideoTranslationAsync(episode.VolcOutputVid, ct).ConfigureAwait(false);
                episode.DubbingProvider = "volc";
                episode.DubbingStatus = "Processing";
                VideoLocalizationStateStore.Save(projectDir, state);
            }
        }
        log?.Invoke($"火山视频翻译批量提交完成，共 {episodes.Count(e => !string.IsNullOrWhiteSpace(e.VolcTranslationTaskId))} 集，开始轮询结果。");

        for (var i = 0; i < payload.UploadPaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = payload.UploadPaths[i];
            var episode = VideoLocalizationStateStore.GetEpisode(state, path);
            if (!episode.SubtitleRemoved)
                throw new InvalidOperationException($"第 {i + 1} 集尚未完成去字幕，不能开始视频翻译。");

            if (string.IsNullOrWhiteSpace(episode.VolcTranslationTaskId))
            {
                var inputVid = episode.VolcOutputVid;
                if (string.IsNullOrWhiteSpace(inputVid))
                {
                    log?.Invoke($"第 {i + 1} 集：上传无字幕视频到火山 VOD…");
                    inputVid = await client.UploadMediaAsync(path, null, ct).ConfigureAwait(false);
                }
                log?.Invoke($"第 {i + 1} 集：提交火山声影智译（字幕翻译＋语音翻译）任务…");
                episode.VolcTranslationTaskId = await client.StartAideoTranslationAsync(inputVid, ct).ConfigureAwait(false);
                episode.DubbingProvider = "volc";
                episode.DubbingStatus = "Processing";
                VideoLocalizationStateStore.Save(projectDir, state);
            }

            while (true)
            {
                var result = await client.GetAideoTranslationResultAsync(episode.VolcTranslationTaskId, ct)
                    .ConfigureAwait(false);
                episode.DubbingStatus = result.Status;
                if (!string.IsNullOrWhiteSpace(result.TargetVid)) episode.VolcTranslationTargetVid = result.TargetVid;
                if (!string.IsNullOrWhiteSpace(result.OutputUrl)) episode.VolcTranslationOutputUrl = result.OutputUrl;
                VideoLocalizationStateStore.Save(projectDir, state);
                if (string.Equals(result.Status, "Completed", StringComparison.OrdinalIgnoreCase)) break;
                if (string.Equals(result.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"第 {i + 1} 集火山视频翻译失败：{result.Error}");
                log?.Invoke($"第 {i + 1} 集：火山视频翻译状态 {result.Status}…");
                await Task.Delay(TimeSpan.FromSeconds(settings.VolcSubtitlePollSeconds), ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(episode.VolcTranslationTargetVid))
                throw new InvalidOperationException($"第 {i + 1} 集火山视频翻译已完成，但未返回 TargetVid。");
            var url = !string.IsNullOrWhiteSpace(episode.VolcTranslationOutputUrl)
                ? episode.VolcTranslationOutputUrl
                : await client.GetPlayUrlAsync(episode.VolcTranslationTargetVid, ct).ConfigureAwait(false);
            var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileNameWithoutExtension(path)}.volc-translated.mp4");
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using (var output = File.Create(temp)) await input.CopyToAsync(output, ct).ConfigureAwait(false);
                await ValidateVideoAsync(path, temp, ct).ConfigureAwait(false);
                File.Move(temp, path, true);
                episode.DubbedVideoDownloaded = true;
                episode.SubtitlesBurned = true;
                episode.EnglishSrtDownloaded = true;
                episode.CleanDubbedVideoPath = "";
                VideoLocalizationStateStore.Save(projectDir, state);
                log?.Invoke($"第 {i + 1} 集：火山视频翻译完成（已包含目标语言配音和硬字幕）。");
            }
            finally { TryDelete(temp); }
        }
    }

    private static async Task ValidateVideoAsync(string input, string output, CancellationToken ct)
    {
        if (!File.Exists(output) || new FileInfo(output).Length < 1024)
            throw new InvalidOperationException("配音服务返回的视频为空或不完整。");
        var before = await FfmpegRunner.ProbeDurationSecondsAsync(MediaBinaryResolver.ResolveFfprobe(), input, ct).ConfigureAwait(false);
        var after = await FfmpegRunner.ProbeDurationSecondsAsync(MediaBinaryResolver.ResolveFfprobe(), output, ct).ConfigureAwait(false);
        if (after <= 0 || Math.Abs(before - after) > Math.Max(2, before * 0.03))
            throw new InvalidOperationException($"配音视频时长校验失败：原视频 {before:F1}s，结果 {after:F1}s。");
    }

    private static string SaveCleanDubbedVideo(string videoPath)
    {
        var directory = VideoLocalizationStateStore.GetLocalizationDirectory(videoPath);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(videoPath)}.dubbed-clean.mp4");
        var temp = target + ".tmp";
        try
        {
            File.Copy(videoPath, temp, true);
            File.Move(temp, target, true);
            return target;
        }
        finally { TryDelete(temp); }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
