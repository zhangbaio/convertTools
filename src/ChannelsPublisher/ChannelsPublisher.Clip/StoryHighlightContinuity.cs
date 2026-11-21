namespace ChannelsPublisher.Clip;

internal sealed class StoryHighlightContinuity(
    IReadOnlyList<ClipSourceAnalysis> sources, IReadOnlyDictionary<string, HighlightCutTimeline> timelines)
{
    private static readonly string[] RebirthWishes = ["重生", "重来", "重活", "再活一次", "回到过去", "穿越"];
    private static readonly string[] RebirthConfirmations = ["真回来了", "真的回来了", "重生了", "穿越了", "回到过去了"];
    private static readonly string[] QuestionWords = ["是不是", "要不要", "能不能", "行不行", "可不可以", "怎么办", "怎么样", "对不对"];
    private static readonly string[] DecisionWords = ["我决定", "我想好了", "我已经想好了", "就这么定", "就这么办", "就这样定", "咱们先不", "我们先不", "不买了", "不去了", "不借了", "我答应", "我同意", "我拒绝", "don't buy", "i decided", "we decided", "let's not"];
    private const int MaxAnswerGapMs = 8000;
    private const int MaxAnswerLengthMs = 20000;

    private readonly ClipCandidate[] _transitions = FindTransitions(sources, timelines).ToArray();

    public IEnumerable<ClipCandidate> ContextCandidates(ClipCandidate clip)
    {
        foreach (var transition in _transitions.Where(transition => CanJoin(clip, transition)))
        {
            if (Extend(clip, transition.EndMs) is { } extended)
                yield return extended;
        }
        if (AnswerAfter(clip) is { } answer && Extend(clip, answer.EndMs) is { } closed)
            yield return closed;
    }

    public string Role(ClipCandidate clip)
    {
        var last = LastLine(clip);
        if (_transitions.Any(transition => Contains(clip, transition)))
            return "保留时空转场画面";
        if (EndsInQuestion(last))
            return "问题待回答";
        return HasDecision(last) ? "人物决定或答复" : "";
    }

    public List<ClipCandidate> Improve(List<ClipCandidate> clips, ClipTimeRange range,
        IReadOnlyList<List<ClipCandidate>> previous, double similarityCap, ClipEngineOptions options, Action<string>? log)
    {
        if (clips.Count == 0)
            return clips;
        foreach (var bridge in _transitions)
        {
            if (!clips.Any(clip => StartsBefore(clip, bridge)) || !clips.Any(clip => ComesAfter(clip, bridge)))
                continue;

            var withBridge = clips.ToList();
            if (!withBridge.Any(clip => Contains(clip, bridge)))
            {
                var adjacent = withBridge.FindIndex(clip => CanJoin(clip, bridge));
                if (adjacent >= 0 && Extend(withBridge[adjacent], bridge.EndMs) is { } extended)
                    withBridge[adjacent] = extended;
                else
                    withBridge.Add(bridge);
            }
            withBridge = withBridge.OrderBy(clip => clip.EpisodeIndex).ThenBy(clip => clip.StartMs).ToList();
            if (FitBudget(withBridge, range, previous, similarityCap, options, protectEnding: HasDecision(LastLine(withBridge[^1]))))
            {
                clips = withBridge;
                log?.Invoke($"剧情高光保留第 {bridge.EpisodeIndex} 集时空转场，按情节时间衔接前后片段。");
            }
        }

        if (AnswerAfter(clips[^1]) is { } reply && Extend(clips[^1], reply.EndMs) is { } resolved)
        {
            var withDecision = clips.ToList();
            withDecision[^1] = resolved;
            if (FitBudget(withDecision, range, previous, similarityCap, options, protectEnding: true))
            {
                log?.Invoke($"剧情高光接上第 {resolved.EpisodeIndex} 集问句后的决定或答复。");
                clips = withDecision;
            }
        }
        if (EndsInQuestion(LastLine(clips[^1])))
            log?.Invoke("当前时长和相似度限制下，未找到可接在结尾问句后的完整决定或答复。");
        return options.StoryVisualReview && !clips.Any(clip => clip.IsVisualOnly)
            ? ConnectVisualGap(clips, range, previous, similarityCap, options, log) : clips;
    }

    private List<ClipCandidate> ConnectVisualGap(List<ClipCandidate> clips, ClipTimeRange range,
        IReadOnlyList<List<ClipCandidate>> previous, double similarityCap, ClipEngineOptions options, Action<string>? log)
    {
        for (var index = 1; index < clips.Count; index++)
        {
            var left = clips[index - 1];
            var right = clips[index];
            var gap = right.StartMs - left.EndMs;
            if (left.VideoPath != right.VideoPath || gap is < 1500 or > 8000)
                continue;
            var source = sources.FirstOrDefault(item => item.Source.VideoPath == left.VideoPath);
            if (source is null)
                continue;
            foreach (var visual in source.Candidates.Where(clip => clip.IsVisualOnly && clip.VisualDescription.Length > 0 &&
                         clip.StartMs >= left.EndMs && clip.EndMs <= right.StartMs).OrderByDescending(clip => clip.Total))
            {
                var connected = clips.ToList();
                connected.Insert(index, visual);
                if (!FitBudget(connected, range, previous, similarityCap, options, protectEnding: true,
                        required: [left, visual, right]))
                    continue;
                log?.Invoke($"剧情高光补上第 {visual.EpisodeIndex} 集两段对白之间的少对白镜头。{visual.VisualDescription}");
                return connected;
            }
        }
        return clips;
    }

    private bool FitBudget(List<ClipCandidate> clips, ClipTimeRange range,
        IReadOnlyList<List<ClipCandidate>> previous, double similarityCap, ClipEngineOptions options, bool protectEnding,
        IReadOnlyList<ClipCandidate>? required = null)
    {
        var minimum = range.MinSeconds * 1000;
        var maximum = range.MaxSeconds * 1000;
        var minimumPiece = Math.Min(minimum, options.SmoothSelection ? options.MinimumSegmentMs : 200);
        var length = clips.Sum(clip => clip.DurationMs);
        foreach (var clip in clips.OrderBy(clip => clip.Total).ToArray())
        {
            if (length <= maximum)
                break;
            if ((protectEnding && ReferenceEquals(clip, clips[^1])) || IsOnlyContextForTransition(clip, clips) ||
                required?.Contains(clip) == true)
                continue;
            var canRemove = length - clip.DurationMs >= minimum;
            if (canRemove && required is null)
            {
                clips.Remove(clip);
                length -= clip.DurationMs;
                continue;
            }
            var maxPiece = clip.DurationMs - (length - maximum);
            var minPiece = Math.Max(minimumPiece, clip.DurationMs - (length - minimum));
            var timeline = timelines[clip.VideoPath];
            if (maxPiece >= minPiece && timeline.Fit(clip.StartMs, clip.EndMs, maxPiece, maxPiece, minPiece) is { } safe)
            {
                clips[clips.IndexOf(clip)] = ConfiguredClipPlanner.Part(clip, safe.Start, safe.End, timeline.Dialogue);
                length += safe.End - safe.Start - clip.DurationMs;
            }
            else if (canRemove)
            {
                clips.Remove(clip);
                length -= clip.DurationMs;
            }
        }
        return StoryHighlightPlanner.FitsConstraints(clips, range, previous, similarityCap);
    }

    private bool IsOnlyContextForTransition(ClipCandidate clip, IReadOnlyList<ClipCandidate> clips) =>
        _transitions.Any(bridge => Contains(clip, bridge) || (clips.Any(other => Contains(other, bridge)) &&
            ((ComesBefore(clip, bridge) && clips.Count(other => ComesBefore(other, bridge)) == 1) ||
             (ComesAfter(clip, bridge) && clips.Count(other => ComesAfter(other, bridge)) == 1))));

    private SubtitleSegment? AnswerAfter(ClipCandidate clip)
    {
        if (!EndsInQuestion(LastLine(clip)))
            return null;
        return timelines[clip.VideoPath].Dialogue.FirstOrDefault(line => line.EndMs > clip.EndMs &&
            line.StartMs >= clip.EndMs && line.StartMs - clip.EndMs <= MaxAnswerGapMs &&
            line.EndMs - clip.EndMs <= MaxAnswerLengthMs && HasDecision(line.Text) && !EndsInQuestion(line.Text));
    }

    private string LastLine(ClipCandidate clip) => timelines[clip.VideoPath].Dialogue.LastOrDefault(line =>
        line.StartMs >= clip.StartMs && line.EndMs <= clip.EndMs)?.Text ?? clip.Text;

    private ClipCandidate? Extend(ClipCandidate clip, int endMs)
    {
        var timeline = timelines[clip.VideoPath];
        var combined = ConfiguredClipPlanner.Part(clip, clip.StartMs, endMs);
        return timeline.Cover(combined) is { } bounds && bounds.End >= endMs
            ? ConfiguredClipPlanner.Part(clip, bounds.Start, bounds.End, timeline.Dialogue) : null;
    }

    private static IEnumerable<ClipCandidate> FindTransitions(IReadOnlyList<ClipSourceAnalysis> sources,
        IReadOnlyDictionary<string, HighlightCutTimeline> timelines)
    {
        foreach (var source in sources)
        {
            foreach (var line in source.Dialogue.Where(line => RebirthWishes.Any(word => line.Text.Contains(word, StringComparison.Ordinal))))
            {
                if (!source.Dialogue.Any(confirm => confirm.StartMs >= line.EndMs && confirm.StartMs - line.EndMs <= 22000 &&
                    RebirthConfirmations.Any(word => confirm.Text.Contains(word, StringComparison.Ordinal))))
                    continue;
                var nextShot = source.ShotCuts.FirstOrDefault(shot => shot >= line.EndMs && shot - line.EndMs <= 6000);
                if (nextShot == 0)
                    continue;

                // Include the reveal after the confirming cut, including any title card before the next line.
                var end = Math.Min(source.Media.DurationMs, Math.Max(line.EndMs + 4500, nextShot + 1000));
                var probe = new ClipCandidate { VideoPath = source.Source.VideoPath, EpisodeIndex = source.Source.EpisodeIndex,
                    StartMs = line.EndMs, EndMs = end };
                if (timelines[source.Source.VideoPath].Cover(probe) is { } safe && safe.End - safe.Start >= 1500)
                    yield return ConfiguredClipPlanner.Part(probe, safe.Start, safe.End, source.Dialogue);
            }
        }
    }

    private static bool Contains(ClipCandidate clip, ClipCandidate bridge) => clip.VideoPath == bridge.VideoPath &&
        clip.StartMs <= bridge.StartMs && clip.EndMs >= bridge.EndMs;

    private static bool CanJoin(ClipCandidate clip, ClipCandidate bridge) => clip.VideoPath == bridge.VideoPath &&
        clip.StartMs < bridge.StartMs && Math.Abs(clip.EndMs - bridge.StartMs) <= 500;

    private static bool StartsBefore(ClipCandidate clip, ClipCandidate bridge) => clip.EpisodeIndex < bridge.EpisodeIndex ||
        (clip.VideoPath == bridge.VideoPath && clip.StartMs < bridge.StartMs);

    private static bool ComesBefore(ClipCandidate clip, ClipCandidate bridge) =>
        StartsBefore(clip, bridge) && !Contains(clip, bridge);

    private static bool ComesAfter(ClipCandidate clip, ClipCandidate bridge) => clip.EpisodeIndex > bridge.EpisodeIndex ||
        (clip.VideoPath == bridge.VideoPath && clip.StartMs >= bridge.EndMs);

    private static bool HasDecision(string text) => DecisionWords.Any(word =>
        Tail(text, 40).Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool EndsInQuestion(string text)
    {
        var tail = Tail(text.TrimEnd(), 26);
        if (tail.EndsWith('?') || tail.EndsWith('？') || tail.EndsWith('吗') || tail.EndsWith('呢'))
            return true;
        if (HasDecision(tail))
            return false;
        return QuestionWords.Any(word => tail.Contains(word, StringComparison.Ordinal));
    }

    private static string Tail(string text, int length) => text[Math.Max(0, text.Length - length)..];
}
