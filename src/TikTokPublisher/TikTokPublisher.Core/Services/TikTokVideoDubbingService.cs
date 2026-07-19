using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public static class TikTokVideoDubbingService
{
    public static async Task DubAsync(string projectDir, string title, string originalTitle,
        ClientSettings settings, Action<string>? log, CancellationToken ct)
    {
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

    private static async Task ValidateVideoAsync(string input, string output, CancellationToken ct)
    {
        if (!File.Exists(output) || new FileInfo(output).Length < 1024)
            throw new InvalidOperationException("ElevenLabs 返回的视频为空或不完整。");
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
