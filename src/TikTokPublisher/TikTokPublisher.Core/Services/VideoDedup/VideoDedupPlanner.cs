using System.Globalization;
using System.Text;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services.VideoDedup;

internal sealed record VideoDedupProbe(double DurationSeconds, int Width, int Height, bool HasAudio);

internal sealed record VideoDedupClip(string Path, VideoDedupProbe Probe);

internal static class VideoDedupPlanner
{
    public static IReadOnlyList<IReadOnlyList<int>> Group(IReadOnlyList<double> rawDurations, VideoDedupPreset preset)
    {
        var count = rawDurations.Count;
        if (count == 0)
            return [];

        var groups = new List<List<int>>();
        if (!preset.HasDurationLimit)
        {
            for (var i = 0; i < count; i++)
                groups.Add([i]);
            return groups;
        }

        var minimum = preset.MinimumDurationSeconds;
        var maximum = preset.MaximumDurationSeconds;
        var current = new List<int>();
        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            var duration = Math.Max(0, rawDurations[i]);
            var alreadyEnough = minimum <= 0 || sum >= minimum;
            var wouldExceed = maximum <= 0 || sum + duration > maximum;
            if (current.Count > 0 && alreadyEnough && wouldExceed)
            {
                groups.Add(current);
                current = [];
                sum = 0;
            }

            current.Add(i);
            sum += duration;
        }

        if (current.Count > 0)
            groups.Add(current);

        if (minimum > 0 && groups.Count > 1)
        {
            var last = groups[^1];
            var lastProcessed = last.Sum(index => ProcessedDuration(rawDurations[index], preset));
            if (lastProcessed < minimum)
            {
                groups[^2].AddRange(last);
                groups.RemoveAt(groups.Count - 1);
            }
        }

        return groups;
    }

    public static double ProcessedDuration(double rawSeconds, VideoDedupPreset preset)
    {
        var trimmed = Math.Max(0.2, rawSeconds - preset.TrimStartSeconds - preset.TrimEndSeconds);
        var speed = preset.SpeedFactor <= 0 ? 1 : preset.SpeedFactor;
        var keep = preset.FrameDropPercent <= 0
            ? 1
            : Math.Max(0.5, 1 - preset.FrameDropPercent / 100d);
        return trimmed / speed * keep;
    }

    public static IReadOnlyList<string> BuildArguments(
        VideoDedupPreset preset,
        IReadOnlyList<VideoDedupClip> clips,
        string outputPath,
        string videoEncoder,
        int seed)
    {
        if (clips.Count == 0)
            throw new InvalidOperationException("没有可编码的视频。");

        VideoDedupEffectFilters.Validate(preset);
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
        foreach (var clip in clips)
            args.AddRange(["-i", clip.Path]);
        foreach (var image in VideoDedupEffectFilters.ImageInputs(preset))
            args.AddRange(["-i", image]);

        var useComplex = clips.Count > 1;
        var transitions = useComplex && CanTransition(preset, clips);
        if (useComplex)
        {
            var graph = new StringBuilder(BuildFilterComplex(preset, clips, transitions, seed));
            var label = VideoDedupEffectFilters.Finish(graph, "vout", preset, clips.Count);
            args.AddRange(["-filter_complex", graph.ToString()]);
            args.AddRange(["-map", $"[{label}]"]);
            if (clips.All(clip => clip.Probe.HasAudio))
                args.AddRange(["-map", "[aout]"]);
            else
                args.Add("-an");
        }
        else if (!VideoDedupEffectFilters.NeedsGraph(preset))
        {
            var video = CombineFilters(BuildVideoFilter(clips[0], preset, seed), VideoDedupEffectFilters.LinearFilters(preset));
            if (video.Length > 0)
                args.AddRange(["-vf", video]);
            AppendSingleAudio(args, clips[0], preset);
        }
        else
        {
            var graph = new StringBuilder();
            graph.Append("[0:v]").Append(BuildVideoFilter(clips[0], preset, seed)).Append("[vgeo]");
            var label = VideoDedupEffectFilters.Finish(graph, "vgeo", preset, 1);
            args.AddRange(["-filter_complex", graph.ToString(), "-map", $"[{label}]"]);
            if (clips[0].Probe.HasAudio)
            {
                args.AddRange(["-map", "0:a"]);
                var audio = BuildAudioFilter(clips[0], preset);
                if (audio.Length > 0)
                    args.AddRange(["-af", audio]);
            }
            else
            {
                args.Add("-an");
            }
        }

        var includeAudio = clips.Count == 1
            ? clips[0].Probe.HasAudio
            : clips.All(clip => clip.Probe.HasAudio);
        AppendEncoder(args, preset, videoEncoder, includeAudio);
        args.AddRange(["-movflags", "+faststart", outputPath]);
        return args;
    }

    public static bool CanTransition(VideoDedupPreset preset, IReadOnlyList<VideoDedupClip> clips)
    {
        if (!preset.TransitionsEnabled || !preset.HasAnyTransition || clips.Count < 2)
            return false;
        return clips.All(clip => ProcessedDuration(clip.Probe.DurationSeconds, preset) >= 0.8);
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

        var copy = CanCopyAudio(preset);
        if (copy)
        {
            args.AddRange(["-c:a", "copy"]);
            return;
        }

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
        args.AddRange(["-preset", "p4", "-gpu", "any", "-profile:v", "high", "-rc-lookahead", "8", "-bf", "3", "-b_ref_mode", "middle"]);
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
        args.AddRange(["-usage", "transcoding", "-quality", "balanced", "-profile:v", "high"]);
        if (IsCrf(preset))
            args.AddRange(["-rc", "cqp", "-qp_i", crf, "-qp_p", crf, "-qp_b", crf]);
        else if (IsCbr(preset))
            args.AddRange(["-rc", "cbr", "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", buffer, "-filler_data", "1"]);
        else
            args.AddRange(["-rc", "vbr_peak", "-b:v", bitrate, "-maxrate", MaxRateToken(preset), "-bufsize", buffer]);
    }

    private static void AppendQsv(List<string> args, VideoDedupPreset preset)
    {
        args.AddRange(["-preset", "veryfast"]);
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
        args.AddRange(["-preset", hevc ? "fast" : "veryfast"]);
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

    internal static bool CanCopyAudio(VideoDedupPreset preset) =>
        string.Equals(preset.AudioCodec, VideoDedupPreset.AudioDefault, StringComparison.Ordinal) &&
        preset.TrimStartSeconds <= 0 &&
        preset.TrimEndSeconds <= 0 &&
        Math.Abs(preset.SpeedFactor - 1) < 0.0001 &&
        preset.FrameDropPercent <= 0 &&
        Math.Abs(preset.AudioLowGainDb) < 0.01 &&
        Math.Abs(preset.AudioMidGainDb) < 0.01 &&
        Math.Abs(preset.AudioHighGainDb) < 0.01;

    private static string BuildFilterComplex(
        VideoDedupPreset preset,
        IReadOnlyList<VideoDedupClip> clips,
        bool transitions,
        int seed)
    {
        var graph = new StringBuilder();
        for (var i = 0; i < clips.Count; i++)
        {
            var video = BuildVideoFilter(clips[i], preset, seed + i * 17);
            if (video.Length == 0)
                video = "setpts=PTS-STARTPTS";
            graph.Append($"[{i.ToString(CultureInfo.InvariantCulture)}:v]{video},format=yuv420p,settb=AVTB[v{i.ToString(CultureInfo.InvariantCulture)}];");
            if (clips[i].Probe.HasAudio)
            {
                var audio = BuildAudioFilter(clips[i], preset);
                if (audio.Length == 0)
                    audio = "asetpts=PTS-STARTPTS";
                graph.Append($"[{i.ToString(CultureInfo.InvariantCulture)}:a]{audio}[a{i.ToString(CultureInfo.InvariantCulture)}];");
            }
        }

        if (!transitions)
        {
            for (var i = 0; i < clips.Count; i++)
                graph.Append($"[v{i.ToString(CultureInfo.InvariantCulture)}]");
            graph.Append($"concat=n={clips.Count.ToString(CultureInfo.InvariantCulture)}:v=1:a=0[vout]");
            if (clips.All(clip => clip.Probe.HasAudio))
            {
                graph.Append(';');
                for (var i = 0; i < clips.Count; i++)
                    graph.Append($"[a{i.ToString(CultureInfo.InvariantCulture)}]");
                graph.Append($"concat=n={clips.Count.ToString(CultureInfo.InvariantCulture)}:v=0:a=1[aout]");
            }

            return graph.ToString();
        }

        var videoLabel = "v0";
        var audioLabel = "a0";
        var hasAudio = clips.All(clip => clip.Probe.HasAudio);
        double elapsed = ProcessedDuration(clips[0].Probe.DurationSeconds, preset);
        for (var i = 1; i < clips.Count; i++)
        {
            if (graph.Length > 0 && graph[^1] != ';')
                graph.Append(';');
            var junction = Hash(clips[i - 1].Path, clips[i].Path, seed + i);
            var duration = TransitionSeconds(junction);
            var name = PickTransition(preset, junction);
            var offset = Math.Max(0, elapsed - duration);
            var nextVideo = i == clips.Count - 1 ? "vout" : $"vx{i.ToString(CultureInfo.InvariantCulture)}";
            graph.Append(
                $"[{videoLabel}][v{i.ToString(CultureInfo.InvariantCulture)}]xfade=transition={name}:duration={Num(duration)}:offset={Num(offset)}[{nextVideo}]");
            videoLabel = nextVideo;
            if (hasAudio)
            {
                var nextAudio = i == clips.Count - 1 ? "aout" : $"ax{i.ToString(CultureInfo.InvariantCulture)}";
                graph.Append(
                    $";[{audioLabel}][a{i.ToString(CultureInfo.InvariantCulture)}]acrossfade=d={Num(duration)}:c1=tri:c2=tri[{nextAudio}]");
                audioLabel = nextAudio;
            }

            elapsed += ProcessedDuration(clips[i].Probe.DurationSeconds, preset) - duration;
        }

        return graph.ToString().TrimEnd(';');
    }

    private static string BuildVideoFilter(VideoDedupClip clip, VideoDedupPreset preset, int seed)
    {
        var filters = new List<string>();
        var end = Math.Max(preset.TrimStartSeconds + 0.2, clip.Probe.DurationSeconds - preset.TrimEndSeconds);
        if (preset.TrimStartSeconds > 0 || preset.TrimEndSeconds > 0)
            filters.Add($"trim=start={Num(preset.TrimStartSeconds)}:end={Num(end)},setpts=PTS-STARTPTS");

        if (preset.CropTopPercent + preset.CropBottomPercent + preset.CropLeftPercent + preset.CropRightPercent > 0)
        {
            var width = 1 - (preset.CropLeftPercent + preset.CropRightPercent) / 100d;
            var height = 1 - (preset.CropTopPercent + preset.CropBottomPercent) / 100d;
            filters.Add(
                $"crop=w=iw*{Num(width)}:h=ih*{Num(height)}:x=iw*{Num(preset.CropLeftPercent / 100d)}:y=ih*{Num(preset.CropTopPercent / 100d)}");
        }

        if (!preset.KeepsOriginalResolution)
        {
            var portrait = clip.Probe.Width <= 0 || clip.Probe.Height <= 0 || clip.Probe.Height >= clip.Probe.Width;
            var targetWidth = portrait ? preset.PortraitWidth : preset.PortraitHeight;
            var targetHeight = portrait ? preset.PortraitHeight : preset.PortraitWidth;
            filters.Add(
                $"scale={targetWidth.ToString(CultureInfo.InvariantCulture)}:{targetHeight.ToString(CultureInfo.InvariantCulture)}:force_original_aspect_ratio=increase,crop={targetWidth.ToString(CultureInfo.InvariantCulture)}:{targetHeight.ToString(CultureInfo.InvariantCulture)}");
        }

        filters.Add($"fps={preset.Fps.ToString(CultureInfo.InvariantCulture)}");

        if (Math.Abs(preset.SpeedFactor - 1) > 0.0001)
            filters.Add($"setpts=PTS/{Num(preset.SpeedFactor)}");

        if (preset.FrameDropPercent > 0)
        {
            var interval = Math.Max(2, (int)Math.Round(100d / preset.FrameDropPercent));
            var dropAt = Math.Abs(seed) % interval;
            filters.Add(
                $"select='not(eq(mod(n+{dropAt.ToString(CultureInfo.InvariantCulture)}\\,{interval.ToString(CultureInfo.InvariantCulture)})\\,0))',setpts=N/FRAME_RATE/TB");
        }

        return string.Join(",", filters);
    }

    private static void AppendSingleAudio(List<string> args, VideoDedupClip clip, VideoDedupPreset preset)
    {
        if (!clip.Probe.HasAudio)
        {
            args.Add("-an");
            return;
        }

        var audio = BuildAudioFilter(clip, preset);
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

    private static string BuildAudioFilter(VideoDedupClip clip, VideoDedupPreset preset)
    {
        var filters = new List<string>();
        var end = Math.Max(preset.TrimStartSeconds + 0.2, clip.Probe.DurationSeconds - preset.TrimEndSeconds);
        if (preset.TrimStartSeconds > 0 || preset.TrimEndSeconds > 0)
            filters.Add($"atrim=start={Num(preset.TrimStartSeconds)}:end={Num(end)},asetpts=PTS-STARTPTS");

        var tempo = preset.SpeedFactor <= 0 ? 1 : preset.SpeedFactor;
        if (preset.FrameDropPercent > 0)
        {
            var interval = Math.Max(2, (int)Math.Round(100d / preset.FrameDropPercent));
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
