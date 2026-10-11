namespace ShortDrama.Infrastructure.Automation;

/// <summary>
/// 红果高码率标准版榜单。分类令牌和榜单键与 HG 短剧助手 v2.1.9 的榜单对照表一致。
/// 漫剧的 selected_items 固定为 comic_series_rank，具体榜放在 sub_selected_items。
/// </summary>
public static class HongguoRankCatalog
{
    public const string CellId = "7470092475068071998";
    public const string TabType = "26";
    public const string Path = "/reading/bookapi/bookmall/cell/change/v:version/";

    public static readonly IReadOnlyList<string> Categories = ["全部", "真人剧", "漫剧", "AI剧"];
    public static readonly IReadOnlyList<string> Boards = ["热播榜", "推荐榜", "新剧榜", "必看榜", "热搜榜", "收藏榜"];

    private static readonly Dictionary<string, string> SelectedItemsByCategory = new(StringComparer.Ordinal)
    {
        ["全部"] = "all",
        ["真人剧"] = "human",
        ["漫剧"] = "comic_series_rank",
        ["AI剧"] = "ai_playlet",
    };

    private static readonly Dictionary<(string Category, string Board), string> SubSelectedItems = new()
    {
        [("全部", "热播榜")] = "ranklist_hot_play_sc",
        [("全部", "推荐榜")] = "ranklist_hot_sc",
        [("全部", "新剧榜")] = "ranklist_new_rank_sc",
        [("全部", "必看榜")] = "ranklist_must_watch",
        [("全部", "热搜榜")] = "ranklist_hot_search_sc",
        [("全部", "收藏榜")] = "ranklist_followed",

        [("真人剧", "热播榜")] = "human_hot_play",
        [("真人剧", "推荐榜")] = "human_hot_sc",
        [("真人剧", "新剧榜")] = "human_new_rank",
        [("真人剧", "必看榜")] = "human_must_watch",
        [("真人剧", "热搜榜")] = "human_hot_search",
        [("真人剧", "收藏榜")] = "human_followed",

        [("漫剧", "热播榜")] = "comic_series_hot_play",
        [("漫剧", "推荐榜")] = "comic_series_hot_rank",
        [("漫剧", "新剧榜")] = "comic_series_new_rank",
        [("漫剧", "热搜榜")] = "comic_series_hot_search",

        [("AI剧", "热播榜")] = "ai_playlet_hot_play",
        [("AI剧", "推荐榜")] = "ai_playlet_hot_sc",
        [("AI剧", "新剧榜")] = "ai_playlet_new_rank",
        [("AI剧", "必看榜")] = "ai_playlet_must_watch",
        [("AI剧", "热搜榜")] = "ai_playlet_hot_search",
        [("AI剧", "收藏榜")] = "ai_playlet_followed",
    };

    public static bool TryResolve(string? category, string? board, out HongguoRankQuery query)
    {
        query = default;
        var categoryText = (category ?? "").Trim();
        var boardText = (board ?? "").Trim();
        if (!SelectedItemsByCategory.TryGetValue(categoryText, out var selectedItems))
            return false;
        if (!SubSelectedItems.TryGetValue((categoryText, boardText), out var subSelectedItems))
            return false;
        query = new HongguoRankQuery(selectedItems, subSelectedItems);
        return true;
    }

    public static string? UnavailableReason(string? category, string? board)
    {
        if (TryResolve(category, board, out _))
            return null;

        var categoryText = (category ?? "").Trim();
        var boardText = (board ?? "").Trim();
        if (!SelectedItemsByCategory.ContainsKey(categoryText))
            return "未知榜单分类";
        if (!Boards.Contains(boardText, StringComparer.Ordinal))
            return "未知榜单";
        return $"{categoryText}没有{boardText}";
    }
}

public readonly record struct HongguoRankQuery(string SelectedItems, string SubSelectedItems);
