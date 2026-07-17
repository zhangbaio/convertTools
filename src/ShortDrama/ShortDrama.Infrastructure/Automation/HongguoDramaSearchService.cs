using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.Automation;

public sealed class HongguoDramaSearchService : IDramaSearchService
{
    private static readonly string[] PosterKeys =
    [
        "poster",
        "poster_url",
        "cover",
        "cover_url",
        "thumbnail",
        "thumbnail_url",
        "thumb",
        "thumb_url",
        "image",
        "image_url",
        "img",
        "img_url",
        "pic",
        "pic_url",
        "book_pic",
        "book_cover",
        "video_cover",
        "vertical_cover",
        "vertical_cover_url",
        "horizontal_cover",
        "horizontal_cover_url"
    ];

    private static readonly string[] PosterKeywords =
    [
        "poster",
        "cover",
        "thumb",
        "thumbnail",
        "image",
        "img",
        "pic"
    ];

    private static readonly Regex EpisodeCountRegex = new(@"(\d+)\s*集", RegexOptions.Compiled);
    private readonly HttpClient _httpClient;

    public HongguoDramaSearchService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        string keyword,
        int page,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return [];
        }

        var client = new HongguoApiClient(_httpClient, HongguoAccessOptionsResolver.Resolve(anchorDirectory: null));
        var items = await client.SearchAsync(keyword.Trim(), Math.Max(1, page), cancellationToken);
        return items
            .Select(MapItem)
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
            .ToArray();
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetTodayAsync(CancellationToken cancellationToken)
    {
        var client = new HongguoApiClient(_httpClient, HongguoAccessOptionsResolver.Resolve(anchorDirectory: null));
        var items = await client.GetTodayNewAsync(cancellationToken);
        return items
            .Select(MapItem)
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
            .ToArray();
    }

    private static DramaSearchItem MapItem(System.Text.Json.JsonElement item)
    {
        var bookId = ReadFirstNonEmpty(
            GetString(item, "book_id"),
            GetString(item, "id"));
        var title = ReadFirstNonEmpty(
            GetString(item, "title"),
            GetString(item, "book_name"),
            GetString(item, "name"),
            bookId);
        var category = ReadFirstNonEmpty(
            GetString(item, "type"),
            GetString(item, "tags"));
        var intro = ReadFirstNonEmpty(
            GetString(item, "intro"),
            GetString(item, "description"),
            GetString(item, "desc"));
        var author = ReadFirstNonEmpty(
            GetString(item, "author"),
            GetString(item, "producer"),
            GetString(item, "company"));
        var publishTime = ReadFirstNonEmpty(
            GetString(item, "publish_time"),
            GetString(item, "create_time"),
            GetString(item, "created_at"));

        return new DramaSearchItem(
            BookId: bookId,
            Title: title,
            Category: category,
            EpisodeTotal: ResolveEpisodeTotal(item, category),
            Intro: intro,
            PosterUrl: ExtractPosterUrl(item),
            Author: author,
            PublishTime: publishTime,
            FavoriteCount: GetInt(item, "favorite_count")
                ?? GetInt(item, "collect_count")
                ?? GetInt(item, "favorite")
                ?? 0);
    }

    private static int ResolveEpisodeTotal(System.Text.Json.JsonElement item, string category)
    {
        foreach (var key in new[] { "episode", "episode_total", "episode_cnt", "ji" })
        {
            var value = GetInt(item, key);
            if (value is > 0)
            {
                return value.Value;
            }
        }

        var match = EpisodeCountRegex.Match(category ?? string.Empty);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static string ExtractPosterUrl(System.Text.Json.JsonElement element)
    {
        foreach (var key in PosterKeys)
        {
            var direct = GetString(element, key);
            if (LooksLikeHttpUrl(direct))
            {
                return direct!;
            }
        }

        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (PosterKeywords.Any(keyword => property.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                {
                    var nested = ExtractPosterUrl(property.Value);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var nested = ExtractPosterUrl(property.Value);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = ExtractPosterUrl(item);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        if (element.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var value = element.GetString()?.Trim();
            if (LooksLikeHttpUrl(value))
            {
                return value!;
            }
        }

        return string.Empty;
    }

    private static bool LooksLikeHttpUrl(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetString(System.Text.Json.JsonElement element, string propertyName)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => value.GetString()?.Trim(),
            System.Text.Json.JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int? GetInt(System.Text.Json.JsonElement element, string propertyName)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        if (value.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(value.GetString(), out intValue))
        {
            return intValue;
        }

        return null;
    }

    private static string ReadFirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }
}
