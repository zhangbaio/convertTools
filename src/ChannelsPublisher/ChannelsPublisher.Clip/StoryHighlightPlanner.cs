namespace ChannelsPublisher.Clip;

// Cross-episode context follows DramaClip v0.2.5 (MIT):
// https://github.com/efarsoft/DramaClip/blob/v0.2.5/service/dramaclip/engines/narration/angles.py
internal static class StoryHighlightPlanner
{
    private const int MaxCandidates = 96;

    public static List<ClipCandidate> Candidates(
        IReadOnlyList<ClipSourceAnalysis> sources, IReadOnlyDictionary<string, HighlightCutTimeline> timelines,
        ClipTimeRange range, IReadOnlyList<List<ClipCandidate>> previous, ClipEngineOptions options,
        StoryHighlightContinuity? continuity = null)
    {
        var minimumSegment = Math.Min(range.MinSeconds * 1000, options.SmoothSelection ? options.MinimumSegmentMs : 200);
        var maximum = range.MaxSeconds * 1000;
        var episodes = new List<List<ClipCandidate>>();
        foreach (var source in sources.OrderBy(source => source.Source.EpisodeIndex))
        {
            var timeline = timelines[source.Source.VideoPath];
            var clips = new List<ClipCandidate>();
            foreach (var candidate in source.Candidates.OrderBy(candidate => candidate.StartMs))
            {
                if (timeline.Cover(candidate) is not { } bounds || bounds.End - bounds.Start < minimumSegment ||
                    bounds.End - bounds.Start > maximum)
                    continue;
                var safe = ConfiguredClipPlanner.Part(candidate, bounds.Start, bounds.End, timeline.Dialogue);
                clips.Add(safe);
                if (continuity is not null)
                    clips.AddRange(continuity.ContextCandidates(safe).Where(clip => clip.DurationMs <= maximum));
            }
            if (source.HardSubtitles is { Count: > 0 } hard)
            {
                foreach (var clip in clips)
                    clip.HardSubtitleText = string.Join(" / ", hard.Where(cue => cue.StartMs >= clip.StartMs && cue.EndMs <= clip.EndMs)
                        .Select(cue => cue.Text).Distinct());
            }
            if (clips.Count > 0)
                episodes.Add(clips);
        }

        var anchors = episodes.SelectMany((clips, episode) => clips.Select((clip, index) => (clip, episode, index))
            .OrderByDescending(item => item.clip.Total).Take(4))
            .OrderBy(item => PreviousOverlap(item.clip, previous) > 0)
            .ThenByDescending(item => item.clip.Total).ToArray();
        var pool = new List<ClipCandidate>();
        var included = new HashSet<(string Path, int Start, int End)>();
        if (continuity is not null)
        {
            foreach (var clip in episodes.SelectMany(clips => clips).Where(clip => continuity.Role(clip) is "保留时空转场画面" or "人物决定或答复")
                .OrderBy(clip => PreviousOverlap(clip, previous) > 0).ThenByDescending(clip => clip.Total).Take(MaxCandidates / 3))
                Add(clip);
        }
        foreach (var clip in episodes.SelectMany(clips => clips).Where(clip => clip.IsVisualOnly && clip.VisualDescription.Length > 0)
            .OrderBy(clip => PreviousOverlap(clip, previous) > 0).ThenByDescending(clip => clip.Total).Take(18))
            Add(clip);
        foreach (var (clip, episode, index) in anchors)
        {
            Add(clip);
            var current = episodes[episode];
            if (index > 0) Add(current[index - 1]);
            if (index + 1 < current.Count) Add(current[index + 1]);
            if (episode > 0) Add(episodes[episode - 1][^1]);
            if (episode + 1 < episodes.Count) Add(episodes[episode + 1][0]);
            if (pool.Count >= MaxCandidates)
                break;
        }
        if (pool.Count < MaxCandidates)
        {
            foreach (var clip in episodes.SelectMany(clips => clips).OrderByDescending(clip => clip.Total))
            {
                Add(clip);
                if (pool.Count >= MaxCandidates) break;
            }
        }
        return pool.OrderBy(clip => clip.EpisodeIndex).ThenBy(clip => clip.StartMs).ToList();

        void Add(ClipCandidate clip)
        {
            if (pool.Count < MaxCandidates && included.Add((clip.VideoPath, clip.StartMs, clip.EndMs)))
                pool.Add(clip);
        }
    }

    public static List<ClipCandidate> Validate(
        IReadOnlyList<int> order, IReadOnlyList<ClipCandidate> candidates, ClipTimeRange range,
        IReadOnlyList<List<ClipCandidate>> previous, double similarityCap,
        Func<List<ClipCandidate>, List<ClipCandidate>>? improve = null)
    {
        if (order.Count == 0 || order.Count > candidates.Count || order.Distinct().Count() != order.Count ||
            order.Any(id => id < 1 || id > candidates.Count))
            throw new InvalidDataException("AI 返回了重复或不存在的候选编号。");

        var selected = order.Select(id => candidates[id - 1]).ToList();
        if (improve is not null)
            selected = improve(selected);
        if (ConstraintViolation(selected, range, previous, similarityCap) is { } reason)
            throw new InvalidDataException(reason);
        return selected;
    }

    internal static bool FitsConstraints(IReadOnlyList<ClipCandidate> selected, ClipTimeRange range,
        IReadOnlyList<List<ClipCandidate>> previous, double similarityCap) =>
        ConstraintViolation(selected, range, previous, similarityCap) is null;

    internal static List<ClipCandidate> MergeVisualOverlap(List<ClipCandidate> selected)
    {
        foreach (var visual in selected.Where(clip => clip.IsVisualOnly).ToArray())
        {
            var index = selected.FindIndex(clip => !clip.IsVisualOnly && ConfiguredClipPlanner.Overlap(clip, visual) > 0);
            if (index < 0)
                continue;
            var existing = selected[index];
            if (existing.StartMs <= visual.StartMs && existing.EndMs >= visual.EndMs && existing.VisualDescription.Length == 0)
            {
                var covering = ConfiguredClipPlanner.Part(existing, existing.StartMs, existing.EndMs);
                covering.VisualDescription = visual.VisualDescription;
                selected[index] = covering;
            }
            selected.Remove(visual);
        }
        return selected;
    }

    private static string? ConstraintViolation(IReadOnlyList<ClipCandidate> selected, ClipTimeRange range,
        IReadOnlyList<List<ClipCandidate>> previous, double similarityCap)
    {
        for (var index = 0; index < selected.Count; index++)
            if (selected.Take(index).Any(other => ConfiguredClipPlanner.Overlap(selected[index], other) > 0))
                return "方案包含互相重叠的片段。";
        var length = selected.Sum(clip => clip.DurationMs);
        if (length < range.MinSeconds * 1000 || length > range.MaxSeconds * 1000)
            return $"方案的总时长超出 {range.MinSeconds}–{range.MaxSeconds} 秒范围。";
        if (previous.Any(prior => selected.Sum(clip => prior.Sum(other => ConfiguredClipPlanner.Overlap(clip, other))) >
                Math.Min(length, prior.Sum(clip => clip.DurationMs)) * similarityCap))
            return "方案与已有高光的重叠超过相似度上限。";
        return null;
    }

    public static int PreviousOverlap(ClipCandidate clip, IReadOnlyList<List<ClipCandidate>> previous) =>
        previous.Select(prior => prior.Sum(other => ConfiguredClipPlanner.Overlap(clip, other))).DefaultIfEmpty(0).Max();
}
