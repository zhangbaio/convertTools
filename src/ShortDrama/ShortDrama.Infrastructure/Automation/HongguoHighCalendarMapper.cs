using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Automation;

public static class HongguoHighCalendarMapper
{
    private static readonly Regex LastChapterNumberRegex =
        new(@"第\s*0*(\d+)\s*集", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] NestedKeys =
    [
        "book_info", "bookInfo", "series_info", "seriesInfo",
        "video_data", "videoData", "video_detail", "videoDetail",
        "album_info", "albumInfo"
    ];

    private static readonly string[] BookIdKeys =
        ["book_id", "bookId", "series_id", "seriesId", "series_id_str", "seriesIdStr"];

    private static readonly string[] TitleKeys =
        ["title", "series_title", "book_name", "bookName", "name"];

    private static readonly string[] AuthorKeys =
        ["author", "author_name", "anchor", "producer"];

    private static readonly string[] IntroKeys =
        ["intro", "series_intro", "abstract", "description"];

    private static readonly string[] CategoryKeys =
        ["category", "tag_text", "type", "complete_category", "tags"];

    private static readonly string[] CoverKeys =
    [
        "series_cover", "seriesCover", "book_cover", "bookCover",
        "poster_url", "posterUrl", "poster", "cover",
        "origin_cover", "originCover", "cover_url", "coverUrl",
        "thumb_url", "thumbUrl", "thumb_uri", "thumbUri",
        "audio_thumb_uri", "audioThumbUri", "horiz_thumb_url", "horizThumbUrl",
        "image_url", "imageUrl"
    ];

    private static readonly string[] MediaValueKeys =
    [
        "url_list", "urlList", "urls", "url",
        "cover_url", "coverUrl", "main_url", "mainUrl",
        "uri", "web_uri", "webUri"
    ];

    private static readonly string[] PublishKeys =
    [
        "media_book_first_recommend_time", "firstOnlineTime", "first_online_time",
        "first_visible_time", "onlineTime", "online_time", "createTime", "create_time",
        "publish_time", "cache_date", "cacheDate"
    ];

    private static readonly string[] EpisodeKeys =
    [
        "drama_chapter_number", "dramaChapterNumber", "final_chapter_number", "finalChapterNumber",
        "serial_count", "episode_cnt", "episode_count", "episodeCount", "chapter_number"
    ];

    private static readonly string[] LastChapterTitleKeys = ["last_chapter_title", "lastChapterTitle"];

    private static readonly string[] TimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy/MM/dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy/MM/dd HH:mm",
        "yyyy-MM-dd",
        "yyyyMMdd"
    ];

    public static IReadOnlyList<DramaSearchItem> MapPayload(JsonNode? payload)
    {
        var results = new List<DramaSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in ExtractItems(payload))
        {
            var mapped = TryMapItem(raw);
            if (mapped is null || !seen.Add(mapped.BookId))
            {
                continue;
            }

            results.Add(mapped);
        }

        return results;
    }

    public static IReadOnlyList<JsonObject> ExtractItems(JsonNode? payload)
    {
        switch (payload)
        {
            case JsonArray array:
            {
                var items = new List<JsonObject>();
                foreach (var entry in array)
                {
                    if (entry is JsonObject obj &&
                        string.Equals(ReadString(obj, "type"), "calendarResults", StringComparison.Ordinal))
                    {
                        items.AddRange(ExtractItems(obj["items"] ?? obj["data"]));
                    }
                    else if (entry is JsonObject dict)
                    {
                        items.Add(dict);
                    }
                }

                return items;
            }
            case JsonObject root:
                if (string.Equals(ReadString(root, "type"), "calendarResults", StringComparison.Ordinal))
                {
                    return ExtractItems(root["items"] ?? root["data"]);
                }

                foreach (var key in new[] { "items", "data", "results", "list", "books", "records" })
                {
                    if (root[key] is JsonArray or JsonObject)
                    {
                        var extracted = ExtractItems(root[key]);
                        if (extracted.Count > 0)
                        {
                            return extracted;
                        }
                    }
                }

                return [];
            default:
                return [];
        }
    }

    public static IReadOnlyList<JsonObject> ExtractLandpageItems(JsonNode? payload)
    {
        var found = new List<JsonObject>();
        var seen = new HashSet<int>();
        Walk(payload);
        return found;

        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                {
                    JsonObject? candidate = null;
                    if (obj["video_data"] is JsonObject video &&
                        (!string.IsNullOrWhiteSpace(ReadString(video, "series_id")) ||
                         !string.IsNullOrWhiteSpace(ReadString(video, "series_id_str"))))
                    {
                        candidate = video;
                    }
                    else if (!string.IsNullOrWhiteSpace(ReadString(obj, "series_id")) ||
                             !string.IsNullOrWhiteSpace(ReadString(obj, "series_title")) ||
                             !string.IsNullOrWhiteSpace(ReadString(obj, "series_id_str")))
                    {
                        candidate = obj;
                    }

                    if (candidate is not null)
                    {
                        var marker = candidate.GetHashCode();
                        if (seen.Add(marker))
                        {
                            found.Add(candidate);
                        }

                        return;
                    }

                    foreach (var property in obj)
                    {
                        Walk(property.Value);
                    }

                    break;
                }
                case JsonArray array:
                    foreach (var item in array)
                    {
                        Walk(item);
                    }

                    break;
            }
        }
    }

    public static DramaSearchItem? TryMapItem(JsonObject obj)
    {
        var nested = NestedObjects(obj);
        var bookId = FirstMeaningful(obj, BookIdKeys, nested);
        if (string.IsNullOrWhiteSpace(bookId))
        {
            return null;
        }

        var category = FirstMeaningful(obj, CategoryKeys, nested) ?? "";
        if (category.All(char.IsDigit))
        {
            category = "";
        }

        return new DramaSearchItem(
            BookId: HongguoHighCrypto.EnsureBookPrefix(bookId),
            Title: FirstMeaningful(obj, TitleKeys, nested) ?? bookId,
            Category: category,
            EpisodeTotal: ReadEpisodeTotal(obj, nested),
            Intro: FirstMeaningful(obj, IntroKeys, nested) ?? "",
            PosterUrl: FirstMediaUrl(obj, CoverKeys, nested) ?? "",
            Author: FirstMeaningful(obj, AuthorKeys, nested) ?? "",
            PublishTime: NormalizePublishTime(CollectPublishValues(obj, nested)),
            FavoriteCount: FirstPositiveInt(obj, ["favorite_count", "followed_cnt", "collect_count", "add_bookshelf_count", "addBookshelfCount"], nested));
    }

    public static DramaSearchItem ApplyBookInfo(DramaSearchItem item, JsonObject bookInfo)
    {
        var author = FirstMeaningful(bookInfo, AuthorKeys, []) ?? item.Author;
        var category = item.Category;
        if (string.IsNullOrWhiteSpace(category))
        {
            var fromInfo = FirstMeaningful(bookInfo, CategoryKeys, []);
            if (!string.IsNullOrWhiteSpace(fromInfo) && !fromInfo.All(char.IsDigit))
            {
                category = fromInfo;
            }
        }

        var resolvedEpisodeTotal = ReadEpisodeTotal(bookInfo, []);
        var episodeTotal = resolvedEpisodeTotal > 0 ? resolvedEpisodeTotal : item.EpisodeTotal;
        var publishTime = NormalizePublishTime(
            CollectPublishValues(bookInfo, []).Append(item.PublishTime));
        var intro = string.IsNullOrWhiteSpace(item.Intro)
            ? FirstMeaningful(bookInfo, IntroKeys, []) ?? ""
            : item.Intro;
        var poster = string.IsNullOrWhiteSpace(item.PosterUrl)
            ? FirstMediaUrl(bookInfo, CoverKeys, []) ?? ""
            : item.PosterUrl;
        var favorite = item.FavoriteCount > 0
            ? item.FavoriteCount
            : FirstPositiveInt(bookInfo, ["add_bookshelf_count", "addBookshelfCount", "collect_count", "favorite_count"], []);
        return item with
        {
            Author = author,
            Category = category,
            EpisodeTotal = episodeTotal,
            PublishTime = publishTime,
            Intro = intro,
            PosterUrl = poster,
            FavoriteCount = favorite
        };
    }

    public static bool TryParsePublishDate(string? value, out DateTime publishedAt)
    {
        publishedAt = default;
        var text = NormalizePublishTime(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out publishedAt) ||
               DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out publishedAt);
    }

    public static IReadOnlyList<DramaSearchItem> FilterByRecentDays(IEnumerable<DramaSearchItem> items, int days)
    {
        var windowDays = Math.Clamp(days, 1, 30);
        var cutoff = DateTime.Today.AddDays(-(windowDays - 1));
        return items
            .Where(item => !TryParsePublishDate(item.PublishTime, out var publishedAt) || publishedAt.Date >= cutoff)
            .ToArray();
    }

    public static string NormalizePublishTime(params string?[] values) =>
        NormalizePublishTime((IEnumerable<string?>)values);

    public static string NormalizePublishTime(IEnumerable<string?> values)
    {
        var fallback = "";
        foreach (var value in values)
        {
            foreach (var timestamp in IterTimestamps(value))
            {
                DateTime clock;
                try
                {
                    clock = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().DateTime;
                }
                catch
                {
                    continue;
                }

                var formatted = clock.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                if (clock.Hour != 0 || clock.Minute != 0 || clock.Second != 0)
                {
                    return formatted;
                }

                if (string.IsNullOrWhiteSpace(fallback))
                {
                    fallback = formatted;
                }
            }
        }

        return fallback;
    }

    private static IEnumerable<string?> CollectPublishValues(JsonObject obj, IReadOnlyList<JsonObject> nested)
    {
        foreach (var key in PublishKeys)
        {
            yield return ReadString(obj, key);
            foreach (var child in nested)
            {
                yield return ReadString(child, key);
            }
        }
    }

    private static IReadOnlyList<JsonObject> NestedObjects(JsonObject obj)
    {
        var nested = new List<JsonObject>();
        foreach (var key in NestedKeys)
        {
            if (obj[key] is JsonObject child)
            {
                nested.Add(child);
            }
        }

        return nested;
    }

    private static string? FirstMeaningful(JsonObject obj, IEnumerable<string> keys, IReadOnlyList<JsonObject> nested)
    {
        foreach (var key in keys)
        {
            var value = Meaningful(ReadString(obj, key));
            if (value is not null)
            {
                return value;
            }

            foreach (var child in nested)
            {
                value = Meaningful(ReadString(child, key));
                if (value is not null)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? FirstMediaUrl(
        JsonObject obj,
        IEnumerable<string> keys,
        IReadOnlyList<JsonObject> nested)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var value))
            {
                var url = MediaUrlValue(value);
                if (url is not null)
                    return url;
            }

            foreach (var child in nested)
            {
                if (!child.TryGetPropertyValue(key, out value))
                    continue;
                var url = MediaUrlValue(value);
                if (url is not null)
                    return url;
            }
        }

        return null;
    }

    private static string? MediaUrlValue(JsonNode? value)
    {
        switch (value)
        {
            case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                return NormalizeMediaUrl(text);
            case JsonArray array:
                foreach (var item in array)
                {
                    var url = MediaUrlValue(item);
                    if (url is not null)
                        return url;
                }
                break;
            case JsonObject obj:
                foreach (var key in MediaValueKeys)
                {
                    if (!obj.TryGetPropertyValue(key, out var nested))
                        continue;
                    var url = MediaUrlValue(nested);
                    if (url is not null)
                        return url;
                }
                break;
        }

        return null;
    }

    internal static string? NormalizeMediaUrl(string? value)
    {
        var raw = (value ?? "").Trim();
        if (raw.StartsWith("//", StringComparison.Ordinal))
            raw = "https:" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        const string marker = "/novel-pic/";
        var path = uri.AbsolutePath;
        var markerIndex = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var templateIndex = markerIndex >= 0
            ? path.IndexOf('~', markerIndex + marker.Length)
            : -1;
        if (uri.Host.EndsWith(".byteimg.com", StringComparison.OrdinalIgnoreCase) &&
            markerIndex >= 0 &&
            templateIndex > markerIndex)
        {
            var imageId = path[(markerIndex + marker.Length)..templateIndex];
            if (!string.IsNullOrWhiteSpace(imageId))
            {
                var prefix = path[..markerIndex];
                if (prefix.EndsWith("/img", StringComparison.OrdinalIgnoreCase))
                    prefix = prefix[..^4];
                if (!prefix.EndsWith("/origin", StringComparison.OrdinalIgnoreCase))
                    prefix += "/origin";
                return $"{uri.Scheme}://{uri.Authority}{prefix}{marker}{imageId}";
            }
        }

        return uri.AbsoluteUri;
    }

    private static int FirstPositiveInt(JsonObject obj, IEnumerable<string> keys, IReadOnlyList<JsonObject> nested)
    {
        foreach (var key in keys)
        {
            var value = ReadPositiveInt(obj, key);
            if (value > 0)
            {
                return value;
            }

            foreach (var child in nested)
            {
                value = ReadPositiveInt(child, key);
                if (value > 0)
                {
                    return value;
                }
            }
        }

        return 0;
    }

    public static int ReadEpisodeTotal(JsonObject obj) => ReadEpisodeTotal(obj, NestedObjects(obj));

    private static int ReadEpisodeTotal(JsonObject obj, IReadOnlyList<JsonObject> nested)
    {
        var lastChapterNumber = ParseLastChapterNumber(FirstMeaningful(obj, LastChapterTitleKeys, nested));
        return lastChapterNumber > 0
            ? lastChapterNumber
            : FirstPositiveInt(obj, EpisodeKeys, nested);
    }

    private static int ParseLastChapterNumber(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return 0;
        }

        var match = LastChapterNumberRegex.Match(title);
        return match.Success && int.TryParse(match.Groups[1].Value, out var parsed)
            ? Math.Max(0, parsed)
            : 0;
    }

    private static int ReadPositiveInt(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return 0;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.Number)
            {
                return Math.Max(0, node.GetValue<int>());
            }

            var text = Meaningful(node.GetValue<string>());
            return int.TryParse(text, out var parsed) ? Math.Max(0, parsed) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static IEnumerable<long> IterTimestamps(string? value)
    {
        var text = Meaningful(value);
        if (text is null)
        {
            yield break;
        }

        if (text.Contains('T', StringComparison.Ordinal))
        {
            if (DateTimeOffset.TryParse(text.Replace("Z", "+00:00", StringComparison.Ordinal), out var iso))
            {
                yield return iso.ToUnixTimeSeconds();
                yield break;
            }
        }

        if (text.Length == 8 && text.All(char.IsDigit) && (text.StartsWith("19", StringComparison.Ordinal) || text.StartsWith("20", StringComparison.Ordinal)))
        {
            if (DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var cacheDate))
            {
                yield return new DateTimeOffset(cacheDate).ToUnixTimeSeconds();
                yield break;
            }
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) && numeric > 0)
        {
            yield return numeric > 1_000_000_000_000 ? numeric / 1000 : numeric;
            yield break;
        }

        foreach (var format in TimeFormats)
        {
            if (DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                yield return new DateTimeOffset(parsed).ToUnixTimeSeconds();
                yield break;
            }
        }

        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var loose))
        {
            yield return new DateTimeOffset(loose).ToUnixTimeSeconds();
        }
    }

    private static string? Meaningful(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text is "0" or "0.0" or "-" or "null" or "None")
        {
            return null;
        }

        return text;
    }

    private static string? ReadString(JsonObject? obj, string name)
    {
        if (obj is null || !obj.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>()?.Trim(),
            JsonValueKind.Number => node.ToJsonString(),
            _ => null
        };
    }
}
