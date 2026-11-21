using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChannelsPublisher.Clip;

internal static class ClipHardSubtitles
{
    public static async Task<IReadOnlyList<SubtitleSegment>> ReadAsync(ClipSourceAnalysis source, string root,
        ClipEngineOptions options, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("本地硬字幕 OCR 需要 Windows OCR。");

        var duration = source.Media.DurationMs;
        var step = Math.Max(500, (int)Math.Ceiling(duration / 18000d) * 50);
        var video = new FileInfo(source.Source.VideoPath);
        var key = $"win-ocr-v2|{video.FullName}|{video.Length}|{video.LastWriteTimeUtc.Ticks}|{step}|{JsonSerializer.Serialize(source.Dialogue)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        var cache = Path.Combine(root, "分析", $"硬字幕-{hash}.json");
        if (File.Exists(cache))
        {
            try
            {
                var cues = JsonSerializer.Deserialize<SubtitleSegment[]>(await File.ReadAllTextAsync(cache, ct));
                if (cues is not null && cues.All(cue => cue.StartMs >= 0 && cue.EndMs > cue.StartMs && cue.EndMs <= duration))
                    return cues;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }

        var folder = Path.Combine(Path.GetTempPath(), "clip-hard-subtitles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await Ffmpeg.RunAsync(options.FfmpegPath, ["-hide_banner", "-loglevel", "error", "-threads", "1", "-i", video.FullName,
                "-an", "-sn", "-filter_threads", "1", "-vf", $"fps=1000/{step}:start_time=0,scale=640:960:force_original_aspect_ratio=decrease:force_divisible_by=2",
                "-q:v", "3", "-threads", "1", "-start_number", "0", Path.Combine(folder, "frame-%04d.jpg")], ct);
            var observations = await WindowsClipOcr.ReadAsync(folder, ct);
            var result = BuildTimeline(observations, step, duration, source.Dialogue);
            var pending = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(result), ct);
                File.Move(pending, cache, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            finally
            {
                try { File.Delete(pending); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return result;
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static List<SubtitleSegment> BuildTimeline(IReadOnlyList<SubtitleObservation> observations, int step, int duration,
        IReadOnlyList<SubtitleSegment>? primary = null)
    {
        var plausible = observations.Where(row => row.Frame >= 0 && row.Frame * (long)step < duration &&
            row.Height is >= 0.009 and <= 0.095 && row.CenterY is >= 0.48 and <= 0.97 &&
            Math.Abs(row.CenterX - 0.5) <= 0.23 && Normalize(row.Text).Length >= 2).ToArray();
        var persistent = plausible.GroupBy(row => Normalize(row.Text)).Where(group =>
            group.Select(row => row.Frame).Distinct().Count() >= Math.Max(12, (duration / step) / 5))
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var transient = plausible.Where(row => !persistent.Contains(Normalize(row.Text))).ToArray();
        var duringDialogue = primary is { Count: > 0 } ? transient.Where(row =>
            primary.Any(cue => row.Frame * step >= cue.StartMs && row.Frame * step < cue.EndMs)) : transient;
        var band = duringDialogue.GroupBy(row => (int)(row.CenterY * 40))
            .OrderByDescending(group => group.Select(row => row.Frame).Distinct().Count()).FirstOrDefault();
        if (band is not null && band.Select(row => row.Frame).Distinct().Count() >= 8)
        {
            var center = band.Average(row => row.CenterY);
            transient = transient.Where(row => Math.Abs(row.CenterY - center) <= 0.022).ToArray();
        }
        var frames = transient
            .GroupBy(row => row.Frame).OrderBy(group => group.Key).Select(group =>
            {
                var anchor = group.OrderByDescending(row => Normalize(row.Text).Length).First();
                var text = string.Join(" ", group.Where(row => Math.Abs(row.CenterY - anchor.CenterY) <= 0.075)
                    .OrderBy(row => row.CenterY).Select(row => row.Text.Trim()).Distinct());
                return (Frame: group.Key, Text: text, Key: Normalize(text));
            }).ToArray();

        var result = new List<SubtitleSegment>();
        for (var start = 0; start < frames.Length;)
        {
            var end = start;
            while (end + 1 < frames.Length && frames[end + 1].Frame - frames[end].Frame <= 2 && frames[end + 1].Key == frames[start].Key)
                end++;
            var startMs = frames[start].Frame * step;
            var endMs = Math.Min(duration, frames[end].Frame * step + step / 2);
            if (end > start && endMs - startMs <= 8000)
                result.Add(new SubtitleSegment(startMs, endMs, frames[start].Text));
            start = end + 1;
        }
        return result;
    }

    internal static List<SubtitleSegment> MissingDialogue(IReadOnlyList<SubtitleSegment> primary, IReadOnlyList<SubtitleSegment> hard) =>
        hard.Where(cue => !primary.Any(line => Overlap(cue, line) > 0 ||
            (Math.Abs(line.StartMs - cue.StartMs) <= 1500 && Normalize(line.Text).Contains(Normalize(cue.Text), StringComparison.Ordinal))))
            .ToList();

    private static int Overlap(SubtitleSegment a, SubtitleSegment b) => Math.Min(a.EndMs, b.EndMs) - Math.Max(a.StartMs, b.StartMs);

    private static string Normalize(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
