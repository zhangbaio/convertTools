namespace ChannelsPublisher.Clip;

internal static class ConfiguredClipPlanner
{
    public static List<ClipCandidate> Plan(
        ClipModeOptions mode, ClipTimeRange range, IReadOnlyList<ClipSourceAnalysis> sources,
        IReadOnlyList<List<ClipCandidate>> previous, ClipEngineOptions options)
    {
        var minimum = range.MinSeconds * 1000;
        var maximum = range.MaxSeconds * 1000;
        var target = minimum + (maximum - minimum) / 2;
        if (mode.Mode == "slice")
            return PlanSlice(sources, previous, minimum, target, mode.SimilarityCap);

        var eligible = mode.Mode == "commentary" ? sources.Where(source => source.HasDialogue) : sources;
        var all = eligible.SelectMany(source => source.Candidates).ToList();
        var durations = sources.ToDictionary(source => source.Source.EpisodeIndex, source => source.Media.DurationMs / 1000d);
        var ranked = mode.Mode switch
        {
            "mashup" => Mashup.Select(all, null).Concat(all.OrderByDescending(Mashup.DramaScore)).Distinct(),
            "commentary" when options.EpisodeQuota => Commentary.Select(all, durations, null)
                .Concat(all.OrderByDescending(clip => clip.Total)).Distinct(),
            "highlight" when options.EpisodeQuota => HighlightPlanner.Select(all, durations)
                .Concat(all.OrderByDescending(clip => clip.Total)).Distinct(),
            _ => all.OrderByDescending(clip => clip.Total),
        };

        var rankedClips = ranked.ToArray();
        var minimumSegment = options.SmoothSelection ? options.MinimumSegmentMs : 200;
        var chosen = Fill(rankedClips, previous, minimum, target, maximum, minimumSegment, mode.SimilarityCap);
        if (chosen.Count == 0 && minimumSegment > minimum)
            chosen = Fill(rankedClips, previous, minimum, target, maximum, minimum, mode.SimilarityCap);
        if (mode.Mode == "mashup")
            return Mashup.ArrangeNarrativeArc(chosen);
        if (mode.Mode == "commentary")
            return chosen.OrderBy(c => c.EpisodeIndex).ThenBy(c => c.StartMs).ToList();
        return chosen.OrderByDescending(c => c.HookScore).ToList();
    }

    private static List<ClipCandidate> Fill(
        IReadOnlyList<ClipCandidate> ranked, IReadOnlyList<List<ClipCandidate>> previous, int minimum, int target,
        int maximum, int minimumSegment, double similarityCap)
    {
        var selected = new List<ClipCandidate>();
        var priorLengths = previous.Select(bin => bin.Sum(c => c.DurationMs)).ToArray();
        var priorOverlaps = new int[previous.Count];
        var length = 0;
        var used = previous.SelectMany(c => c).ToArray();
        foreach (var allowReuse in new[] { false, true })
        {
            if (allowReuse && similarityCap == 0)
                break;
            foreach (var candidate in ranked)
            {
                if (length >= target || (length >= minimum && target - length < minimumSegment))
                    break;

                var blocked = allowReuse ? selected : selected.Concat(used);
                foreach (var (start, end) in FreeIntervals(candidate, blocked))
                {
                    var remaining = Math.Min(maximum - length, Math.Max(minimumSegment, target - length));
                    var duration = Math.Min(remaining, end - start);
                    if (length + duration < minimum && remaining >= 2 * minimumSegment && duration > remaining - minimumSegment)
                        duration = remaining - minimumSegment;
                    if (duration < minimumSegment)
                        continue;

                    var segment = Part(candidate, start, start + duration);
                    var overlaps = previous.Select(bin => bin.Sum(c => Overlap(segment, c))).ToArray();
                    if (overlaps.Where((overlap, i) => priorOverlaps[i] + overlap >
                            Math.Min(priorLengths[i], target) * similarityCap).Any())
                        continue;

                    for (var i = 0; i < priorOverlaps.Length; i++)
                        priorOverlaps[i] += overlaps[i];
                    selected.Add(segment);
                    length += duration;
                    if (length >= target || (length >= minimum && target - length < minimumSegment))
                        break;
                }
            }
            if (length >= minimum && (length >= target || target - length < minimumSegment))
                break;
        }
        return length >= minimum && priorOverlaps.Select((overlap, i) =>
            overlap <= Math.Min(length, priorLengths[i]) * similarityCap).All(fits => fits) ? selected : [];
    }

    private static IEnumerable<(int Start, int End)> FreeIntervals(ClipCandidate segment, IEnumerable<ClipCandidate> blocked)
    {
        var cursor = segment.StartMs;
        foreach (var other in blocked.Where(c => Overlap(segment, c) > 0).OrderBy(c => c.StartMs).ToArray())
        {
            if (other.StartMs > cursor)
                yield return (cursor, other.StartMs);
            cursor = Math.Max(cursor, other.EndMs);
        }
        if (cursor < segment.EndMs)
            yield return (cursor, segment.EndMs);
    }

    private static List<ClipCandidate> PlanSlice(
        IReadOnlyList<ClipSourceAnalysis> sources, IReadOnlyList<List<ClipCandidate>> previous, int minimum, int target, double cap)
    {
        var choices = new List<(ClipCandidate Clip, double Score)>();
        foreach (var source in sources.Where(source => source.Media.DurationMs >= minimum))
        {
            var candidates = source.Candidates;
            var past = previous.SelectMany(bin => bin).Where(c => c.VideoPath == source.Source.VideoPath);
            foreach (var duration in new[] { Math.Min(target, source.Media.DurationMs), minimum }.Distinct())
            {
                var lastStart = source.Media.DurationMs - duration;
                var starts = new[] { 0, lastStart }.Concat(candidates.Select(c => c.StartMs))
                    .Concat(past.SelectMany(c => new[] { c.StartMs - duration, c.EndMs,
                        c.StartMs - duration + (int)(Math.Min(c.DurationMs, duration) * cap),
                        c.EndMs - (int)(Math.Min(c.DurationMs, duration) * cap) }))
                    .Select(start => Math.Clamp(start, 0, lastStart)).Distinct();
                foreach (var start in starts)
                {
                    var slice = new ClipCandidate { VideoPath = source.Source.VideoPath, EpisodeIndex = source.Source.EpisodeIndex,
                        StartMs = start, EndMs = start + duration };
                    if (previous.Any(bin => bin.Sum(c => Overlap(slice, c)) > Math.Min(bin.Sum(c => c.DurationMs), duration) * cap))
                        continue;
                    var inside = candidates.Where(c => Overlap(slice, c) > 0).ToArray();
                    var best = inside.OrderByDescending(c => c.Total).FirstOrDefault();
                    if (best is not null)
                    {
                        slice = Part(best, slice.StartMs, slice.EndMs);
                        slice.Text = string.Join(" ", inside.Select(c => c.Text));
                    }
                    var score = inside.Sum(c => (c.Total + 1) * Overlap(slice, c)) / (double)target;
                    choices.Add((slice, score));
                }
            }
        }
        var selection = choices.OrderByDescending(choice => choice.Score).ThenByDescending(choice => choice.Clip.DurationMs)
            .ThenBy(choice => choice.Clip.EpisodeIndex).ThenBy(choice => choice.Clip.StartMs).FirstOrDefault();
        return selection.Clip is null ? [] : [selection.Clip];
    }

    private static int Overlap(ClipCandidate a, ClipCandidate b) => a.VideoPath == b.VideoPath
        ? Math.Max(0, Math.Min(a.EndMs, b.EndMs) - Math.Max(a.StartMs, b.StartMs)) : 0;

    private static ClipCandidate Part(ClipCandidate clip, int start, int end) => new()
    {
        VideoPath = clip.VideoPath,
        EpisodeIndex = clip.EpisodeIndex,
        StartMs = start,
        EndMs = end,
        Text = clip.Text,
        Total = clip.Total,
        Emotion = clip.Emotion,
        Conflict = clip.Conflict,
        Cliffhanger = clip.Cliffhanger,
        Twist = clip.Twist,
        AudioEnergy = clip.AudioEnergy,
        ShotDensity = clip.ShotDensity,
        Title = clip.Title,
        Summary = clip.Summary,
        RecommendReason = clip.RecommendReason,
        Tags = clip.Tags,
    };
}
