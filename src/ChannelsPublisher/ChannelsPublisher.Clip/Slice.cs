namespace ChannelsPublisher.Clip;

/// <summary>切片选段：在每集连续时间轴上挑不重叠、戏剧分最高的连续窗口（各约 target），
/// 跨集汇总取全局最高 count 个。每个窗口=一条连续切片（不打散、无内部跳剪）。
/// 移植自 material_clip/clip_split.py(select_best_windows) + SliceClipMode。</summary>
public static class Slice
{
    public static List<ClipCandidate> SelectWindows(IReadOnlyList<ClipCandidate> all, int count, int targetMs, Action<string>? log)
    {
        int n = Math.Max(1, count);
        var windows = new List<(double Score, ClipCandidate Win)>();
        foreach (var group in all.GroupBy(c => c.EpisodeIndex))
        {
            var segs = group.OrderBy(c => c.StartMs).ToList();
            if (segs.Count == 0) continue;
            windows.AddRange(BestWindows(segs, n, targetMs));
        }
        var picked = windows
            .OrderByDescending(w => w.Score)
            .Take(n)
            .Select(w => w.Win)
            .OrderBy(c => c.EpisodeIndex).ThenBy(c => c.StartMs)
            .ToList();
        log?.Invoke($"✂️ 切片选窗：{picked.Count} 条连续片段（约 {targetMs / 1000}s）");
        return picked;
    }

    private static IEnumerable<(double Score, ClipCandidate Win)> BestWindows(List<ClipCandidate> segs, int count, int targetMs)
    {
        int total = segs[^1].EndMs;
        var vid = segs[0].VideoPath;
        int ep = segs[0].EpisodeIndex;
        if (total <= targetMs)
        {
            yield return (segs.Sum(s => s.Total), MakeWin(vid, ep, segs[0].StartMs, total));
            yield break;
        }

        var cands = new List<(double Score, int Start, int End)>();
        for (int i = 0; i < segs.Count; i++)
        {
            int acc = 0; double score = 0; int j = i;
            while (j < segs.Count && acc < targetMs)
            {
                int dur = Math.Max(1, segs[j].EndMs - segs[j].StartMs);
                acc += dur;
                score += segs[j].Total * dur;
                j++;
            }
            int start = segs[i].StartMs;
            cands.Add((score, start, Math.Min(total, start + targetMs)));
        }
        cands.Sort((a, b) => b.Score.CompareTo(a.Score));

        var chosen = new List<(int Start, int End)>();
        foreach (var (score, start, end) in cands)
        {
            if (chosen.Any(c => start < c.End && end > c.Start)) continue; // 与已选重叠则跳过
            chosen.Add((start, end));
            yield return (score, MakeWin(vid, ep, start, end));
            if (chosen.Count >= count) break;
        }
    }

    private static ClipCandidate MakeWin(string vid, int ep, int start, int end)
        => new ClipCandidate { EpisodeIndex = ep, VideoPath = vid, StartMs = start, EndMs = end, Text = "" };
}
