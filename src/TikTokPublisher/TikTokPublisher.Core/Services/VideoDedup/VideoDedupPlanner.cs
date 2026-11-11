using System.Globalization;
using System.Text;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services.VideoDedup;

internal sealed record VideoDedupProbe(double DurationSeconds, int Width, int Height, bool HasAudio);

internal sealed record VideoDedupClip(string Path, VideoDedupProbe Probe);

internal readonly record struct VideoDedupSlice(int ClipIndex, double StartSeconds, double EndSeconds)
{
    public double Length => Math.Max(0, EndSeconds - StartSeconds);
}

internal static class VideoDedupPlanner
{
    public static IReadOnlyList<IReadOnlyList<VideoDedupSlice>> Plan(IReadOnlyList<double> rawDurations, VideoDedupPreset preset)
    {
        var count = rawDurations.Count;
        if (count == 0)
            return [];

        var slices = new List<VideoDedupSlice>();
        for (var i = 0; i < count; i++)
            slices.AddRange(SliceClip(i, Math.Max(0, rawDurations[i]), preset));

        var groups = new List<List<VideoDedupSlice>>();
        if (!preset.HasDurationLimit)
        {
            foreach (var slice in slices)
                groups.Add([slice]);
            return groups;
        }

        var minimum = preset.MinimumDurationSeconds;
        var maximum = preset.MaximumDurationSeconds;
        var current = new List<VideoDedupSlice>();
        double sum = 0;
        foreach (var slice in slices)
        {
            var alreadyEnough = minimum <= 0 || sum >= minimum;
            var wouldExceed = maximum > 0 && current.Count > 0 && sum + slice.Length > maximum;
            if (alreadyEnough && wouldExceed)
            {
                groups.Add(current);
                current = [];
                sum = 0;
            }

            current.Add(slice);
            sum += slice.Length;
        }

        if (current.Count > 0)
            groups.Add(current);

        if (minimum > 0 && groups.Count > 1)
        {
            var lastLength = groups[^1].Sum(slice => slice.Length);
            var previousLength = groups[^2].Sum(slice => slice.Length);
            if (lastLength < minimum && (maximum <= 0 || previousLength + lastLength <= maximum))
            {
                groups[^2].AddRange(groups[^1]);
                groups.RemoveAt(groups.Count - 1);
            }
        }

        return groups;
    }

    private static List<VideoDedupSlice> SliceClip(int index, double duration, VideoDedupPreset preset)
    {
        if (!preset.HasDurationLimit)
        {
            var start = Math.Clamp(preset.TrimStartSeconds, 0, duration);
            var end = Math.Max(0, duration - Math.Max(0, preset.TrimEndSeconds));
            if (end < start + 0.2)
                end = Math.Min(duration, start + 0.2);
            return [new VideoDedupSlice(index, start, end)];
        }

        if (preset.MaximumDurationSeconds <= 0 || duration <= preset.MaximumDurationSeconds)
            return [new VideoDedupSlice(index, 0, duration)];

        var maximum = preset.MaximumDurationSeconds;
        var gap = Math.Max(0, preset.TrimStartSeconds) + Math.Max(0, preset.TrimEndSeconds);
        var slices = new List<VideoDedupSlice>();
        var cursor = 0d;
        while (duration - cursor > maximum)
        {
            var resume = cursor + maximum + gap;
            if (resume >= duration)
            {
                slices.Add(new VideoDedupSlice(index, cursor, Math.Min(duration, cursor + maximum)));
                return slices;
            }

            slices.Add(new VideoDedupSlice(index, cursor, cursor + maximum));
            cursor = resume;
        }

        if (cursor < duration)
            slices.Add(new VideoDedupSlice(index, cursor, duration));

        if (preset.MinimumDurationSeconds > 0 && slices.Count > 1 &&
            slices[^1].Length < preset.MinimumDurationSeconds)
        {
            var previous = slices[^2];
            var absorbed = previous.Length + gap + slices[^1].Length;
            if (absorbed <= maximum)
            {
                slices[^2] = previous with { EndSeconds = slices[^1].EndSeconds };
                slices.RemoveAt(slices.Count - 1);
            }
        }

        return slices;
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
        int seed)
    {
        if (slices.Count == 0)
            throw new InvalidOperationException("没有可编码的视频。");

        VideoDedupEffectFilters.Validate(preset);
        var inputs = new List<int>();
        foreach (var slice in slices)
        {
            if (!inputs.Contains(slice.ClipIndex))
                inputs.Add(slice.ClipIndex);
        }

        var localInput = new Dictionary<int, int>();
        for (var i = 0; i < inputs.Count; i++)
            localInput[inputs[i]] = i;

        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
        foreach (var index in inputs)
            args.AddRange(["-i", clips[index].Path]);
        foreach (var image in VideoDedupEffectFilters.ImageInputs(preset))
            args.AddRange(["-i", image]);

        var hasAudio = slices.All(slice => clips[slice.ClipIndex].Probe.HasAudio);
        var useComplex = slices.Count > 1;
        var transitions = useComplex && CanTransition(preset, slices);
        if (useComplex)
        {
            var graph = new StringBuilder(BuildFilterComplex(preset, clips, slices, localInput, transitions, seed));
            var label = VideoDedupEffectFilters.Finish(graph, "vout", preset, inputs.Count);
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
                BuildVideoFilter(clips[slice.ClipIndex], slice, preset, seed),
                VideoDedupEffectFilters.LinearFilters(preset));
            if (video.Length > 0)
                args.AddRange(["-vf", video]);
            AppendSingleAudio(args, clips[slice.ClipIndex], slice, preset);
        }
        else
        {
            var slice = slices[0];
            var graph = new StringBuilder();
            graph.Append("[0:v]").Append(BuildVideoFilter(clips[slice.ClipIndex], slice, preset, seed)).Append("[vgeo]");
            var label = VideoDedupEffectFilters.Finish(graph, "vgeo", preset, 1);
            args.AddRange(["-filter_complex", graph.ToString(), "-map", $"[{label}]"]);
            if (clips[slice.ClipIndex].Probe.HasAudio)
            {
                args.AddRange(["-map", "0:a"]);
                var audio = BuildAudioFilter(clips[slice.ClipIndex], slice, preset);
                if (audio.Length > 0)
                    args.AddRange(["-af", audio]);
            }
            else
            {
                args.Add("-an");
            }
        }

        AppendEncoder(args, preset, videoEncoder, hasAudio);
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

    private static void AppendEncoder(
        List<string> args,
        VideoDedupPreset preset,
        string videoEncoder,
        bool includeAudio)
    {
        var rate = preset.Fps.ToString(CultureInfo.InvariantCulture);
        args.AddRange(["-r", rate, "-c:v", videoEncoder]);
        if (videoEncoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            AppendNvenc(args, preset);
        else if (videoEncoder.Contains("amf", StringComparison.OrdinalIgnoreCase))
            AppendAmf(args, preset);
        else if (videoEncoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
            AppendQsv(args, preset);
        else if (videoEncoder.Contains("videotoolbox", StringComparison.OrdinalIgnoreCase))
            AppendVideoToolbox(args, preset);
        else
            AppendSoftwareVideo(args, preset, videoEncoder);
        args.AddRange(["-pix_fmt", "yuv420p"]);

        if (!includeAudio)
            return;

        args.AddRange(["-c:a", "aac"]);
        if (preset.AudioBitrateKbps > 0)
            args.AddRange(["-b:a", $"{preset.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture)}k"]);
        else
            args.AddRange(["-b:a", "128k"]);
    }

    private static void AppendNvenc(List<string> args, VideoDedupPreset preset)
    {
        var bitrate = BitrateToken(preset);
        var buffer = BufferToken(preset);
        args.AddRange(["-preset", "p4", "-gpu", "any", "-profile:v", "main", "-rc-lookahead", "8", "-bf", "3", "-b_ref_mode", "middle"]);
        if (IsCrf(preset))
            args.AddRange(["-rc", "vbr", "-cq", preset.Crf.ToString(CultureInfo.InvariantCulture), "-b:v", "0"]);
        else if (IsCbr(preset))
            args.AddRange(["-rc", "cbr", "-b:v", bitrate, "-maxrate", bitrate, "-minrate", bitrate, "-bufsize", buffer]);
        else
            args.AddRange(["-rc", "vbr", "-b:v", bitrate, "-maxrate", MaxRateToken(preset), "-bufsize", buffer]);
    }

    private static void AppendAmf(List<string> args, VideoDedupPreset preset)
    {
        var bitrate = BitrateToken(preset);
        var buffer = BufferToken(preset);
        var crf = preset.Crf.ToString(CultureInfo.InvariantCulture);
        args.AddRange(["-usage", "transcoding", "-quality", "balanced", "-profile:v", "main"]);
        if (IsCrf(preset))
            args.AddRange(["-rc", "cqp", "-qp_i", crf, "-qp_p", crf, "-qp_b", crf]);
        else if (IsCbr(preset))
            args.AddRange(["-rc", "cbr", "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", buffer, "-filler_data", "1"]);
        else
            args.AddRange(["-rc", "vbr_peak", "-b:v", bitrate, "-maxrate", MaxRateToken(preset), "-bufsize", buffer]);
    }

    private static void AppendQsv(List<string> args, VideoDedupPreset preset)
    {
        args.AddRange(["-profile:v", "main", "-preset", "veryfast"]);
        if (IsCrf(preset))
        {
            args.AddRange(["-global_quality", preset.Crf.ToString(CultureInfo.InvariantCulture)]);
            return;
        }

        var bitrate = BitrateToken(preset);
        args.AddRange([
            "-b:v", bitrate,
            "-maxrate", IsCbr(preset) ? bitrate : MaxRateToken(preset),
            "-bufsize", BufferToken(preset),
        ]);
    }

    private static void AppendVideoToolbox(List<string> args, VideoDedupPreset preset)
    {
        if (IsCrf(preset))
            args.AddRange(["-q:v", preset.Crf.ToString(CultureInfo.InvariantCulture)]);
        else
            args.AddRange(["-b:v", BitrateToken(preset), "-maxrate", IsCbr(preset) ? BitrateToken(preset) : MaxRateToken(preset)]);
    }

    private static void AppendSoftwareVideo(List<string> args, VideoDedupPreset preset, string videoEncoder)
    {
        var hevc = videoEncoder.Contains("265", StringComparison.OrdinalIgnoreCase) ||
                   videoEncoder.Contains("hevc", StringComparison.OrdinalIgnoreCase);
        args.AddRange(["-profile:v", "main", "-preset", hevc ? "fast" : "veryfast"]);
        var bitrate = BitrateToken(preset);
        if (IsCrf(preset))
            args.AddRange(["-crf", preset.Crf.ToString(CultureInfo.InvariantCulture)]);
        else if (IsCbr(preset))
            args.AddRange(["-b:v", bitrate, "-minrate", bitrate, "-maxrate", bitrate, "-bufsize", bitrate]);
        else
            args.AddRange(["-b:v", bitrate, "-maxrate", MaxRateToken(preset)]);
    }

    private static bool IsCrf(VideoDedupPreset preset) =>
        string.Equals(preset.BitrateMode, VideoDedupPreset.BitrateCrf, StringComparison.Ordinal);

    private static bool IsCbr(VideoDedupPreset preset) =>
        string.Equals(preset.BitrateMode, VideoDedupPreset.BitrateCbr, StringComparison.Ordinal);

    private static string BitrateToken(VideoDedupPreset preset) =>
        $"{Math.Max(500, (int)Math.Round(preset.VideoBitrateMbps * 1000)).ToString(CultureInfo.InvariantCulture)}k";

    private static string MaxRateToken(VideoDedupPreset preset) =>
        $"{Math.Max(500, (int)Math.Round(preset.VideoBitrateMbps * 1450)).ToString(CultureInfo.InvariantCulture)}k";

    private static string BufferToken(VideoDedupPreset preset) =>
        $"{Math.Max(500, (int)Math.Round(preset.VideoBitrateMbps * 2000)).ToString(CultureInfo.InvariantCulture)}k";

    private static string BuildFilterComplex(
        VideoDedupPreset preset,
        IReadOnlyList<VideoDedupClip> clips,
        IReadOnlyList<VideoDedupSlice> slices,
        IReadOnlyDictionary<int, int> localInput,
        bool transitions,
        int seed)
    {
        var graph = new StringBuilder();
        for (var i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            var clip = clips[slice.ClipIndex];
            var input = localInput[slice.ClipIndex].ToString(CultureInfo.InvariantCulture);
            var label = i.ToString(CultureInfo.InvariantCulture);
            var video = BuildVideoFilter(clip, slice, preset, seed + i * 17);
            if (video.Length == 0)
                video = "setpts=PTS-STARTPTS";
            graph.Append($"[{input}:v]{video},format=yuv420p,settb=AVTB[v{label}];");
            if (clip.Probe.HasAudio)
            {
                var audio = BuildAudioFilter(clip, slice, preset);
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

    private static int CroppedEven(int source, double ratio)
    {
        if (source <= 0 || ratio <= 0)
            return 0;
        return (int)(Math.Truncate(source * ratio / 2d) * 2d);
    }

    private static int Even(int value) => value <= 0 ? 0 : value - (value % 2);

    private static string BuildVideoFilter(VideoDedupClip clip, VideoDedupSlice slice, VideoDedupPreset preset, int seed)
    {
        var filters = new List<string>();
        var duration = Math.Max(0, clip.Probe.DurationSeconds);
        if (slice.StartSeconds > 0.001 || slice.EndSeconds < duration - 0.001)
            filters.Add($"trim=start={Num(slice.StartSeconds)}:end={Num(slice.EndSeconds)},setpts=PTS-STARTPTS");

        if (preset.CropTopPercent + preset.CropBottomPercent + preset.CropLeftPercent + preset.CropRightPercent > 0)
        {
            var width = 1 - (preset.CropLeftPercent + preset.CropRightPercent) / 100d;
            var height = 1 - (preset.CropTopPercent + preset.CropBottomPercent) / 100d;
            filters.Add(
                $"crop=trunc(iw*{Num(width)}/2)*2:trunc(ih*{Num(height)}/2)*2:trunc(iw*{Num(preset.CropLeftPercent / 100d)}/2)*2:trunc(ih*{Num(preset.CropTopPercent / 100d)}/2)*2");
        }

        var frame = ResolveUploadFrame(clip.Probe.Width, clip.Probe.Height, preset);
        if (frame.Scaled)
        {
            filters.Add(
                $"scale={frame.Width.ToString(CultureInfo.InvariantCulture)}:{frame.Height.ToString(CultureInfo.InvariantCulture)}:flags=bicubic");
        }

        filters.Add("format=yuv420p,setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709");

        // 变速必须在 fps 之前：抽帧后的 setpts=N/FRAME_RATE/TB 会按固定帧率重排时间戳，放在 fps 之后的变速会被抹掉。
        if (Math.Abs(preset.SpeedFactor - 1) > 0.0001)
            filters.Add($"setpts=PTS/{Num(preset.SpeedFactor)}");

        filters.Add($"fps={preset.Fps.ToString(CultureInfo.InvariantCulture)}");

        if (preset.FrameDropPercent > 0)
            filters.Add(FrameDropFilter(preset.FrameDropPercent, seed));

        return string.Join(",", filters);
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

    private static void AppendSingleAudio(List<string> args, VideoDedupClip clip, VideoDedupSlice slice, VideoDedupPreset preset)
    {
        if (!clip.Probe.HasAudio)
        {
            args.Add("-an");
            return;
        }

        var audio = BuildAudioFilter(clip, slice, preset);
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

    private static string BuildAudioFilter(VideoDedupClip clip, VideoDedupSlice slice, VideoDedupPreset preset)
    {
        var filters = new List<string>();
        var duration = Math.Max(0, clip.Probe.DurationSeconds);
        if (slice.StartSeconds > 0.001 || slice.EndSeconds < duration - 0.001)
            filters.Add($"atrim=start={Num(slice.StartSeconds)}:end={Num(slice.EndSeconds)},asetpts=PTS-STARTPTS");
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

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
