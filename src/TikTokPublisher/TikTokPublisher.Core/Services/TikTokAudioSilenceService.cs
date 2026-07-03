using System.Globalization;
using System.Text.RegularExpressions;
using TikTokPublisher.Core.Media;

namespace TikTokPublisher.Core.Services;

public sealed record SilenceSegment(double StartSeconds, double EndSeconds, double DurationSeconds);

public static class TikTokAudioSilenceService
{
    private static readonly Regex SilenceStartRe = new(@"silence_start:\s*(?<start>\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex SilenceEndRe = new(
        @"silence_end:\s*(?<end>\d+(?:\.\d+)?)\s*\|\s*silence_duration:\s*(?<duration>\d+(?:\.\d+)?)",
        RegexOptions.Compiled);

    public static async Task<IReadOnlyList<SilenceSegment>> DetectExcessiveSilenceAsync(
        string videoPath,
        double durationSeconds,
        double maxContinuousSilenceSeconds = TikTokVideoConstraints.DefaultMaxContinuousSilenceSeconds,
        double silenceThresholdDb = TikTokVideoConstraints.DefaultSilenceThresholdDb,
        CancellationToken ct = default)
    {
        var ffmpeg = MediaBinaryResolver.ResolveFfmpeg();
        var (_, _, stderr) = await FfmpegRunner.RunCaptureAsync(ffmpeg, new[]
        {
            "-hide_banner", "-nostats", "-i", videoPath,
            "-vn", "-sn", "-dn",
            "-af", $"silencedetect=n={silenceThresholdDb.ToString(CultureInfo.InvariantCulture)}dB:d={maxContinuousSilenceSeconds.ToString(CultureInfo.InvariantCulture)}",
            "-f", "null", "-",
        }, ct);

        return ParseSilenceDetectOutput(stderr, durationSeconds, maxContinuousSilenceSeconds);
    }

    public static IReadOnlyList<SilenceSegment> ParseSilenceDetectOutput(
        string outputText,
        double mediaDurationSeconds,
        double minimumDurationSeconds = TikTokVideoConstraints.DefaultMaxContinuousSilenceSeconds)
    {
        double? pendingStart = null;
        var segments = new List<SilenceSegment>();
        var minimum = Math.Max(0, minimumDurationSeconds);
        var totalDuration = Math.Max(0, mediaDurationSeconds);

        foreach (var line in (outputText ?? "").Split('\n'))
        {
            var startMatch = SilenceStartRe.Match(line);
            if (startMatch.Success)
            {
                pendingStart = double.Parse(startMatch.Groups["start"].Value, CultureInfo.InvariantCulture);
                continue;
            }

            var endMatch = SilenceEndRe.Match(line);
            if (!endMatch.Success) continue;

            var endSeconds = double.Parse(endMatch.Groups["end"].Value, CultureInfo.InvariantCulture);
            var duration = double.Parse(endMatch.Groups["duration"].Value, CultureInfo.InvariantCulture);
            var startSeconds = pendingStart ?? Math.Max(0, endSeconds - duration);
            pendingStart = null;
            if (duration + 1e-6 < minimum) continue;
            segments.Add(new SilenceSegment(Math.Max(0, startSeconds), Math.Max(0, endSeconds), Math.Max(0, duration)));
        }

        if (pendingStart is not null && totalDuration > pendingStart.Value)
        {
            var trailing = Math.Max(0, totalDuration - pendingStart.Value);
            if (trailing + 1e-6 >= minimum)
                segments.Add(new SilenceSegment(pendingStart.Value, totalDuration, trailing));
        }

        return segments;
    }

    public static string FormatSegment(SilenceSegment segment) =>
        $"{segment.DurationSeconds:F1} 秒（{FormatTimestamp(segment.StartSeconds)} - {FormatTimestamp(segment.EndSeconds)}）";

    private static string FormatTimestamp(double seconds)
    {
        var value = Math.Max(0, seconds);
        var minutes = (int)(value / 60);
        var rest = value - minutes * 60;
        return $"{minutes:00}:{rest:000.0}";
    }
}
