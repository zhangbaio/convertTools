using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ChannelsPublisher.Clip;

internal sealed record ClipSourceAnalysis(
    EpisodeSource Source, ClipMediaInfo Media, List<ClipCandidate> Candidates,
    IReadOnlyList<SubtitleSegment> Dialogue, IReadOnlyList<int> ShotCuts, IReadOnlyList<SubtitleSegment>? HardSubtitles = null)
{
    public bool HasDialogue => Dialogue.Count > 0;
}

internal static partial class ClipSubtitles
{
    public static async Task<List<SubtitleSegment>> ReadAsync(
        string video, string projectDir, ClipMediaInfo media, ClipEngineOptions options, CancellationToken ct)
    {
        foreach (var path in CandidateFiles(video, projectDir))
        {
            if (!File.Exists(path))
                continue;

            var segments = Path.GetExtension(path) == ".srt"
                ? Parse(await File.ReadAllTextAsync(path, ct), media.DurationMs)
                : await ConvertAsync(path, media.DurationMs, options, ct);
            if (segments.Count > 0)
                return segments;
        }
        return media.HasSubtitles ? await ConvertAsync(video, media.DurationMs, options, ct) : [];
    }

    public static IEnumerable<string> CandidateFiles(string video, string projectDir)
    {
        foreach (var extension in new[] { ".srt", ".ass", ".vtt" })
            yield return Path.ChangeExtension(video, extension);

        var fileName = Path.GetFileNameWithoutExtension(video);
        var digest = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(video))))[..8].ToLowerInvariant();
        foreach (var folder in new[] { projectDir, Path.GetDirectoryName(video)! }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.ChangeExtension(Path.GetRelativePath(folder, video), ".srt");
            foreach (var root in new[] { Path.Combine(folder, "素材剪辑输出", "字幕"), Path.Combine(folder, "material-clip-output", "subtitles") })
            {
                if (!Path.IsPathRooted(relative) && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    yield return Path.Combine(root, relative);
                yield return Path.Combine(root, "导入视频", $"{fileName}-{digest}.srt");
                yield return Path.Combine(root, "imported", $"{fileName}-{digest}.srt");
            }
        }
    }

    private static async Task<List<SubtitleSegment>> ConvertAsync(
        string input, int durationMs, ClipEngineOptions options, CancellationToken ct)
    {
        var (code, output, _) = await Ffmpeg.RunCaptureAsync(options.FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-i", input, "-map", "0:s:0", "-f", "srt", "pipe:1"], ct);
        return code == 0 ? Parse(output, durationMs) : [];
    }

    public static string CachePath(string outputRoot, string video, ClipEngineOptions options)
    {
        var info = new FileInfo(video);
        var key = string.Join("|", video, info.Length, info.LastWriteTimeUtc.Ticks,
            options.AsrEngine, options.AsrProvider, options.AsrLanguage, options.LocalModel, options.LocalModelDir,
            options.LocalVadPath, options.LocalUseItn, options.HybridMinCharsPerSec);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(outputRoot, "字幕", hash + ".srt");
    }

    public static List<SubtitleSegment> Parse(string srt, int durationMs)
    {
        var segments = new List<SubtitleSegment>();
        foreach (var block in srt.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Timing().Match(block);
            if (!match.Success || !TryMilliseconds(match.Groups[1].Value, out var start) ||
                !TryMilliseconds(match.Groups[2].Value, out var end))
                continue;
            var text = block[(match.Index + match.Length)..].Trim();
            if (end > start && start < durationMs && text.Length > 0)
                segments.Add(new SubtitleSegment(start, Math.Min(end, durationMs), text));
        }
        return segments.OrderBy(segment => segment.StartMs).ToList();
    }

    private static bool TryMilliseconds(string value, out int ms)
    {
        ms = 0;
        if (!TimeSpan.TryParse(value.Replace(',', '.'), CultureInfo.InvariantCulture, out var time) ||
            time.TotalMilliseconds is < 0 or > int.MaxValue)
            return false;
        ms = (int)time.TotalMilliseconds;
        return true;
    }

    [GeneratedRegex(@"(?m)^\s*(\d{1,2}:\d{2}:\d{2}[,.]\d{3})\s*-->\s*(\d{1,2}:\d{2}:\d{2}[,.]\d{3})[^\n]*")]
    private static partial Regex Timing();
}
