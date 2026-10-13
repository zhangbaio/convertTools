namespace ShortDrama.Core.Models;

/// <summary>
/// Search category shown on the short-drama download page.
/// Signed Hongguo search tabs: 短剧 = 11, 漫剧 = 19, 合集 = 23.
/// 默认 keeps the historical signed tab (11) plus the NovelFM comic fallback.
/// </summary>
public static class DramaSearchCategory
{
    public const string Comic = "漫剧";
    public const string Short = "短剧";
    public const string Collection = "合集";
    public const string Default = "默认";
    public const string BookId = "短剧ID";

    public static readonly IReadOnlyList<string> Options = [Comic, Short, Collection, Default, BookId];

    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        foreach (var option in Options)
        {
            if (string.Equals(option, text, StringComparison.Ordinal))
            {
                return option;
            }
        }

        return Comic;
    }

    public static int SignedTabType(string? value) => Normalize(value) switch
    {
        Comic => 19,
        Collection => 23,
        _ => 11
    };

    /// <summary>
    /// NovelFM tab 13 is a comic catalog. Use it for 漫剧 and 默认, and for callers that do not pass a category.
    /// </summary>
    public static bool UseNovelFm(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var category = Normalize(value);
        return category is Comic or Default;
    }
}
