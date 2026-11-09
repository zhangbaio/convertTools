using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>
/// 下载时以当前数据链路为准。红果 APP、红果短剧下载器和红果高清共用数字书号，只换前缀。
/// 项目里的原始 bookId 保持不变，各链路的解析结果另存在 sourceReferences。
/// </summary>
public static class DramaDownloadSourceSelector
{
    public const string DefaultChain = "playlet";

    private static readonly (string Source, string BookPrefix, string EpisodePrefix)[] Known =
    [
        ("mapleleaf", "mapleleaf:", "mapleleaf_ep:"),
        ("hglocal", "hglocal:", "hglocal_ep:"),
        ("downloader", "downloader:", "downloader_ep:"),
        ("hghigh", "hghigh:", "hghigh_ep:"),
        ("pikachu", "pikachu:", "pikachu_ep:"),
        ("playlet", "playlet:", "playlet_ep:"),
        ("hongguoapp", "hongguoapp:", "hongguoapp_ep:"),
        ("dramabox", "dramabox:", "dramabox_ep:"),
        ("foreign", "foreign:", "foreign_ep:"),
    ];

    private static readonly HashSet<string> SharedSeriesSources = new(StringComparer.Ordinal)
    {
        "hongguoapp",
        "playlet",
        "hghigh",
    };

    public static string Normalize(string? chain)
    {
        var value = (chain ?? string.Empty).Trim().ToLowerInvariant();
        return value.Length == 0 ? DefaultChain : value;
    }

    public static string Label(string? chain) => Normalize(chain) switch
    {
        "hghigh" => "红果高清",
        "mapleleaf" => "Mapleleaf",
        "hglocal" => "本地直连",
        "pikachu" => "皮卡丘",
        "playlet" => "红果短剧下载器",
        "hongguoapp" => "红果APP",
        "dramabox" => "DramaBox",
        "foreign" => "海外短剧下载器",
        "downloader" => "下载器网关",
        _ => "红果新接口",
    };

    public static string InferSource(string? bookId)
    {
        var id = (bookId ?? string.Empty).Trim();
        foreach (var known in Known)
        {
            if (id.StartsWith(known.BookPrefix, StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith(known.EpisodePrefix, StringComparison.OrdinalIgnoreCase))
                return known.Source;
        }

        return "hgnew";
    }

    public static bool IsRetired(string? chain)
    {
        var value = (chain ?? string.Empty).Trim().ToLowerInvariant();
        return value is "hgnew" or "mapleleaf" or "downloader" or "foreign";
    }

    /// <summary>
    /// 不需要搜索就能决定用哪个书号时返回该书号。需要按剧名重搜时返回 null。
    /// </summary>
    public static string? TryRetarget(string? bookId, string? selectedChain)
    {
        var id = (bookId ?? string.Empty).Trim();
        if (id.Length == 0)
            return null;

        var selected = Normalize(selectedChain);
        if (!HasKnownPrefix(id))
            return id;

        var source = InferSource(id);
        if (string.Equals(source, selected, StringComparison.Ordinal))
            return id;
        if (IsEpisodeId(id))
            return id;
        if (string.Equals(selected, DefaultChain, StringComparison.Ordinal) && IsRetired(source))
            return id;
        if (!SharedSeriesSources.Contains(source) || !SharedSeriesSources.Contains(selected))
            return null;

        var raw = StripBookPrefix(id);
        if (string.IsNullOrWhiteSpace(raw) || HasKnownPrefix(raw))
            return null;
        return BookPrefix(selected) + raw;
    }

    public static string? ReadCachedBookId(string? directory, string? chain)
    {
        var root = ReadProject(directory);
        var references = root?["sourceReferences"] as JsonObject;
        var reference = references?[Normalize(chain)] as JsonObject;
        return reference?["bookId"] is JsonValue value && value.TryGetValue<string>(out var bookId)
            ? bookId.Trim()
            : null;
    }

    public static void Remember(string? directory, string? chain, string? bookId, string? title)
    {
        var path = ProjectPath(directory);
        if (path is null || string.IsNullOrWhiteSpace(bookId))
            return;

        var root = ReadProject(directory);
        if (root is null)
            return;

        var references = root["sourceReferences"] as JsonObject;
        if (references is null)
        {
            references = new JsonObject();
            root["sourceReferences"] = references;
        }

        references[Normalize(chain)] = new JsonObject
        {
            ["bookId"] = bookId.Trim(),
            ["title"] = (title ?? string.Empty).Trim(),
            ["resolvedAt"] = DateTimeOffset.Now.ToString("o"),
        };

        var temporary = path + ".source-references.tmp";
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException)
        {
            // 书号缓存写不进去时，这一次下载仍使用已经解析出的书号。
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }

    public static int ReadEpisodeCount(string? directory)
    {
        var root = ReadProject(directory);
        var node = root?["episodeCount"] ?? root?["episode_count"];
        if (node is JsonValue value && value.TryGetValue<int>(out var count))
            return count;
        return int.TryParse(node?.ToString(), out var parsed) ? parsed : 0;
    }

    public static bool IsEpisodeId(string? bookId)
    {
        var id = (bookId ?? string.Empty).Trim();
        return id.Length > 0 && IsEpisodeIdCore(id);
    }

    private static bool HasKnownPrefix(string bookId)
    {
        foreach (var known in Known)
        {
            if (bookId.StartsWith(known.BookPrefix, StringComparison.OrdinalIgnoreCase) ||
                bookId.StartsWith(known.EpisodePrefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsEpisodeIdCore(string bookId)
    {
        foreach (var known in Known)
        {
            if (bookId.StartsWith(known.EpisodePrefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string BookPrefix(string chain)
    {
        foreach (var known in Known)
        {
            if (string.Equals(known.Source, chain, StringComparison.Ordinal))
                return known.BookPrefix;
        }

        return chain + ":";
    }

    private static string StripBookPrefix(string bookId)
    {
        foreach (var known in Known)
        {
            if (bookId.StartsWith(known.BookPrefix, StringComparison.OrdinalIgnoreCase))
                return bookId[known.BookPrefix.Length..];
        }

        return bookId;
    }

    private static string? ProjectPath(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;
        var path = Path.Combine(directory, "shortdrama-project.json");
        return File.Exists(path) ? path : null;
    }

    private static JsonObject? ReadProject(string? directory)
    {
        var path = ProjectPath(directory);
        if (path is null)
            return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
