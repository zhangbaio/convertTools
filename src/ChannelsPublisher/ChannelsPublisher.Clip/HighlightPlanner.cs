namespace ChannelsPublisher.Clip;

/// <summary>高光选段与装箱。移植自 material_clip/clip_modes.py(HighlightClipMode) + clip_split.py(plan_range_clips)。</summary>
public static class HighlightPlanner
{
    /// <summary>选出综合分最高的 50%，多集按集时长配额（最大余数法）分配名额。</summary>
    public static List<ClipCandidate> Select(IReadOnlyList<ClipCandidate> candidates, IReadOnlyDictionary<int, double> episodeDurationsSec)
    {
        if (candidates.Count == 0) return new List<ClipCandidate>();
        int totalKeep = Math.Max(1, (int)Math.Round(candidates.Count * 0.5));

        var byEp = candidates.GroupBy(c => c.EpisodeIndex).ToDictionary(g => g.Key, g => g.ToList());
        var quota = AllocateEpisodeQuota(byEp, episodeDurationsSec, totalKeep);

        var selected = new HashSet<ClipCandidate>();
        foreach (var (ep, items) in byEp)
        {
            int keep = quota.TryGetValue(ep, out var q) ? q : 0;
            foreach (var c in items.OrderByDescending(c => c.Total).Take(keep)) selected.Add(c);
        }
        // 按全局综合分降序返回，保证顺序稳定。
        return candidates.OrderByDescending(c => c.Total).Where(selected.Contains).ToList();
    }

    public static Dictionary<int, int> AllocateEpisodeQuota(
        Dictionary<int, List<ClipCandidate>> byEp,
        IReadOnlyDictionary<int, double> durs,
        int totalKeep)
    {
        var weights = new Dictionary<int, double>();
        foreach (var (ep, items) in byEp)
        {
            double d = durs != null && durs.TryGetValue(ep, out var dv) && dv > 0 ? dv : items.Max(c => c.EndMs) / 1000.0;
            weights[ep] = Math.Max(d, 1e-6);
        }

        var quota = byEp.Keys.ToDictionary(e => e, _ => 0);
        int remaining = totalKeep;
        if (totalKeep >= byEp.Count)
        {
            foreach (var e in byEp.Keys) quota[e] = 1;   // 每集保底 1 个
            remaining -= byEp.Count;
        }

        double totalW = weights.Values.Sum();
        var raw = byEp.Keys.ToDictionary(e => e, e => remaining * weights[e] / totalW);
        foreach (var e in byEp.Keys) quota[e] += (int)raw[e];
        int leftover = remaining - byEp.Keys.Sum(e => (int)raw[e]);
        foreach (var e in byEp.Keys.OrderByDescending(e => raw[e] - (int)raw[e]).Take(Math.Max(0, leftover)))
            quota[e] += 1;
        foreach (var e in byEp.Keys) quota[e] = Math.Min(quota[e], byEp[e].Count);
        return quota;
    }

    /// <summary>把选中片段装箱进 count 个 [minMs,maxMs] 的短片。
    /// preserveOrder=false（高光）：按分贪心装箱、每片钩子置前；
    /// preserveOrder=true（混剪）：保留传入的叙事弧顺序，顺序填满一片再进下一片。</summary>
    public static List<List<ClipCandidate>> PlanRangeBins(IReadOnlyList<ClipCandidate> selected, int count, int minMs, int maxMs, bool preserveOrder = false)
    {
        int n = Math.Max(1, count);
        var binMs = new int[n];
        var bins = new List<List<ClipCandidate>>();
        for (int i = 0; i < n; i++) bins.Add(new List<ClipCandidate>());

        if (preserveOrder)
        {
            int b = 0;
            foreach (var c in selected)
            {
                int dur = c.DurationMs;
                // 当前片已达下限且再加会超上限 → 进下一片。
                if (binMs[b] + dur > maxMs && binMs[b] >= minMs) b++;
                if (b >= n) break;
                if (binMs[b] + dur > maxMs && bins[b].Count > 0) continue; // 放不下且非空则跳过该片
                bins[b].Add(c);
                binMs[b] += dur;
            }
            return bins.Where(x => x.Count > 0).ToList();
        }

        foreach (var c in selected.OrderByDescending(c => c.Total))
        {
            int dur = c.DurationMs;
            var fit = Enumerable.Range(0, n).Where(b => binMs[b] + dur <= maxMs).ToList();
            if (fit.Count == 0)
            {
                if (Enumerable.Range(0, n).All(b => binMs[b] >= minMs)) break; // 各片都够长，丢弃低分
                continue;                                                       // 此片过长，放不进未满片
            }
            var below = fit.Where(b => binMs[b] < minMs).ToList();
            int pick = below.Count > 0
                ? below.OrderBy(b => b).ThenBy(b => binMs[b]).First()
                : fit.OrderBy(b => binMs[b]).First();
            bins[pick].Add(c);
            binMs[pick] += dur;
        }

        foreach (var bin in bins)
        {
            if (bin.Count > 1)
            {
                var hook = bin.OrderByDescending(c => c.HookScore).First();
                bin.Remove(hook);
                bin.Insert(0, hook);
            }
        }
        return bins.Where(b => b.Count > 0).ToList();
    }
}
