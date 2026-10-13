using System.Globalization;
using System.Text.Json.Nodes;

namespace ShortDrama.Infrastructure.Automation;

public sealed record HongguoDramaField(string Name, string Value);

public sealed record HongguoDramaFieldRead(
    string Orientation,
    string Genre,
    IReadOnlyList<HongguoDramaField> Fields);

public sealed record HongguoDramaLookupHit(
    string Title,
    string BookId,
    string Orientation,
    string Genre,
    IReadOnlyList<HongguoDramaField> Fields,
    IReadOnlyList<string> Candidates)
{
    public static HongguoDramaLookupHit Empty { get; } = new("", "", "", "", [], []);
}

public static class HongguoDramaFieldReader
{
    private static readonly HashSet<string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "漫剧", "短剧", "AI短剧", "AI漫剧", "动态漫", "小说", "有声", "出版", "comic", "short"
    };

    private static readonly string[] GenreKeys =
    [
        "category_dim_theme", "theme", "themes", "genre", "genre_name", "subject",
        "tag_text", "tags", "complete_category", "category", "category_v2"
    ];

    private static readonly string[] OrientationKeys =
    [
        "video_orientation", "orientation", "screen_type", "screen_mode",
        "video_direction", "video_vertical", "is_vertical", "vertical"
    ];

    private static readonly string[] WidthKeys = ["video_width", "width"];
    private static readonly string[] HeightKeys = ["video_height", "height"];

    private static readonly string[] TitleKeys = ["book_name", "title", "series_title", "name"];
    private static readonly string[] AuthorKeys = ["author", "author_name", "authorName", "anchor", "producer", "copyright"];
    private static readonly string[] ActorKeys = ["actors", "actor", "actor_list", "main_actor", "celebrity", "role_name"];
    private static readonly string[] EpisodeKeys =
    [
        "episode_cnt", "drama_chapter_number", "serial_count", "episode_total",
        "episode_count", "chapter_number"
    ];
    private static readonly string[] PlayKeys = ["read_count", "play_count", "play_num", "read_cnt", "play_cnt", "view_count"];
    private static readonly string[] FavoriteKeys = ["favorite_count", "collect_count", "add_bookshelf_count", "followed_cnt"];
    private static readonly string[] DurationKeys = ["duration", "total_duration", "video_duration", "play_duration"];
    private static readonly string[] PublishKeys =
    [
        "media_book_first_recommend_time", "first_online_time", "firstOnlineTime",
        "online_time", "onlineTime", "publish_time", "create_time"
    ];
    private static readonly string[] FilingKeys =
    [
        "record_number", "filing_number", "beian_number", "copyright_number",
        "media_record", "record_info", "icp"
    ];
    private static readonly string[] IntroKeys = ["abstract", "intro", "series_intro", "description"];
    private static readonly string[] ContentTypeKeys = ["content_type", "book_type", "type", "category"];

    public static HongguoDramaFieldRead Read(JsonObject root)
    {
        var leaves = new List<Leaf>();
        Collect(root, "", leaves, 0);
        var orientation = ResolveOrientation(leaves);
        var genre = ResolveGenre(leaves);
        var contentType = ResolveContentType(leaves);
        return new HongguoDramaFieldRead(orientation, genre, BuildFields(leaves, orientation, genre, contentType));
    }

    private static void Collect(JsonNode? node, string path, List<Leaf> leaves, int depth)
    {
        if (node is null || depth > 6)
            return;

        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                    Collect(property.Value, Join(path, property.Key), leaves, depth + 1);
                break;
            case JsonArray array:
                if (array.Count == 0)
                    return;
                if (array.All(item => item is null or JsonValue))
                {
                    var text = string.Join("、", array.Select(FormatScalar).Where(item => item.Length > 0));
                    if (text.Length > 0)
                        leaves.Add(new Leaf(path, LastKey(path), Trim(text)));
                    return;
                }

                for (var index = 0; index < array.Count && index < 6; index++)
                    Collect(array[index], path, leaves, depth + 1);
                break;
            default:
                var formatted = FormatScalar(node);
                if (formatted.Length > 0)
                    leaves.Add(new Leaf(path, LastKey(path), Trim(formatted)));
                break;
        }
    }

    private static string ResolveOrientation(IReadOnlyList<Leaf> leaves)
    {
        foreach (var leaf in leaves.Where(item => OrientationKeys.Contains(item.Key, StringComparer.OrdinalIgnoreCase)))
        {
            var labeled = ReadOrientationToken(leaf.Value);
            if (labeled.Length > 0)
                return labeled;
            if (leaf.Value is "1" or "true" or "True" or "是")
                return "竖屏";
            if (leaf.Value is "0" or "2" or "false" or "False" or "否")
                return "横屏";
        }

        foreach (var leaf in leaves.Where(item => GenreKeys.Contains(item.Key, StringComparer.OrdinalIgnoreCase)))
        {
            var labeled = ReadOrientationToken(leaf.Value);
            if (labeled.Length > 0)
                return labeled;
        }

        var width = FirstNumber(leaves, WidthKeys);
        var height = FirstNumber(leaves, HeightKeys);
        if (width > 0 && height > 0 && width != height)
            return height > width ? "竖屏" : "横屏";
        return "";
    }

    private static string ResolveGenre(IReadOnlyList<Leaf> leaves)
    {
        var found = new List<string>();
        foreach (var key in GenreKeys)
        {
            foreach (var leaf in leaves.Where(item =>
                         string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase) ||
                         item.Path.Contains("category_dim_theme", StringComparison.OrdinalIgnoreCase) &&
                         item.Key is "name" or "tag_name"))
            {
                foreach (var token in SplitTokens(leaf.Value))
                {
                    if (IsOrientation(token) || IsContentType(token) || token.All(char.IsDigit))
                        continue;
                    if (found.Contains(token, StringComparer.Ordinal))
                        continue;
                    found.Add(token);
                }
            }

            if (found.Count > 0)
                break;
        }

        return string.Join("、", found.Take(4));
    }

    private static string ResolveContentType(IReadOnlyList<Leaf> leaves)
    {
        foreach (var key in ContentTypeKeys)
        {
            foreach (var leaf in leaves.Where(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                var token = SplitTokens(leaf.Value).FirstOrDefault(IsContentType);
                if (!string.IsNullOrWhiteSpace(token))
                    return token;
            }
        }

        return "";
    }

    private static IReadOnlyList<HongguoDramaField> BuildFields(
        IReadOnlyList<Leaf> leaves,
        string orientation,
        string genre,
        string contentType)
    {
        var fields = new List<HongguoDramaField>();
        Add(fields, "剧名", FirstTitle(leaves));
        Add(fields, "作者", FirstText(leaves, AuthorKeys));
        Add(fields, "演员", FirstText(leaves, ActorKeys));
        Add(fields, "集数", FirstEpisode(leaves));
        Add(fields, "播放量", FirstCount(leaves, PlayKeys));
        Add(fields, "收藏量", FirstCount(leaves, FavoriteKeys));
        Add(fields, "时长", FormatDuration(FirstText(leaves, DurationKeys)));
        Add(fields, "上线时间", FormatPublishTime(FirstText(leaves, PublishKeys)));
        Add(fields, "备案号", FirstFiling(leaves));
        Add(fields, "横竖屏", orientation);
        Add(fields, "题材", genre);
        Add(fields, "内容类型", contentType);
        Add(fields, "简介", FirstText(leaves, IntroKeys));
        return fields;
    }

    private static string FirstTitle(IReadOnlyList<Leaf> leaves)
    {
        var titled = FirstText(leaves, TitleKeys);
        if (titled.Length > 0)
            return titled;
        var text = FirstText(leaves, ["text"]);
        return text.Length is > 0 and <= 80 && !text.Contains('<') ? text : "";
    }

    private static string FirstEpisode(IReadOnlyList<Leaf> leaves)
    {
        foreach (var key in new[] { "episode_right_text" }.Concat(EpisodeKeys))
        {
            var value = FirstText(leaves, [key]);
            if (value.Length == 0 || value is "是" or "否" || !value.Any(char.IsDigit))
                continue;
            return value;
        }

        return "";
    }

    private static string FirstCount(IReadOnlyList<Leaf> leaves, IEnumerable<string> keys)
    {
        var value = FirstText(leaves, keys);
        if (value.Length == 0 || value is "是" or "否" || !value.Any(char.IsDigit))
            return "";
        return FormatCount(value);
    }

    private static void Add(List<HongguoDramaField> fields, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            fields.Add(new HongguoDramaField(name, value.Trim()));
    }

    private static string FirstText(IReadOnlyList<Leaf> leaves, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            var leaf = leaves.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (leaf is not null && !string.IsNullOrWhiteSpace(leaf.Value))
                return leaf.Value;
        }

        return "";
    }

    private static double FirstNumber(IReadOnlyList<Leaf> leaves, IEnumerable<string> keys)
    {
        var text = FirstText(leaves, keys);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static string FirstFiling(IReadOnlyList<Leaf> leaves)
    {
        var labeled = FirstText(leaves, FilingKeys);
        if (labeled.Length > 0)
            return labeled;
        return leaves.Select(item => item.Value).FirstOrDefault(value => value.Contains("备字", StringComparison.Ordinal)) ?? "";
    }

    private static string FormatCount(string raw)
    {
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return raw;
        if (value < 10_000)
            return value.ToString(CultureInfo.InvariantCulture);
        return (value / 10_000d).ToString("0.#", CultureInfo.InvariantCulture) + "万";
    }

    private static string FormatDuration(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains('分') || raw.Contains('秒') || raw.Contains(':'))
            return raw;
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ||
            seconds <= 0 ||
            seconds > 172_800)
            return raw;
        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        var remain = seconds % 60;
        if (hours > 0)
            return $"{hours}小时{minutes}分{remain}秒";
        if (minutes > 0)
            return $"{minutes}分{remain}秒";
        return $"{remain}秒";
    }

    private static string FormatPublishTime(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var formatted = HongguoHighCalendarMapper.NormalizePublishTime(raw);
        return string.IsNullOrWhiteSpace(formatted) ? raw : formatted;
    }

    private static string ReadOrientationToken(string value)
    {
        if (value.Contains("竖屏", StringComparison.Ordinal))
            return "竖屏";
        if (value.Contains("横屏", StringComparison.Ordinal))
            return "横屏";
        return "";
    }

    private static bool IsOrientation(string token) => token is "竖屏" or "横屏";

    private static bool IsContentType(string token) => ContentTypes.Contains(token);

    private static IEnumerable<string> SplitTokens(string value)
    {
        return value
            .Split(['|', '｜', ',', '，', '、', '/', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length is > 0 and <= 12);
    }

    private static string FormatScalar(JsonNode? node)
    {
        if (node is not JsonValue value)
            return "";
        if (value.TryGetValue<string>(out var text))
            return text.Trim();
        if (value.TryGetValue<bool>(out var flag))
            return flag ? "是" : "否";
        if (value.TryGetValue<long>(out var integer))
            return integer.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<int>(out var number))
            return number.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<double>(out var real))
        {
            if (Math.Abs(real - Math.Round(real)) < 0.0001)
                return ((long)Math.Round(real)).ToString(CultureInfo.InvariantCulture);
            return real.ToString("0.##", CultureInfo.InvariantCulture);
        }

        return value.ToString().Trim().Trim('"');
    }

    private static string Trim(string value) =>
        value.Length <= 1000 ? value : value[..1000];

    private static string Join(string path, string key) =>
        string.IsNullOrEmpty(path) ? key : path + "." + key;

    private static string LastKey(string path)
    {
        var index = path.LastIndexOf('.');
        return index < 0 ? path : path[(index + 1)..];
    }

    private sealed record Leaf(string Path, string Key, string Value);
}
