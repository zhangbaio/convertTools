namespace ChannelsPublisher.Clip;

/// <summary>字幕 → 候选片段（时间窗口化）+ 四维关键词打分。移植自 material_clip/candidates.py。</summary>
public static class CandidateBuilder
{
    private const int GapThresholdMs = 2200;     // 相邻字幕最大间隔
    private const int MaxGroupMs = 14000;        // 单候选最大时长
    private const int MinPunctMs = 5000;         // 句末分割最小窗口
    private const int MinCandidateMs = 4000;     // 候选存活下限
    private static readonly char[] SentenceEnds = { '。', '！', '!', '？', '?', '…' };

    public static List<ClipCandidate> Build(EpisodeSource source, IReadOnlyList<SubtitleSegment> segs, int sourceDurationMs)
    {
        var candidates = new List<ClipCandidate>();
        var group = new List<SubtitleSegment>();

        void Flush(List<SubtitleSegment> g)
        {
            if (g.Count == 0) return;
            int start = g[0].StartMs, end = g[^1].EndMs;
            if (end - start < MinCandidateMs) return;
            var text = string.Concat(g.Select(s => s.Text));
            candidates.Add(Score(
                new ClipCandidate { EpisodeIndex = source.EpisodeIndex, VideoPath = source.VideoPath, StartMs = start, EndMs = end, Text = text },
                sourceDurationMs));
        }

        foreach (var seg in segs)
        {
            if (group.Count == 0) { group.Add(seg); continue; }
            var prev = group[^1];
            int gap = Math.Max(0, seg.StartMs - prev.EndMs);
            int groupDur = seg.EndMs - group[0].StartMs;
            bool prevSentenceEnd = !string.IsNullOrEmpty(prev.Text) && SentenceEnds.Contains(prev.Text[^1]);
            group.Add(seg);

            if (gap > GapThresholdMs || groupDur >= MaxGroupMs || (groupDur >= MinPunctMs && prevSentenceEnd))
            {
                var closed = group.Take(group.Count - 1).ToList();
                Flush(closed.Count > 0 ? closed : group);
                group = new List<SubtitleSegment> { seg };
            }
        }
        Flush(group);
        return candidates;
    }

    private static ClipCandidate Score(ClipCandidate c, int sourceDurationMs)
    {
        var t = c.Text;
        int conflictBase = Math.Min(10, Keywords.Conflict.Count(t.Contains) * 3);
        int twistBase = Math.Min(10, Keywords.Twist.Count(t.Contains) * 4);
        int emotionBase = Math.Min(10, Keywords.Emotion.Count(t.Contains) * 3);
        int cliffBase = Math.Min(10, Keywords.Cliffhanger.Count(t.Contains) * 3);

        int punct = t.Count(ch => ch is '?' or '？' or '!' or '！' or '…');
        int tailDist = Math.Max(0, sourceDurationMs - c.EndMs);
        int tailBonus = tailDist <= 10000 ? 4 : tailDist <= 20000 ? 2 : 0;

        c.Conflict = Math.Min(10, conflictBase + Math.Min(3, punct));
        c.Twist = Math.Min(10, twistBase + (t.Contains("原来") || t.Contains("竟然") ? 1 : 0));
        c.Emotion = Math.Min(10, emotionBase + Math.Min(2, punct));
        c.Cliffhanger = Math.Min(10, cliffBase + tailBonus + (t.Contains("?") || t.Contains("？") ? 2 : 0));
        c.Total = Math.Round(0.4 * c.Conflict + 0.25 * c.Twist + 0.25 * c.Emotion + 0.10 * c.Cliffhanger, 2);
        return c;
    }
}
