using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

internal static partial class TikTokSpeechRateRepairService
{
    internal sealed record Cue(TimeSpan Start, TimeSpan End, string Text);
    internal sealed record RepairInterval(double Start, double End, double Factor);
    internal sealed record RepairResult(bool Repaired, int FastSegmentCount, string FinalSrtPath);

    public static async Task<RepairResult> ProcessAsync(string videoPath, string dubbingId,
        ClientSettings settings, ElevenLabsDubbingClient client, Action<string>? log, CancellationToken ct)
    {
        var srt = await client.GetSrtAsync(dubbingId, ct).ConfigureAwait(false);
        var cues = ParseSrt(srt);
        var intervals = BuildIntervals(cues, settings.ElevenLabsSpeechRateTarget,
            settings.ElevenLabsSpeechRateThreshold, settings.ElevenLabsSpeechRateMaxStretch);
        if (settings.ElevenLabsSpeechRateMode != "repair" || intervals.Count == 0)
        {
            log?.Invoke(settings.ElevenLabsSpeechRateMode == "repair"
                ? "英文配音语速检查通过，无需修复。"
                : $"英文配音保持原始模式；检测到 {intervals.Count} 个可能过快片段，未执行变速。");
            return new RepairResult(false, intervals.Count, "");
        }

        var duration = await FfmpegRunner.ProbeDurationSecondsAsync(MediaBinaryResolver.ResolveFfprobe(), videoPath, ct)
            .ConfigureAwait(false);
        intervals = NormalizeIntervals(intervals, duration);
        if (intervals.Count == 0) return new RepairResult(false, 0, "");

        var output = Path.Combine(Path.GetDirectoryName(videoPath)!, $".{Path.GetFileNameWithoutExtension(videoPath)}.rate-fixed.mp4");
        try
        {
            var filter = BuildFilter(intervals, duration);
            await FfmpegRunner.RunAsync(MediaBinaryResolver.ResolveFfmpeg(),
                ["-y", "-hide_banner", "-loglevel", "error", "-i", videoPath,
                 "-filter_complex", filter, "-map", $"[vout]", "-map", $"[aout]",
                 "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
                 "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", output], ct).ConfigureAwait(false);
            if (!File.Exists(output) || new FileInfo(output).Length < 1024)
                throw new InvalidOperationException("语速修复未生成有效视频。");
            File.Move(output, videoPath, true);

            var localizationDir = VideoLocalizationStateStore.GetLocalizationDirectory(videoPath);
            Directory.CreateDirectory(localizationDir);
            var srtPath = Path.Combine(localizationDir, $"{Path.GetFileNameWithoutExtension(videoPath)}.en.retimed.srt");
            await File.WriteAllTextAsync(srtPath, BuildAdjustedSrt(cues, intervals), new UTF8Encoding(false), ct).ConfigureAwait(false);
            log?.Invoke($"英文配音语速修复完成：调整 {intervals.Count} 个片段，字幕时间轴已同步。");
            return new RepairResult(true, intervals.Count, srtPath);
        }
        finally { TryDelete(output); }
    }

    internal static List<Cue> ParseSrt(string srt)
    {
        var result = new List<Cue>();
        var normalized = srt.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (Match match in SrtBlockRegex().Matches(normalized))
        {
            if (!TryTimestamp(match.Groups[1].Value, out var start) || !TryTimestamp(match.Groups[2].Value, out var end) || end <= start) continue;
            var text = match.Groups[3].Value.Trim();
            if (text.Length > 0) result.Add(new Cue(start, end, text));
        }
        return result;
    }

    internal static List<RepairInterval> BuildIntervals(IReadOnlyList<Cue> cues, double target, double threshold, double maxStretch)
    {
        var raw = new List<RepairInterval>();
        foreach (var cue in cues)
        {
            var seconds = (cue.End - cue.Start).TotalSeconds;
            if (seconds < 0.4) continue;
            var words = EnglishWordRegex().Matches(cue.Text).Count;
            var rate = words / seconds;
            if (rate <= threshold) continue;
            var factor = Math.Clamp(rate / target, 1.0, maxStretch);
            if (factor > 1.01) raw.Add(new RepairInterval(cue.Start.TotalSeconds, cue.End.TotalSeconds, factor));
        }
        return raw;
    }

    private static List<RepairInterval> NormalizeIntervals(List<RepairInterval> raw, double duration)
    {
        var result = new List<RepairInterval>();
        foreach (var item in raw.OrderBy(x => x.Start))
        {
            var current = item with { Start = Math.Clamp(item.Start, 0, duration), End = Math.Clamp(item.End, 0, duration) };
            if (current.End - current.Start < 0.1) continue;
            if (result.Count > 0 && current.Start < result[^1].End)
            {
                var previous = result[^1];
                result[^1] = new RepairInterval(previous.Start, Math.Max(previous.End, current.End), Math.Max(previous.Factor, current.Factor));
            }
            else result.Add(current);
        }
        return result;
    }

    private static string BuildFilter(IReadOnlyList<RepairInterval> intervals, double duration)
    {
        var parts = new List<(double Start, double End, double Factor)>();
        var cursor = 0d;
        foreach (var interval in intervals)
        {
            if (interval.Start > cursor) parts.Add((cursor, interval.Start, 1));
            parts.Add((interval.Start, interval.End, interval.Factor));
            cursor = interval.End;
        }
        if (cursor < duration) parts.Add((cursor, duration, 1));

        var filter = new StringBuilder();
        for (var i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            var start = p.Start.ToString("0.######", CultureInfo.InvariantCulture);
            var end = p.End.ToString("0.######", CultureInfo.InvariantCulture);
            var factor = p.Factor.ToString("0.######", CultureInfo.InvariantCulture);
            var tempo = (1 / p.Factor).ToString("0.######", CultureInfo.InvariantCulture);
            filter.Append($"[0:v]trim=start={start}:end={end},setpts={factor}*(PTS-STARTPTS)[v{i}];");
            filter.Append($"[0:a]atrim=start={start}:end={end},asetpts=PTS-STARTPTS,atempo={tempo}[a{i}];");
        }
        for (var i = 0; i < parts.Count; i++) filter.Append($"[v{i}][a{i}]");
        filter.Append($"concat=n={parts.Count}:v=1:a=1[vout][aout]");
        return filter.ToString();
    }

    private static string BuildAdjustedSrt(IReadOnlyList<Cue> cues, IReadOnlyList<RepairInterval> intervals)
    {
        var output = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            output.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture));
            output.Append(FormatSrt(Map(cue.Start.TotalSeconds, intervals))).Append(" --> ")
                .AppendLine(FormatSrt(Map(cue.End.TotalSeconds, intervals)));
            output.AppendLine(cue.Text).AppendLine();
        }
        return output.ToString();
    }

    internal static double Map(double time, IReadOnlyList<RepairInterval> intervals)
    {
        var mapped = time;
        foreach (var interval in intervals)
        {
            if (time >= interval.End) mapped += (interval.End - interval.Start) * (interval.Factor - 1);
            else if (time > interval.Start)
            {
                mapped += (time - interval.Start) * (interval.Factor - 1);
                break;
            }
            else break;
        }
        return mapped;
    }

    private static bool TryTimestamp(string value, out TimeSpan result) =>
        TimeSpan.TryParseExact(value.Trim().Replace(',', '.'), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out result);
    private static string FormatSrt(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    [GeneratedRegex(@"[A-Za-z]+(?:'[A-Za-z]+)?")]
    private static partial Regex EnglishWordRegex();
    [GeneratedRegex(@"(?ms)(?:^|\n)(?:\d+\n)?\s*(\d{2}:\d{2}:\d{2}[,.]\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}[,.]\d{3})[^\n]*\n(.*?)(?=\n\s*\n|\z)")]
    private static partial Regex SrtBlockRegex();
}
