namespace ChannelsPublisher.Clip;

/// <summary>混剪选段：跨集全局取最具戏剧张力的 50%，精确+近似去重，排成叙事弧（钩子→张力→高潮→悬念）。
/// 移植自 material_clip/clip_modes.py(MashupClipMode) + selection.py(dedupe_similar_candidates)。</summary>
public static class Mashup
{
    // 混剪偏重戏剧张力/悬念（冲突/反转/钩子 > 情绪）。
    public static double DramaScore(ClipCandidate c)
        => 0.35 * c.Conflict + 0.30 * c.Twist + 0.20 * c.Cliffhanger + 0.15 * c.Emotion;

    public static List<ClipCandidate> Select(IReadOnlyList<ClipCandidate> candidates, Action<string>? log)
    {
        if (candidates.Count == 0) return new List<ClipCandidate>();
        var ranked = candidates.OrderByDescending(DramaScore).ToList();
        int keep = Math.Max(1, (int)Math.Round(ranked.Count * 0.5));
        var picked = Dedupe(ranked.Take(keep).ToList());
        var episodes = picked.Select(c => c.EpisodeIndex).Distinct().Count();
        log?.Invoke($"🎬 混剪选段：跨 {episodes} 集全局选出 {picked.Count} 段最具张力片段");
        return ArrangeNarrativeArc(picked);
    }

    private static List<ClipCandidate> Dedupe(List<ClipCandidate> items)
    {
        // 精确去重（同视频同起止，保留高分）。
        var exact = items
            .GroupBy(c => (c.VideoPath, c.StartMs, c.EndMs))
            .Select(g => g.OrderByDescending(c => c.Total).First())
            .ToList();

        // 近似去重：字符 3-gram Jaccard ≥ 0.82 视为重复，保留高分者（回放/闪回/重复桥段）。
        var kept = new List<ClipCandidate>();
        var keptGrams = new List<HashSet<string>>();
        foreach (var c in exact.OrderByDescending(c => c.Total))
        {
            var g = NGrams(Normalize(c.Text), 3);
            bool dup = false;
            for (int i = 0; i < kept.Count; i++)
                if (Jaccard(g, keptGrams[i]) >= 0.82) { dup = true; break; }
            if (!dup) { kept.Add(c); keptGrams.Add(g); }
        }
        return kept;
    }

    // 钩子(情绪+冲突) → 中段(综合分升序) → 高潮(综合分) → 悬念收尾(cliffhanger)。<3 段退化为升序。
    public static List<ClipCandidate> ArrangeNarrativeArc(List<ClipCandidate> candidates)
    {
        var items = candidates.ToList();
        if (items.Count < 3) return items.OrderBy(c => c.Total).ToList();

        var chosen = new HashSet<ClipCandidate>();
        ClipCandidate PopBest(Func<ClipCandidate, (double, double)> key)
        {
            var best = items.Where(c => !chosen.Contains(c)).OrderByDescending(key).First();
            chosen.Add(best);
            return best;
        }

        var cliffEnd = PopBest(c => (c.Cliffhanger, c.Total));
        var climax = PopBest(c => (c.Total, c.Twist));
        var hook = PopBest(c => (c.Emotion + c.Conflict, c.Total));
        var middle = items.Where(c => !chosen.Contains(c)).OrderBy(c => c.Total).ToList();

        var result = new List<ClipCandidate> { hook };
        result.AddRange(middle);
        result.Add(climax);
        result.Add(cliffEnd);
        return result;
    }

    private static string Normalize(string t)
        => new string((t ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static HashSet<string> NGrams(string t, int n)
    {
        var set = new HashSet<string>();
        if (t.Length < n) { if (t.Length > 0) set.Add(t); return set; }
        for (int i = 0; i <= t.Length - n; i++) set.Add(t.Substring(i, n));
        return set;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        int inter = a.Count <= b.Count ? a.Count(b.Contains) : b.Count(a.Contains);
        int union = a.Count + b.Count - inter;
        return union == 0 ? 0 : (double)inter / union;
    }
}
