using System.Globalization;

namespace ChannelsPublisher.Clip;

internal static class CommentarySubtitleMask
{
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<SubtitleMaskRegion>>> DetectAsync(
        IReadOnlyList<ClipCandidate> segments, ClipEngineOptions options, string working, Action<string>? log, CancellationToken ct)
    {
        var sources = segments.Select(segment => segment.VideoPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var fallback = Fallback(options.SubtitleBlurHeight);
        var masks = sources.ToDictionary(source => source, _ => fallback, StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows() || sources.Length == 0)
            return masks;

        var folder = Path.Combine(working, "subtitle-ocr");
        Directory.CreateDirectory(folder);
        var samples = segments.SelectMany(segment => new[] { 0.18, 0.49, 0.81 }.Select(fraction => (
            Source: segment.VideoPath, Time: (segment.StartMs + fraction * segment.DurationMs) / 1000d))).ToArray();
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, samples.Length),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (index, token) =>
                {
                    var sample = samples[index];
                    await Ffmpeg.RunCaptureAsync(options.FfmpegPath, ["-hide_banner", "-loglevel", "error",
                        "-threads", "1", "-filter_threads", "1", "-ss", sample.Time.ToString("0.###", CultureInfo.InvariantCulture),
                        "-i", sample.Source, "-an", "-sn", "-frames:v", "1", "-vf",
                        "scale=540:840:force_original_aspect_ratio=decrease:force_divisible_by=2",
                        "-update", "1", Path.Combine(folder, $"frame-{index:D4}.png")], token);
                });

            var observations = await WindowsClipOcr.ReadAsync(folder, ct);
            observations = observations.Where(box => box.Frame >= 0 && box.Frame < samples.Length &&
                box.Height is >= 0.009 and <= 0.075 && box.CenterY is >= 0.52 and <= 0.955 &&
                Math.Abs(box.CenterX - 0.5) <= 0.17).ToArray();
            var common = SelectRegions(observations);
            foreach (var group in observations.GroupBy(box => samples[box.Frame].Source, StringComparer.OrdinalIgnoreCase))
            {
                var regions = SelectRegions(group);
                if (regions.Count > 0)
                    masks[group.Key] = regions;
            }
            var detected = masks.Values.Count(regions => !ReferenceEquals(regions, fallback));
            foreach (var source in sources.Where(source => ReferenceEquals(masks[source], fallback) && common.Count > 0))
                masks[source] = common;
            log?.Invoke($"原字幕定位：{detected}/{sources.Length} 个源视频单独定位，其余{(common.Count > 0 ? "复用项目内的字幕位置" : "使用中下部和底部遮盖")}。");
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            log?.Invoke("原字幕定位未完成，采用中下部和底部的字幕遮盖区域。");
        }
        return masks;
    }

    internal static IReadOnlyList<SubtitleMaskRegion> SelectRegions(IEnumerable<SubtitleObservation> boxes)
    {
        var remaining = boxes.ToList();
        var regions = new List<SubtitleMaskRegion>();
        var requiredFrames = 2;
        while (remaining.Count > 0 && regions.Count < 2)
        {
            var cluster = remaining.Select(anchor =>
                    remaining.Where(box => Math.Abs(box.CenterY - anchor.CenterY) <= 0.030).ToArray())
                .OrderByDescending(group => group.Select(box => box.Frame).Distinct().Count())
                .ThenBy(group => group.Max(box => box.CenterY) - group.Min(box => box.CenterY))
                .First();
            var frameCount = cluster.Select(box => box.Frame).Distinct().Count();
            if (frameCount < requiredFrames)
                break;

            var widest = cluster.Max(box => Math.Abs(box.CenterX - 0.5) + box.Width / 2);
            var halfWidth = Math.Clamp(widest + 0.085, 0.31, 0.465);
            regions.Add(new SubtitleMaskRegion(0.5 - halfWidth,
                Math.Max(0.48, cluster.Min(box => box.Y) - 0.018), 0.5 + halfWidth,
                Math.Min(0.987, cluster.Max(box => box.Y + box.Height) + 0.018)));
            remaining.RemoveAll(box => cluster.Contains(box));
            requiredFrames = Math.Max(3, (frameCount + 2) / 3);
        }
        return regions;
    }

    public static IReadOnlyList<SubtitleMaskRegion> Fallback(double bottomHeight) =>
        [new(0.065, 0.63, 0.935, 0.77), new(0.065, 1 - Math.Clamp(bottomHeight, 0.05, 0.35), 0.935, 0.99)];

    public static string BuildFilter(IReadOnlyList<SubtitleMaskRegion> regions, ClipMediaInfo media)
    {
        var width = media.Width;
        var height = media.Height;
        if (width < 16 || height < 16)
            return "null";

        return string.Join(",", regions.Select(region =>
        {
            // Keep a two-pixel border for delogo's interpolation, including for 4:2:0 inputs.
            var x = Math.Clamp((int)(region.Left * width) & ~1, 2, (width - 8) & ~1);
            var y = Math.Clamp((int)(region.Top * height) & ~1, 2, (height - 8) & ~1);
            var right = Math.Clamp(((int)Math.Ceiling(region.Right * width) + 1) & ~1, x + 4, (width - 2) & ~1);
            var bottom = Math.Clamp(((int)Math.Ceiling(region.Bottom * height) + 1) & ~1, y + 4, (height - 2) & ~1);
            return $"delogo=x={x}:y={y}:w={right - x}:h={bottom - y}";
        }).DefaultIfEmpty("null"));
    }
}

internal sealed record SubtitleObservation(int Frame, double X, double Y, double Width, double Height, string Text = "")
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

internal sealed record SubtitleMaskRegion(double Left, double Top, double Right, double Bottom);
