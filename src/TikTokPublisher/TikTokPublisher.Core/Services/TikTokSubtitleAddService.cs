using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public static partial class TikTokSubtitleAddService
{
    private sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

    public static async Task AddAsync(string projectDir, string title, string originalTitle,
        ClientSettings settings, Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ElevenLabsApiKey))
            throw new InvalidOperationException("添加字幕需要 ElevenLabs API Key（系统服务 → 视频本地化）。");
        var payload = TikTokUploadStagingService.BuildPayload(
            projectDir, title, originalTitle, rebuildStaging: false, repairSmallVideos: false, log, ct);
        if (payload.UploadPaths.Count == 0) throw new InvalidOperationException("添加字幕失败：未找到上传视频。");

        var state = VideoLocalizationStateStore.Load(projectDir);
        var client = new ElevenLabsDubbingClient(settings);
        for (var i = 0; i < payload.UploadPaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var video = payload.UploadPaths[i];
            var episode = VideoLocalizationStateStore.GetEpisode(state, video);
            if (!episode.DubbedVideoDownloaded || string.IsNullOrWhiteSpace(episode.ElevenLabsDubbingId))
                throw new InvalidOperationException($"第 {i + 1} 集尚未完成 ElevenLabs 视频配音，不能添加字幕。");

            RestoreCleanDubbedVideo(video, episode);

            var srt = !string.IsNullOrWhiteSpace(episode.FinalSrtPath) && File.Exists(episode.FinalSrtPath)
                ? await File.ReadAllTextAsync(episode.FinalSrtPath, ct).ConfigureAwait(false)
                : await client.GetSrtAsync(episode.ElevenLabsDubbingId, ct).ConfigureAwait(false);
            var cues = ParseSrt(srt);
            if (cues.Count == 0) throw new InvalidOperationException($"第 {i + 1} 集 ElevenLabs 没有返回有效字幕。");
            episode.EnglishSrtDownloaded = true;
            VideoLocalizationStateStore.Save(projectDir, state);
            await BurnAsync(video, cues, settings, ct).ConfigureAwait(false);
            episode.SubtitlesBurned = true;
            VideoLocalizationStateStore.Save(projectDir, state);
            log?.Invoke($"第 {i + 1} 集：英文字幕添加完成（{cues.Count} 条）。");
        }
    }

    internal static List<(TimeSpan Start, TimeSpan End, string Text)> ParseSrtForTest(string srt) =>
        ParseSrt(srt).Select(x => (x.Start, x.End, x.Text)).ToList();

    internal static void RestoreCleanDubbedVideo(string video, VideoLocalizationEpisodeState episode)
    {
        if (string.IsNullOrWhiteSpace(episode.CleanDubbedVideoPath) || !File.Exists(episode.CleanDubbedVideoPath))
        {
            if (episode.SubtitlesBurned)
                throw new InvalidOperationException("已添加过字幕，但未找到干净配音底片；为避免重复烧录，请重新执行视频配音步骤。");

            var directory = VideoLocalizationStateStore.GetLocalizationDirectory(video);
            Directory.CreateDirectory(directory);
            episode.CleanDubbedVideoPath = Path.Combine(
                directory,
                $"{Path.GetFileNameWithoutExtension(video)}.dubbed-clean.mp4");
            File.Copy(video, episode.CleanDubbedVideoPath, true);
        }

        File.Copy(episode.CleanDubbedVideoPath, video, true);
        episode.SubtitlesBurned = false;
    }

    private static List<Cue> ParseSrt(string srt)
    {
        var result = new List<Cue>();
        var normalized = srt.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (Match match in SrtBlockRegex().Matches(normalized))
        {
            if (!TryParseTimestamp(match.Groups[1].Value, out var start) ||
                !TryParseTimestamp(match.Groups[2].Value, out var end) || end <= start)
                continue;
            var text = match.Groups[3].Value.Trim();
            if (text.Length > 0) result.Add(new Cue(start, end, text));
        }
        return result;
    }

    private static bool TryParseTimestamp(string value, out TimeSpan result) =>
        TimeSpan.TryParseExact(value.Trim().Replace(',', '.'), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out result);

    private static async Task BurnAsync(string video, IReadOnlyList<Cue> cues, ClientSettings settings, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(video)!;
        var stem = Path.GetFileNameWithoutExtension(video);
        var ass = Path.Combine(dir, $".{stem}.en.ass");
        var output = Path.Combine(dir, $".{stem}.subtitled.mp4");
        try
        {
            await File.WriteAllTextAsync(ass, BuildAss(cues, settings), new UTF8Encoding(false), ct).ConfigureAwait(false);
            var filter = $"ass='{ass.Replace("\\", "/").Replace(":", "\\:").Replace("'", "\\'")}'";
            await FfmpegRunner.RunAsync(MediaBinaryResolver.ResolveFfmpeg(),
                ["-y", "-hide_banner", "-loglevel", "error", "-i", video, "-vf", filter,
                 "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
                 "-c:a", "copy", "-movflags", "+faststart", output], ct).ConfigureAwait(false);
            var before = await FfmpegRunner.ProbeDurationSecondsAsync(MediaBinaryResolver.ResolveFfprobe(), video, ct).ConfigureAwait(false);
            var after = await FfmpegRunner.ProbeDurationSecondsAsync(MediaBinaryResolver.ResolveFfprobe(), output, ct).ConfigureAwait(false);
            if (Math.Abs(before - after) > 1.5) throw new InvalidOperationException("添加字幕后视频时长校验失败。");
            File.Move(output, video, true);
        }
        finally { TryDelete(ass); TryDelete(output); }
    }

    private static string BuildAss(IReadOnlyList<Cue> cues, ClientSettings settings)
    {
        var b = new StringBuilder($"[Script Info]\nScriptType: v4.00+\nPlayResX: 720\nPlayResY: 1280\nWrapStyle: 0\n\n[V4+ Styles]\nFormat: Name,Fontname,Fontsize,PrimaryColour,SecondaryColour,OutlineColour,BackColour,Bold,Italic,Underline,StrikeOut,ScaleX,ScaleY,Spacing,Angle,BorderStyle,Outline,Shadow,Alignment,MarginL,MarginR,MarginV,Encoding\nStyle: Default,{settings.VideoTranslateFont},{Math.Clamp(settings.VideoTranslateFontSize, 12, 120)},&H00FFFFFF,&H000000FF,&H00202020,&H64000000,1,0,0,0,100,100,0,0,1,2,1,2,40,40,{Math.Clamp(settings.VideoTranslateMarginV, 0, 600)},1\n\n[Events]\nFormat: Layer,Start,End,Style,Name,MarginL,MarginR,MarginV,Effect,Text\n");
        foreach (var cue in cues)
            b.AppendLine($"Dialogue: 0,{AssTime(cue.Start)},{AssTime(cue.End)},Default,,0,0,0,,{Escape(cue.Text)}");
        return b.ToString();
    }

    private static string AssTime(TimeSpan value) => value.ToString(@"h\:mm\:ss\.ff", CultureInfo.InvariantCulture);
    private static string Escape(string value) => value.Replace("{", "（").Replace("}", "）").Replace("\r", "").Replace("\n", "\\N");
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    [GeneratedRegex(@"(?ms)(?:^|\n)(?:\d+\n)?\s*(\d{2}:\d{2}:\d{2}[,.]\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}[,.]\d{3})[^\n]*\n(.*?)(?=\n\s*\n|\z)")]
    private static partial Regex SrtBlockRegex();
}
