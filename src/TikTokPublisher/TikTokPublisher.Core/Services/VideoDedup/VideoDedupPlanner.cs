using System.Globalization;
using System.Text;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Services.VideoDedup;

internal sealed record VideoDedupProbe(
    double DurationSeconds,
    int Width,
    int Height,
    bool HasAudio,
    int AudioBitrateKbps = 0,
    double AverageFramesPerSecond = 0,
    VideoDedupSourceFormat? Format = null,
    double VideoDuration = 0,
    double AudioDuration = 0,
    long FrameCount = 0,
    long VideoBitrateBps = 0);

internal sealed record VideoDedupClip(string Path, VideoDedupProbe Probe);

internal readonly record struct VideoDedupSlice(int ClipIndex, double StartSeconds, double EndSeconds)
{
    public double Length => Math.Max(0, EndSeconds - StartSeconds);
}

internal static partial class VideoDedupPlanner
{
    public static IReadOnlyList<IReadOnlyList<VideoDedupSlice>> Plan(
        IReadOnlyList<double> rawDurations,
        VideoDedupPreset preset,
        IReadOnlyList<string>? clipPaths = null)
    {
        var count = rawDurations.Count;
        if (count == 0)
            return [];

        if (!preset.HasDurationLimit)
        {
            var single = new List<IReadOnlyList<VideoDedupSlice>>(count);
            for (var i = 0; i < count; i++)
                single.Add(TrimWholeClip(i, Math.Max(0, rawDurations[i]), preset));
            return single;
        }

        return ArrangeAverage(rawDurations, preset, clipPaths);
    }

    /// <summary>
    /// 用排好的切片估算成片秒数。切片已经裁过开头和结尾，这里只再算变速、抽帧，
    /// 以及不同片段接缝上的转场重叠。
    /// </summary>
    internal static double PredictedOutputSeconds(
        IReadOnlyList<VideoDedupSlice> slices,
        VideoDedupPreset preset,
        IReadOnlyList<string>? clipPaths = null)
    {
        if (slices.Count == 0)
            return 0;

        var elapsed = OutputDuration(slices[0].Length, preset);
        if (!CanTransition(preset, slices))
        {
            for (var index = 1; index < slices.Count; index++)
                elapsed += OutputDuration(slices[index].Length, preset);
            return elapsed;
        }

        var seed = 0;
        if (clipPaths is { Count: > 0 })
            seed = Math.Abs(clipPaths[slices[0].ClipIndex].GetHashCode(StringComparison.Ordinal));
        for (var index = 1; index < slices.Count; index++)
        {
            var overlap = clipPaths is { Count: > 0 }
                ? TransitionSeconds(Hash(
                    clipPaths[slices[index - 1].ClipIndex],
                    clipPaths[slices[index].ClipIndex],
                    seed + index))
                : TransitionSeconds(index);
            elapsed += OutputDuration(slices[index].Length, preset) - overlap;
        }

        return elapsed;
    }

    internal static double RoundDuration(double seconds) =>
        Math.Round(seconds, 1, MidpointRounding.AwayFromZero);

    internal const string DurationAdjustmentHint = "请调整单集时长，或改原片后再重跑。";

    internal static string FormatDurationStop(string mismatch) =>
        DurationAdjustmentHint + Environment.NewLine + mismatch;

    /// <summary>每一集按 0.1 秒取整后都落在最短和最长之内时返回 null。</summary>
    internal static string? DescribeDurationMismatch(
        IReadOnlyList<IReadOnlyList<VideoDedupSlice>> plan,
        VideoDedupPreset preset,
        IReadOnlyList<string>? clipPaths = null)
    {
        if (!preset.HasDurationLimit || plan.Count == 0)
            return null;

        var minimum = preset.MinimumDurationSeconds;
        var maximum = preset.MaximumDurationSeconds;
        var problems = new List<string>();
        for (var index = 0; index < plan.Count; index++)
        {
            var seconds = RoundDuration(PredictedOutputSeconds(plan[index], preset, clipPaths));
            var episode = (index + 1).ToString(CultureInfo.InvariantCulture);
            if (seconds < minimum)
                problems.Add($"第 {episode} 集预计 {Seconds(seconds)} 秒，短于最小 {Seconds(minimum)} 秒");
            else if (seconds > maximum)
                problems.Add($"第 {episode} 集预计 {Seconds(seconds)} 秒，超过最大 {Seconds(maximum)} 秒");
        }

        if (problems.Count == 0)
            return null;

        var listed = problems.Count <= 12
            ? string.Join("；", problems)
            : string.Join("；", problems.Take(12)) + $" 等 {problems.Count.ToString(CultureInfo.InvariantCulture)} 集";
        return $"视频去重已停止，不会开始编码。按 {Seconds(minimum)}–{Seconds(maximum)} 秒平均分集，{listed}。";
    }

    internal static string DescribeDurationCheckPassed(
        int sourceCount,
        IReadOnlyList<IReadOnlyList<VideoDedupSlice>> plan,
        VideoDedupPreset preset,
        IReadOnlyList<string>? clipPaths = null)
    {
        var durations = plan
            .Select(group => RoundDuration(PredictedOutputSeconds(group, preset, clipPaths)))
            .ToList();
        var low = durations.Min();
        var high = durations.Max();
        var span = Math.Abs(low - high) < 0.001
            ? $"每集 {Seconds(low)} 秒"
            : $"每集 {Seconds(low)}–{Seconds(high)} 秒";
        var minutes = (durations.Sum() / 60d).ToString("0.#", CultureInfo.InvariantCulture);
        return $"分集计算通过：{sourceCount.ToString(CultureInfo.InvariantCulture)} 个原片共 {minutes} 分钟，平均分成 {plan.Count.ToString(CultureInfo.InvariantCulture)} 集，{span}，在 {Seconds(preset.MinimumDurationSeconds)}–{Seconds(preset.MaximumDurationSeconds)} 秒内。";
    }

    private static string Seconds(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static List<VideoDedupSlice> TrimWholeClip(int index, double duration, VideoDedupPreset preset)
    {
        var start = Math.Clamp(preset.TrimStartSeconds, 0, duration);
        var end = Math.Max(0, duration - Math.Max(0, preset.TrimEndSeconds));
        if (end < start + 0.2)
            end = Math.Min(duration, start + 0.2);
        return [new VideoDedupSlice(index, start, end)];
    }

    private static List<List<VideoDedupSlice>> ArrangeAverage(
        IReadOnlyList<double> durations,
        VideoDedupPreset preset,
        IReadOnlyList<string>? clipPaths)
    {
        var total = 0d;
        foreach (var duration in durations)
            total += Math.Max(0, duration);
        if (total <= 0)
            return [];

        var minimum = preset.MinimumDurationSeconds;
        var maximum = preset.MaximumDurationSeconds;
        var trim = Math.Max(0, preset.TrimStartSeconds) + Math.Max(0, preset.TrimEndSeconds);
        var scale = OutputScale(preset);
        var count = ChooseEpisodeCount(total, minimum, maximum, trim, scale);
        var cuts = new double[count + 1];
        for (var index = 1; index < count; index++)
            cuts[index] = total * index / count;
        cuts[count] = total;

        var groups = Realize(durations, cuts, preset);
        if (count < 2 || !CanTransition(preset, groups.SelectMany(group => group).ToArray()))
            return groups;

        var minimumGap = trim + 0.25;
        for (var pass = 0; pass < 8; pass++)
        {
            var predicted = Measure(groups, preset, clipPaths);
            if (AllInside(predicted, minimum, maximum))
                return groups;

            var moved = false;
            for (var index = 0; index < predicted.Length; index++)
            {
                if (predicted[index] >= minimum && predicted[index] <= maximum)
                    continue;

                var neighbor = index + 1 < predicted.Length ? index + 1 : index - 1;
                if (neighbor < 0)
                    continue;

                var grow = predicted[index] < minimum;
                var raw = grow
                    ? Math.Min(minimum - predicted[index], Math.Max(0, predicted[neighbor] - minimum)) / scale
                    : Math.Min(predicted[index] - maximum, Math.Max(0, maximum - predicted[neighbor])) / scale;
                if (raw < 0.02)
                    continue;

                var boundary = Math.Max(index, neighbor);
                var direction = neighbor > index ? 1d : -1d;
                if (!grow)
                    direction = -direction;

                var saved = cuts[boundary];
                var accepted = false;
                var step = raw;
                for (var attempt = 0; attempt < 2 && step >= 0.02; attempt++)
                {
                    var next = saved + direction * step;
                    if (next < cuts[boundary - 1] + minimumGap || next > cuts[boundary + 1] - minimumGap)
                    {
                        step /= 2;
                        continue;
                    }

                    cuts[boundary] = next;
                    var candidate = Realize(durations, cuts, preset);
                    var after = Measure(candidate, preset, clipPaths);
                    var targetImproved = RangeDistance(after[index], minimum, maximum) <
                                         RangeDistance(predicted[index], minimum, maximum) - 0.001;
                    var neighborOk = RangeDistance(after[neighbor], minimum, maximum) <=
                                     RangeDistance(predicted[neighbor], minimum, maximum) + 0.001;
                    if (targetImproved && neighborOk)
                    {
                        groups = candidate;
                        accepted = true;
                        break;
                    }

                    cuts[boundary] = saved;
                    step /= 2;
                }

                if (!accepted)
                    continue;

                moved = true;
                break;
            }

            if (!moved)
                return groups;
        }

        return groups;
    }

    private static int ChooseEpisodeCount(double total, double minimum, double maximum, double trim, double scale)
    {
        var midpoint = (minimum + maximum) / 2d;
        var bestFit = 0;
        var bestFitDistance = double.MaxValue;
        var nearest = 1;
        var nearestDistance = double.MaxValue;
        var limit = Math.Max(1, (int)Math.Floor(total / 0.2));
        for (var count = 1; count <= limit; count++)
        {
            var raw = total / count;
            if (raw <= trim + 0.05)
                break;

            var output = RoundDuration(Math.Max(0.2, raw - trim) * scale);
            var outside = RangeDistance(output, minimum, maximum);
            if (outside == 0)
            {
                var distance = Math.Abs(output - midpoint);
                if (bestFit == 0 || distance < bestFitDistance - 0.001)
                {
                    bestFit = count;
                    bestFitDistance = distance;
                }
            }
            else if (bestFit == 0 && outside < nearestDistance - 0.001)
            {
                nearest = count;
                nearestDistance = outside;
            }

            if (output < minimum)
                break;
        }

        return bestFit != 0 ? bestFit : nearest;
    }

    private static List<List<VideoDedupSlice>> Realize(
        IReadOnlyList<double> durations,
        IReadOnlyList<double> cuts,
        VideoDedupPreset preset)
    {
        var groups = new List<List<VideoDedupSlice>>(cuts.Count - 1);
        for (var index = 0; index < cuts.Count - 1; index++)
        {
            var group = SliceRange(durations, cuts[index], cuts[index + 1]);
            if (group.Count == 0)
                continue;
            TrimArrangedEpisode(group, preset);
            if (group.Count > 0)
                groups.Add(group);
        }

        return groups;
    }

    private static List<VideoDedupSlice> SliceRange(IReadOnlyList<double> durations, double start, double end)
    {
        var slices = new List<VideoDedupSlice>();
        var cursor = 0d;
        for (var index = 0; index < durations.Count && cursor < end - 0.0001; index++)
        {
            var length = Math.Max(0, durations[index]);
            var clipEnd = cursor + length;
            var lower = Math.Max(start, cursor);
            var upper = Math.Min(end, clipEnd);
            if (upper - lower > 0.0005)
                slices.Add(new VideoDedupSlice(index, lower - cursor, upper - cursor));
            cursor = clipEnd;
        }

        return slices;
    }

    private static double[] Measure(
        IReadOnlyList<IReadOnlyList<VideoDedupSlice>> groups,
        VideoDedupPreset preset,
        IReadOnlyList<string>? clipPaths)
    {
        var predicted = new double[groups.Count];
        for (var index = 0; index < groups.Count; index++)
            predicted[index] = RoundDuration(PredictedOutputSeconds(groups[index], preset, clipPaths));
        return predicted;
    }

    private static bool AllInside(IReadOnlyList<double> seconds, double minimum, double maximum)
    {
        foreach (var value in seconds)
        {
            if (value < minimum || value > maximum)
                return false;
        }

        return true;
    }

    private static double RangeDistance(double seconds, double minimum, double maximum) =>
        seconds < minimum ? minimum - seconds : seconds > maximum ? seconds - maximum : 0;

    private static double OutputScale(VideoDedupPreset preset)
    {
        var speed = preset.SpeedFactor <= 0 ? 1 : preset.SpeedFactor;
        var keep = preset.FrameDropPercent <= 0
            ? 1
            : 1 - 1d / FrameDropInterval(preset.FrameDropPercent);
        return keep / speed;
    }

    /// <summary>对已经排好的一集做开头和结尾裁剪。裁掉的是原片秒数，变速还在这之后。</summary>
    private static void TrimArrangedEpisode(List<VideoDedupSlice> group, VideoDedupPreset preset)
    {
        var pendingStart = Math.Max(0, preset.TrimStartSeconds);
        var pendingEnd = Math.Max(0, preset.TrimEndSeconds);
        if (pendingStart <= 0.001 && pendingEnd <= 0.001)
            return;

        while (pendingStart > 0.001 && group.Count > 0)
        {
            var slice = group[0];
            if (slice.Length - pendingStart >= 0.2 || group.Count == 1)
            {
                var take = Math.Min(pendingStart, Math.Max(0, slice.Length - 0.2));
                group[0] = slice with { StartSeconds = slice.StartSeconds + take };
                break;
            }

            pendingStart -= slice.Length;
            group.RemoveAt(0);
        }

        while (pendingEnd > 0.001 && group.Count > 0)
        {
            var slice = group[^1];
            if (slice.Length - pendingEnd >= 0.2 || group.Count == 1)
            {
                var take = Math.Min(pendingEnd, Math.Max(0, slice.Length - 0.2));
                var trimmed = slice with { EndSeconds = slice.EndSeconds - take };
                if (trimmed.EndSeconds < trimmed.StartSeconds + 0.2)
                    trimmed = trimmed with { EndSeconds = Math.Min(slice.EndSeconds, trimmed.StartSeconds + 0.2) };
                group[^1] = trimmed;
                break;
            }

            pendingEnd -= slice.Length;
            group.RemoveAt(group.Count - 1);
        }
    }

    public static double ProcessedDuration(double rawSeconds, VideoDedupPreset preset)
    {
        var trimmed = Math.Max(0.2, rawSeconds - preset.TrimStartSeconds - preset.TrimEndSeconds);
        var speed = preset.SpeedFactor <= 0 ? 1 : preset.SpeedFactor;
        var keep = preset.FrameDropPercent <= 0
            ? 1
            : 1 - 1d / FrameDropInterval(preset.FrameDropPercent);
        return trimmed / speed * keep;
    }

    public static IReadOnlyList<string> BuildArguments(
        VideoDedupPreset preset,
        IReadOnlyList<VideoDedupClip> clips,
        string outputPath,
        string videoEncoder,
        int seed) =>
        BuildArguments(preset, clips, WholeSlices(clips, preset), outputPath, videoEncoder, seed);

    public static IReadOnlyList<string> BuildArguments(
        VideoDedupPreset preset,
        IReadOnlyList<VideoDedupClip> clips,
        IReadOnlyList<VideoDedupSlice> slices,
        string outputPath,
        string videoEncoder,
        int seed,
        VideoDedupHardwareDevice? outputDevice = null)
    {
        if (slices.Count == 0)
            throw new InvalidOperationException("没有可编码的视频。");

        VideoDedupEffectFilters.Validate(preset);
        var opened = OpenInputs(slices);
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
        outputDevice?.AppendDevice(args);
        var threads = VideoDedupCpuBudget.ThreadsFor(Environment.ProcessorCount, IsHardwareEncoder(videoEncoder));
        AppendFilterThreadLimits(args, threads);
        for (var index = 0; index < opened.Inputs.Count; index++)
        {
            var input = opened.Inputs[index];
            var decoderThreads = threads.DecoderThreads / opened.Inputs.Count
                + (index < threads.DecoderThreads % opened.Inputs.Count ? 1 : 0);
            args.AddRange(["-threads:v", Math.Max(1, decoderThreads).ToString(CultureInfo.InvariantCulture)]);
            if (input.SeekSeconds > 0.001)
                args.AddRange(["-ss", Num(input.SeekSeconds), "-t", Num(input.ReadSeconds)]);
            args.AddRange(["-i", clips[input.ClipIndex].Path]);
        }

        foreach (var image in VideoDedupEffectFilters.ImageInputs(preset))
            args.AddRange(["-i", image]);

        var hasAudio = slices.All(slice => clips[slice.ClipIndex].Probe.HasAudio);
        var useComplex = slices.Count > 1;
        var transitions = useComplex && CanTransition(preset, slices);
        if (useComplex)
        {
            var graph = new StringBuilder(BuildFilterComplex(preset, clips, slices, opened, transitions, seed));
            var label = VideoDedupEffectFilters.Finish(graph, "vout", preset, opened.Inputs.Count);
            args.AddRange(["-filter_complex", graph.ToString()]);
            args.AddRange(["-map", $"[{label}]"]);
            if (hasAudio)
                args.AddRange(["-map", "[aout]"]);
            else
                args.Add("-an");
        }
        else if (!VideoDedupEffectFilters.NeedsGraph(preset))
        {
            var slice = slices[0];
            var video = CombineFilters(
                BuildVideoFilter(clips[slice.ClipIndex], slice, preset, seed, opened.Origin[0],
                    reduceFramesBeforeScaling: preset.FrameDropPercent <= 0),
                VideoDedupEffectFilters.LinearFilters(preset));
            if (outputDevice is not null)
                video = CombineFilters(video, "format=nv12,hwupload");
            if (video.Length > 0)
                args.AddRange(["-vf", video]);
            AppendSingleAudio(args, clips[slice.ClipIndex], slice, preset, opened.Origin[0]);
        }
        else
        {
            var slice = slices[0];
            var graph = new StringBuilder();
            graph.Append("[0:v]").Append(BuildVideoFilter(clips[slice.ClipIndex], slice, preset, seed, opened.Origin[0])).Append("[vgeo]");
            var label = VideoDedupEffectFilters.Finish(graph, "vgeo", preset, opened.Inputs.Count);
            args.AddRange(["-filter_complex", graph.ToString(), "-map", $"[{label}]"]);
            if (clips[slice.ClipIndex].Probe.HasAudio)
            {
                args.AddRange(["-map", "0:a"]);
                var audio = BuildAudioFilter(clips[slice.ClipIndex], slice, preset, opened.Origin[0]);
                if (audio.Length > 0)
                    args.AddRange(["-af", audio]);
            }
            else
            {
                args.Add("-an");
            }
        }

        args.AddRange(["-threads:v", threads.EncoderThreads.ToString(CultureInfo.InvariantCulture)]);
        AppendEncoder(args, preset, videoEncoder, hasAudio, ResolveSourceAudioBitrateKbps(clips, slices),
            hardwareFrames: outputDevice is not null,
            videoBitrateMbps: ResolveVideoBitrate(preset, clips, slices).Mbps);
        args.AddRange(["-movflags", "+faststart", outputPath]);
        return args;
    }

    public static bool CanTransition(VideoDedupPreset preset, IReadOnlyList<VideoDedupSlice> slices)
    {
        if (!preset.TransitionsEnabled || !preset.HasAnyTransition || slices.Count < 2)
            return false;
        return slices.All(slice => OutputDuration(slice.Length, preset) >= 0.8);
    }

    private static VideoDedupSlice[] WholeSlices(IReadOnlyList<VideoDedupClip> clips, VideoDedupPreset preset)
    {
        var slices = new VideoDedupSlice[clips.Count];
        for (var i = 0; i < clips.Count; i++)
        {
            var duration = Math.Max(0, clips[i].Probe.DurationSeconds);
            var start = Math.Clamp(preset.TrimStartSeconds, 0, duration);
            var end = Math.Max(0, duration - Math.Max(0, preset.TrimEndSeconds));
            if (end < start + 0.2)
                end = Math.Min(duration, start + 0.2);
            slices[i] = new VideoDedupSlice(i, start, end);
        }

        return slices;
    }

    private static double OutputDuration(double contentSeconds, VideoDedupPreset preset)
    {
        var speed = preset.SpeedFactor <= 0 ? 1 : preset.SpeedFactor;
        var keep = preset.FrameDropPercent <= 0
            ? 1
            : 1 - 1d / FrameDropInterval(preset.FrameDropPercent);
        return Math.Max(0.2, contentSeconds) / speed * keep;
    }

    public static string PickTransition(VideoDedupPreset preset, int seed)
    {
        var options = new List<string>();
        if (preset.TransitionFadeBlack) options.Add("fadeblack");
        if (preset.TransitionDissolve) options.Add("fade");
        if (preset.TransitionPush) options.Add("smoothleft");
        if (preset.TransitionSlide) options.Add("slideleft");
        if (preset.TransitionZoom) options.Add("zoomin");
        if (options.Count == 0)
            return "fade";
        var index = Math.Abs(seed) % options.Count;
        return options[index];
    }

    public static double TransitionSeconds(int seed) =>
        0.3 + (Math.Abs(seed) % 31) / 100d;

    /// <summary>用正式编码参数试编几帧，避免试编码通过而正式参数（profile、码控、B 帧）在这张卡上不被支持。</summary>
    public static IReadOnlyList<string> BuildProbeArguments(VideoDedupPreset preset, string videoEncoder)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        var threads = VideoDedupCpuBudget.ThreadsFor(Environment.ProcessorCount, IsHardwareEncoder(videoEncoder));
        AppendFilterThreadLimits(args, threads);
        args.AddRange([
            "-f", "lavfi", "-i", "testsrc=size=1080x1920:rate=30:duration=0.3",
            "-frames:v", "5", "-threads:v", threads.EncoderThreads.ToString(CultureInfo.InvariantCulture),
        ]);
        AppendEncoder(args, preset, videoEncoder, includeAudio: false);
        args.AddRange(["-f", "null", "-"]);
        return args;
    }

    private static void AppendEncoder(
        List<string> args,
        VideoDedupPreset preset,
        string videoEncoder,
        bool includeAudio,
        int sourceAudioBitrateKbps = 0,
        bool hardwareFrames = false,
        double? videoBitrateMbps = null)
    {
        var rate = preset.Fps.ToString(CultureInfo.InvariantCulture);
        args.AddRange(["-r", rate, "-c:v", videoEncoder]);
        var qsv = videoEncoder.Contains("qsv", StringComparison.OrdinalIgnoreCase);
        var target = videoBitrateMbps ?? preset.VideoBitrateMbps;
        if (videoEncoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            AppendNvenc(args, preset, target);
        else if (videoEncoder.Contains("amf", StringComparison.OrdinalIgnoreCase))
            AppendAmf(args, preset, target);
        else if (qsv)
            AppendQsv(args, preset, target);
        else if (videoEncoder.Contains("videotoolbox", StringComparison.OrdinalIgnoreCase))
            AppendVideoToolbox(args, preset, target);
        else
            AppendSoftwareVideo(args, preset, videoEncoder, target);
        // QSV 只接收 NV12，直接给 yuv420p 会被拒或多一次隐式转换。
        if (!hardwareFrames)
            args.AddRange(["-pix_fmt", qsv ? "nv12" : "yuv420p"]);
        if (IsHevcEncoder(videoEncoder))
            args.AddRange(["-tag:v", "hvc1"]);

        if (!includeAudio)
            return;

        args.AddRange(["-c:a", "aac"]);
        var bitrate = preset.AudioBitrateKbps > 0 ? preset.AudioBitrateKbps : sourceAudioBitrateKbps;
        if (bitrate > 0)
            args.AddRange(["-b:a", $"{bitrate.ToString(CultureInfo.InvariantCulture)}k"]);
    }

    internal static int ResolveSourceAudioBitrateKbps(
        IReadOnlyList<VideoDedupClip> clips,
        IReadOnlyList<VideoDedupSlice> slices)
    {
        foreach (var slice in slices)
        {
            if ((uint)slice.ClipIndex >= (uint)clips.Count)
                continue;
            var bitrate = clips[slice.ClipIndex].Probe.AudioBitrateKbps;
            if (bitrate > 0)
                return bitrate;
        }

        return 0;
    }

    internal static bool UsesSourceVideoBitrate(VideoDedupPreset preset) => preset.UseSourceVideoBitrate && !IsCrf(preset);

    internal static double ResolveVideoBitrate(VideoDedupPreset preset, VideoDedupProbe probe) =>
        UsesSourceVideoBitrate(preset) && probe.VideoBitrateBps > 0
            ? probe.VideoBitrateBps / 1_000_000d
            : preset.VideoBitrateMbps;

    internal static (double Mbps, bool UsesFallback) ResolveVideoBitrate(
        VideoDedupPreset preset, IReadOnlyList<VideoDedupClip> clips, IReadOnlyList<VideoDedupSlice> slices)
    {
        if (!UsesSourceVideoBitrate(preset))
            return (preset.VideoBitrateMbps, false);

        double totalSeconds = 0;
        double weightedMbps = 0;
        var usesFallback = false;
        foreach (var slice in slices)
        {
            if ((uint)slice.ClipIndex >= (uint)clips.Count || slice.Length <= 0)
                continue;
            var probe = clips[slice.ClipIndex].Probe;
            usesFallback |= probe.VideoBitrateBps <= 0;
            weightedMbps += ResolveVideoBitrate(preset, probe) * slice.Length;
            totalSeconds += slice.Length;
        }

        return totalSeconds > 0 ? (weightedMbps / totalSeconds, usesFallback) : (preset.VideoBitrateMbps, true);
    }

    private static void AppendNvenc(List<string> args, VideoDedupPreset preset, double target)
    {
        var bitrate = BitrateToken(target);
        var buffer = BufferToken(target);
        args.AddRange(["-preset", "p2", "-gpu", "any", "-profile:v", "main", "-rc-lookahead", "0", "-bf", "3", "-b_ref_mode", "middle"]);
        if (IsCrf(preset))
            args.AddRange(["-rc", "vbr", "-cq", preset.Crf.ToString(CultureInfo.InvariantCulture), "-b:v", "0"]);
        else if (IsCbr(preset))
            args.AddRange(["-rc", "cbr", "-b:v", bitrate, "-maxrate", bitrate, "-minrate", bitrate, "-bufsize", buffer]);
        else
            args.AddRange(["-rc", "vbr", "-b:v", bitrate, "-maxrate", MaxRateToken(target), "-bufsize", buffer]);
    }

    private static void AppendAmf(List<string> args, VideoDedupPreset preset, double target)
    {
        var bitrate = BitrateToken(target);
        var buffer = BufferToken(target);
        args.AddRange(["-usage", "transcoding", "-quality", "speed", "-profile:v", "main"]);
        if (IsCrf(preset))
        {
            // AMF 的固定 QP 比 x264 同数值的 CRF 码率高约一倍，I 帧 +2、P/B 帧 +4 后接近同等体积。
            var qpI = Math.Clamp(preset.Crf + 2, 0, 51).ToString(CultureInfo.InvariantCulture);
            var qpP = Math.Clamp(preset.Crf + 4, 0, 51).ToString(CultureInfo.InvariantCulture);
            args.AddRange(["-rc", "cqp", "-qp_i", qpI, "-qp_p", qpP, "-qp_b", qpP]);
        }
        else if (IsCbr(preset))
            args.AddRange(["-rc", "cbr", "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", buffer, "-filler_data", "1"]);
        else
            args.AddRange(["-rc", "vbr_peak", "-b:v", bitrate, "-maxrate", MaxRateToken(target), "-bufsize", buffer]);
    }

    private static void AppendQsv(List<string> args, VideoDedupPreset preset, double target)
    {
        args.AddRange(["-profile:v", "main", "-preset", "veryfast"]);
        if (IsCrf(preset))
        {
            args.AddRange(["-global_quality", preset.Crf.ToString(CultureInfo.InvariantCulture)]);
            return;
        }

        var bitrate = BitrateToken(target);
        args.AddRange([
            "-b:v", bitrate,
            "-maxrate", IsCbr(preset) ? bitrate : MaxRateToken(target),
            "-bufsize", BufferToken(target),
        ]);
    }

    private static void AppendVideoToolbox(List<string> args, VideoDedupPreset preset, double target)
    {
        if (IsCrf(preset))
            args.AddRange(["-q:v", preset.Crf.ToString(CultureInfo.InvariantCulture)]);
        else
            args.AddRange(["-b:v", BitrateToken(target), "-maxrate", IsCbr(preset) ? BitrateToken(target) : MaxRateToken(target), "-bufsize", BufferToken(target)]);
    }

    private static void AppendSoftwareVideo(List<string> args, VideoDedupPreset preset, string videoEncoder, double target)
    {
        var hevc = IsHevcEncoder(videoEncoder);
        args.AddRange(["-profile:v", "main", "-preset", "veryfast"]);
        var bitrate = BitrateToken(target);
        if (IsCrf(preset))
            args.AddRange(["-crf", preset.Crf.ToString(CultureInfo.InvariantCulture)]);
        else if (IsCbr(preset))
            args.AddRange(["-b:v", bitrate, "-minrate", bitrate, "-maxrate", bitrate, "-bufsize", bitrate]);
        else
            args.AddRange(["-b:v", bitrate, "-maxrate", MaxRateToken(target), "-bufsize", BufferToken(target)]);
        var threads = VideoDedupCpuBudget.ThreadsFor(Environment.ProcessorCount, hardwareEncoder: false);
        if (hevc)
            args.AddRange(["-x265-params", $"log-level=error:pools={threads.EncoderThreads.ToString(CultureInfo.InvariantCulture)}"]);
        else
            args.AddRange(["-x264-params", $"threads={threads.EncoderThreads.ToString(CultureInfo.InvariantCulture)}:lookahead-threads=1"]);
    }

    private static void AppendFilterThreadLimits(List<string> args, VideoDedupCpuBudget.EncodeThreads threads)
    {
        var filter = threads.FilterThreads.ToString(CultureInfo.InvariantCulture);
        args.AddRange([
            "-filter_threads", filter,
            "-filter_complex_threads", filter,
        ]);
    }

    internal static bool IsHardwareEncoder(string videoEncoder) =>
        videoEncoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase) ||
        videoEncoder.Contains("amf", StringComparison.OrdinalIgnoreCase) ||
        videoEncoder.Contains("qsv", StringComparison.OrdinalIgnoreCase) ||
        videoEncoder.Contains("videotoolbox", StringComparison.OrdinalIgnoreCase);

    internal static bool IsHevcEncoder(string videoEncoder) =>
        videoEncoder.Contains("265", StringComparison.OrdinalIgnoreCase) ||
        videoEncoder.Contains("hevc", StringComparison.OrdinalIgnoreCase);

    private static bool IsCrf(VideoDedupPreset preset) =>
        string.Equals(preset.BitrateMode, VideoDedupPreset.BitrateCrf, StringComparison.Ordinal);

    private static bool IsCbr(VideoDedupPreset preset) =>
        string.Equals(preset.BitrateMode, VideoDedupPreset.BitrateCbr, StringComparison.Ordinal);

    private static string BitrateToken(double target) => KbpsToken(target * 1000);

    private static string MaxRateToken(double target) => KbpsToken(target * 1450);

    private static string BufferToken(double target) => KbpsToken(target * 2000);

    private static string KbpsToken(double kbps) =>
        $"{Math.Max(1, Math.Round(kbps)).ToString("0", CultureInfo.InvariantCulture)}k";

    private static string BuildFilterComplex(
        VideoDedupPreset preset,
        IReadOnlyList<VideoDedupClip> clips,
        IReadOnlyList<VideoDedupSlice> slices,
        OpenedInputs opened,
        bool transitions,
        int seed)
    {
        var graph = new StringBuilder();
        var sharedFrame = SharedEpisodeFrame(clips, slices, preset);
        for (var i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            var clip = clips[slice.ClipIndex];
            var input = opened.SliceInput[i].ToString(CultureInfo.InvariantCulture);
            var label = i.ToString(CultureInfo.InvariantCulture);
            var video = BuildVideoFilter(clip, slice, preset, seed + i * 17, opened.Origin[i], sharedFrame,
                normalizeSampleAspectRatio: true);
            if (video.Length == 0)
                video = "setpts=PTS-STARTPTS";
            graph.Append($"[{input}:v]{video},format=yuv420p,settb=AVTB[v{label}];");
            if (clip.Probe.HasAudio)
            {
                var audio = BuildAudioFilter(clip, slice, preset, opened.Origin[i]);
                if (audio.Length == 0)
                    audio = "asetpts=PTS-STARTPTS";
                graph.Append($"[{input}:a]{audio}[a{label}];");
            }
        }

        if (!transitions)
        {
            for (var i = 0; i < slices.Count; i++)
                graph.Append($"[v{i.ToString(CultureInfo.InvariantCulture)}]");
            graph.Append($"concat=n={slices.Count.ToString(CultureInfo.InvariantCulture)}:v=1:a=0[vout]");
            if (slices.All(slice => clips[slice.ClipIndex].Probe.HasAudio))
            {
                graph.Append(';');
                for (var i = 0; i < slices.Count; i++)
                    graph.Append($"[a{i.ToString(CultureInfo.InvariantCulture)}]");
                graph.Append($"concat=n={slices.Count.ToString(CultureInfo.InvariantCulture)}:v=0:a=1[aout]");
            }

            return graph.ToString();
        }

        var videoLabel = "v0";
        var audioLabel = "a0";
        var hasAudio = slices.All(slice => clips[slice.ClipIndex].Probe.HasAudio);
        double elapsed = OutputDuration(slices[0].Length, preset);
        for (var i = 1; i < slices.Count; i++)
        {
            if (graph.Length > 0 && graph[^1] != ';')
                graph.Append(';');
            var junction = Hash(clips[slices[i - 1].ClipIndex].Path, clips[slices[i].ClipIndex].Path, seed + i);
            var duration = TransitionSeconds(junction);
            var name = PickTransition(preset, junction);
            var offset = Math.Max(0, elapsed - duration);
            var nextVideo = i == slices.Count - 1 ? "vout" : $"vx{i.ToString(CultureInfo.InvariantCulture)}";
            graph.Append(
                $"[{videoLabel}][v{i.ToString(CultureInfo.InvariantCulture)}]xfade=transition={name}:duration={Num(duration)}:offset={Num(offset)}[{nextVideo}]");
            videoLabel = nextVideo;
            if (hasAudio)
            {
                var nextAudio = i == slices.Count - 1 ? "aout" : $"ax{i.ToString(CultureInfo.InvariantCulture)}";
                graph.Append(
                    $";[{audioLabel}][a{i.ToString(CultureInfo.InvariantCulture)}]acrossfade=d={Num(duration)}:c1=tri:c2=tri[{nextAudio}]");
                audioLabel = nextAudio;
            }

            elapsed += OutputDuration(slices[i].Length, preset) - duration;
        }

        return graph.ToString().TrimEnd(';');
    }

    internal readonly record struct UploadFrame(int Width, int Height, bool Scaled);

    internal static UploadFrame ResolveUploadFrame(int sourceWidth, int sourceHeight, VideoDedupPreset preset)
    {
        var widthRatio = 1 - (preset.CropLeftPercent + preset.CropRightPercent) / 100d;
        var heightRatio = 1 - (preset.CropTopPercent + preset.CropBottomPercent) / 100d;
        var croppedWidth = CroppedEven(sourceWidth, widthRatio);
        var croppedHeight = CroppedEven(sourceHeight, heightRatio);
        if (croppedWidth <= 0 || croppedHeight <= 0)
            return default;

        var portrait = croppedHeight >= croppedWidth;
        if (!preset.KeepsOriginalResolution)
        {
            var targetWidth = portrait ? preset.PortraitWidth : preset.PortraitHeight;
            var targetHeight = portrait ? preset.PortraitHeight : preset.PortraitWidth;
            if (targetWidth <= 0 || targetHeight <= 0 || Math.Min(targetWidth, targetHeight) < TikTokVideoConstraints.MinShortSide)
            {
                targetWidth = portrait ? TikTokVideoConstraints.UploadPortraitWidth : TikTokVideoConstraints.UploadPortraitHeight;
                targetHeight = portrait ? TikTokVideoConstraints.UploadPortraitHeight : TikTokVideoConstraints.UploadPortraitWidth;
            }

            targetWidth = Even(targetWidth);
            targetHeight = Even(targetHeight);
            return new UploadFrame(targetWidth, targetHeight, targetWidth != croppedWidth || targetHeight != croppedHeight);
        }

        if (Math.Min(croppedWidth, croppedHeight) >= TikTokVideoConstraints.MinShortSide)
            return new UploadFrame(croppedWidth, croppedHeight, false);

        if (Math.Min(sourceWidth, sourceHeight) >= TikTokVideoConstraints.MinShortSide)
            return new UploadFrame(Even(sourceWidth), Even(sourceHeight), true);

        return portrait
            ? new UploadFrame(TikTokVideoConstraints.UploadPortraitWidth, TikTokVideoConstraints.UploadPortraitHeight, true)
            : new UploadFrame(TikTokVideoConstraints.UploadPortraitHeight, TikTokVideoConstraints.UploadPortraitWidth, true);
    }

    /// <summary>
    /// 同一集里各段成片尺寸不一致时，转场和拼接都要求宽高相同。
    /// 返回这一集统一使用的标准画面；尺寸已经一致时返回空，单段保持各自的尺寸。
    /// </summary>
    internal static UploadFrame? SharedEpisodeFrame(
        IReadOnlyList<VideoDedupClip> clips,
        IReadOnlyList<VideoDedupSlice> slices,
        VideoDedupPreset preset)
    {
        if (slices.Count < 2)
            return null;

        UploadFrame first = default;
        var haveFrame = false;
        var distinct = false;
        var portraitVotes = 0;
        var landscapeVotes = 0;
        foreach (var slice in slices)
        {
            var probe = clips[slice.ClipIndex].Probe;
            var frame = ResolveUploadFrame(probe.Width, probe.Height, preset);
            if (frame.Width <= 0 || frame.Height <= 0)
                continue;
            if (!haveFrame)
            {
                first = frame;
                haveFrame = true;
            }
            else if (first.Width != frame.Width || first.Height != frame.Height)
            {
                distinct = true;
            }

            if (frame.Height >= frame.Width)
                portraitVotes++;
            else
                landscapeVotes++;
        }

        if (!distinct)
            return null;

        var portrait = portraitVotes > landscapeVotes ||
                       (portraitVotes == landscapeVotes && first.Height >= first.Width);
        return portrait
            ? new UploadFrame(TikTokVideoConstraints.UploadPortraitWidth, TikTokVideoConstraints.UploadPortraitHeight, true)
            : new UploadFrame(TikTokVideoConstraints.UploadPortraitHeight, TikTokVideoConstraints.UploadPortraitWidth, true);
    }

    private static int CroppedEven(int source, double ratio)
    {
        if (source <= 0 || ratio <= 0)
            return 0;
        return (int)(Math.Truncate(source * ratio / 2d) * 2d);
    }

    private static int Even(int value) => value <= 0 ? 0 : value - (value % 2);

    private static string BuildVideoFilter(
        VideoDedupClip clip,
        VideoDedupSlice slice,
        VideoDedupPreset preset,
        int seed,
        double originSeconds,
        UploadFrame? sharedFrame = null,
        bool reduceFramesBeforeScaling = false,
        bool normalizeSampleAspectRatio = false)
    {
        var filters = new List<string>();
        var start = slice.StartSeconds - originSeconds;
        var end = slice.EndSeconds - originSeconds;
        var limit = Math.Max(0, clip.Probe.DurationSeconds - originSeconds);
        if (start > 0.001 || end < limit - 0.001)
            filters.Add($"trim=start={Num(start)}:end={Num(end)},setpts=PTS-STARTPTS");

        if (preset.CropTopPercent + preset.CropBottomPercent + preset.CropLeftPercent + preset.CropRightPercent > 0)
        {
            var width = 1 - (preset.CropLeftPercent + preset.CropRightPercent) / 100d;
            var height = 1 - (preset.CropTopPercent + preset.CropBottomPercent) / 100d;
            filters.Add(
                $"crop=trunc(iw*{Num(width)}/2)*2:trunc(ih*{Num(height)}/2)*2:trunc(iw*{Num(preset.CropLeftPercent / 100d)}/2)*2:trunc(ih*{Num(preset.CropTopPercent / 100d)}/2)*2");
        }

        var effects = preset.Effects ?? new VideoDedupEffects();
        filters.AddRange(VideoDedupEffectFilters.CompositionFilters(effects, VideoDedupEffectFilters.CompositionSeed(clip.Path)));

        var frame = sharedFrame ?? ResolveUploadFrame(clip.Probe.Width, clip.Probe.Height, preset);
        var downsampleBeforeScale = reduceFramesBeforeScaling && frame.Scaled
            && clip.Probe.AverageFramesPerSecond * preset.SpeedFactor > preset.Fps;
        if (downsampleBeforeScale)
            AppendFrameRateFilters(filters, preset);

        if (sharedFrame is not null || frame.Scaled || effects.ReframeEnabled && effects.ReframeKeepPercent < 100)
        {
            filters.Add(
                $"scale={frame.Width.ToString(CultureInfo.InvariantCulture)}:{frame.Height.ToString(CultureInfo.InvariantCulture)}:flags=bicubic");
        }

        // Scaling to the same dimensions can still leave different sample aspect ratios for each segment.
        if (normalizeSampleAspectRatio || effects.ReframeEnabled && effects.ReframeKeepPercent < 100)
            filters.Add("setsar=1");

        filters.Add("format=yuv420p,setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709");

        if (!downsampleBeforeScale)
            AppendFrameRateFilters(filters, preset);

        if (preset.FrameDropPercent > 0)
            filters.Add(FrameDropFilter(preset.FrameDropPercent, seed));

        return string.Join(",", filters);
    }

    private static void AppendFrameRateFilters(List<string> filters, VideoDedupPreset preset)
    {
        // Convert frame rate after changing speed so the FPS filter sees the final timestamps.
        if (Math.Abs(preset.SpeedFactor - 1) > 0.0001)
            filters.Add($"setpts=PTS/{Num(preset.SpeedFactor)}");
        filters.Add($"fps={preset.Fps.ToString(CultureInfo.InvariantCulture)}");
    }

    internal static int FrameDropInterval(double percent) =>
        Math.Max(2, (int)Math.Round(100d / percent));

    /// <summary>每 interval 帧恰好丢 1 帧，位置在块内随机，丢帧总数与音频补偿速率一致。</summary>
    internal static string FrameDropFilter(double percent, int seed)
    {
        var interval = FrameDropInterval(percent).ToString(CultureInfo.InvariantCulture);
        var salt = (Math.Abs(seed % 9973) + 1).ToString(CultureInfo.InvariantCulture);
        return $"select='not(eq(mod(n\\,{interval})\\,mod(floor(abs(sin((floor(n/{interval})+{salt})*12.9898))*43758.5453)\\,{interval})))',setpts=N/FRAME_RATE/TB";
    }

    private static void AppendSingleAudio(
        List<string> args,
        VideoDedupClip clip,
        VideoDedupSlice slice,
        VideoDedupPreset preset,
        double originSeconds)
    {
        if (!clip.Probe.HasAudio)
        {
            args.Add("-an");
            return;
        }

        var audio = BuildAudioFilter(clip, slice, preset, originSeconds);
        if (audio.Length > 0)
            args.AddRange(["-af", audio]);
    }

    private static string CombineFilters(string left, string right)
    {
        if (left.Length == 0)
            return right;
        if (right.Length == 0)
            return left;
        return left + "," + right;
    }

    private static string BuildAudioFilter(VideoDedupClip clip, VideoDedupSlice slice, VideoDedupPreset preset, double originSeconds)
    {
        var filters = new List<string>();
        var start = slice.StartSeconds - originSeconds;
        var end = slice.EndSeconds - originSeconds;
        var limit = Math.Max(0, clip.Probe.DurationSeconds - originSeconds);
        if (start > 0.001 || end < limit - 0.001)
            filters.Add($"atrim=start={Num(start)}:end={Num(end)},asetpts=PTS-STARTPTS");
        filters.Add("aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo");

        var tempo = preset.SpeedFactor <= 0 ? 1 : preset.SpeedFactor;
        if (preset.FrameDropPercent > 0)
        {
            var interval = FrameDropInterval(preset.FrameDropPercent);
            tempo *= interval / (double)(interval - 1);
        }

        if (Math.Abs(tempo - 1) > 0.0001)
            filters.Add(BuildAtempo(tempo));

        if (Math.Abs(preset.AudioLowGainDb) > 0.01)
            filters.Add($"equalizer=f=100:t=q:w=1:g={Num(preset.AudioLowGainDb)}");
        if (Math.Abs(preset.AudioMidGainDb) > 0.01)
            filters.Add($"equalizer=f=1000:t=q:w=1:g={Num(preset.AudioMidGainDb)}");
        if (Math.Abs(preset.AudioHighGainDb) > 0.01)
            filters.Add($"equalizer=f=8000:t=q:w=1:g={Num(preset.AudioHighGainDb)}");
        return string.Join(",", filters);
    }

    private static string BuildAtempo(double factor)
    {
        var filters = new List<string>();
        var remaining = factor;
        while (remaining < 0.5)
        {
            filters.Add("atempo=0.5");
            remaining /= 0.5;
        }

        while (remaining > 2)
        {
            filters.Add("atempo=2.0");
            remaining /= 2;
        }

        filters.Add($"atempo={Num(remaining)}");
        return string.Join(",", filters);
    }

    private static int Hash(string left, string right, int seed)
    {
        var text = left + "|" + right + "|" + seed.ToString(CultureInfo.InvariantCulture);
        unchecked
        {
            var hash = 17;
            foreach (var ch in text)
                hash = hash * 31 + ch;
            return hash;
        }
    }

    /// <summary>
    /// 切点离开片头超过 1 秒时，先快进到切点再按相对时间裁切。
    /// 同一原片的两个切点各开一路输入，避免共用一个跳转位置。
    /// </summary>
    private static OpenedInputs OpenInputs(IReadOnlyList<VideoDedupSlice> slices)
    {
        var inputs = new List<OpenedInput>();
        var sliceInput = new int[slices.Count];
        var origin = new double[slices.Count];
        for (var i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            var seek = slice.StartSeconds >= 1d ? slice.StartSeconds : 0d;
            var index = -1;
            for (var slot = 0; slot < inputs.Count; slot++)
            {
                if (inputs[slot].ClipIndex == slice.ClipIndex && Math.Abs(inputs[slot].SeekSeconds - seek) < 0.001)
                {
                    index = slot;
                    break;
                }
            }

            if (index < 0)
            {
                index = inputs.Count;
                var read = seek > 0 ? slice.Length + 1d : 0d;
                inputs.Add(new OpenedInput(slice.ClipIndex, seek, read));
            }
            else if (seek > 0)
            {
                var read = Math.Max(inputs[index].ReadSeconds, slice.EndSeconds - seek + 1d);
                inputs[index] = inputs[index] with { ReadSeconds = read };
            }

            sliceInput[i] = index;
            origin[i] = seek;
        }

        return new OpenedInputs(inputs, sliceInput, origin);
    }

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private readonly record struct OpenedInput(int ClipIndex, double SeekSeconds, double ReadSeconds);

    private readonly record struct OpenedInputs(List<OpenedInput> Inputs, int[] SliceInput, double[] Origin);
}
