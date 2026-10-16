namespace TikTokPublisher.Core.Drama;

/// <summary>
/// 对齐 Python <c>pick_drama_search_match</c>：按剧名相似度挑选搜索结果，低于阈值视为未命中。
/// </summary>
public static class DramaSearchMatch
{
    public const double MinRatio = 0.6;

    public static double TitleSimilarity(string? needle, string? title)
    {
        var left = (needle ?? "").Trim();
        var right = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return 0;

        if (string.Equals(left, right, StringComparison.Ordinal))
            return 1.0;

        var shorter = left.Length <= right.Length ? left : right;
        var longer = ReferenceEquals(shorter, left) ? right : left;
        if (shorter.Length >= 4 && longer.Contains(shorter, StringComparison.Ordinal))
            return 0.9;

        return ComputeSequenceRatio(left, right);
    }

    public static (DramaSearchItem? Match, double Ratio) Pick(
        IReadOnlyList<DramaSearchItem> results,
        string? name,
        double minRatio = MinRatio)
    {
        if (results is null || results.Count == 0)
            return (null, 0);

        var needle = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(needle))
            return (null, 0);

        DramaSearchItem? best = null;
        var bestRatio = 0.0;
        foreach (var item in results)
        {
            var title = (item.Title ?? "").Trim();
            var ratio = TitleSimilarity(needle, title);
            if (ratio <= bestRatio)
                continue;

            best = item;
            bestRatio = ratio;
            if (bestRatio >= 1.0)
                break;
        }

        if (best is not null && bestRatio >= minRatio)
            return (best, bestRatio);
        return (null, bestRatio);
    }

    private static double ComputeSequenceRatio(string left, string right)
    {
        // difflib.SequenceMatcher.ratio ≈ 2 * M / (len(a) + len(b))，M 为匹配字符数。
        var matches = LongestCommonSubsequenceLength(left, right);
        var total = left.Length + right.Length;
        return total == 0 ? 0 : 2.0 * matches / total;
    }

    private static int LongestCommonSubsequenceLength(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            for (var j = 1; j <= right.Length; j++)
            {
                current[j] = left[i - 1] == right[j - 1]
                    ? previous[j - 1] + 1
                    : Math.Max(previous[j], current[j - 1]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return previous[right.Length];
    }
}
