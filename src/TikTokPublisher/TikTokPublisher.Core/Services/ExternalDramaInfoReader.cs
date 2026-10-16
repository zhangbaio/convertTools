using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TikTokPublisher.Core.Services;

internal sealed record ExternalDramaInfo(
    string Title,
    string Intro,
    string Category,
    int DeclaredEpisodeCount,
    string? IntroPath,
    string IntroSource);

internal static partial class ExternalDramaInfoReader
{
    private static readonly string[] PlainIntroFileCandidates =
    [
        "简介.txt", "剧情简介.txt", "剧情.txt", "介绍.txt", "信息.txt",
    ];

    private static readonly string[] DetailInfoFileCandidates =
    [
        "详细简介.txt", "短剧信息.txt",
    ];

    private static readonly string[] MetadataIntroKeys =
    [
        "intro", "description", "desc", "简介", "剧情简介", "详细简介", "介绍",
    ];

    private static readonly Dictionary<string, string> DetailKeyAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["剧名"] = "title",
            ["剧集名称"] = "title",
            ["标题"] = "title",
            ["原剧名"] = "title",
            ["作者"] = "author",
            ["类型"] = "category",
            ["分类"] = "category",
            ["题材"] = "category",
            ["集数"] = "episode_count",
            ["总集数"] = "episode_count",
            ["简介"] = "intro",
            ["剧情简介"] = "intro",
            ["详细简介"] = "intro",
            ["发布时间"] = "published_at",
            ["发布日期"] = "published_at",
        };

    static ExternalDramaInfoReader() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    internal static ExternalDramaInfo Read(string projectDirectory, JsonObject? metadata = null)
    {
        var root = Path.GetFullPath(projectDirectory);
        metadata ??= new JsonObject();
        var metadataIntro = ReadMetadataIntro(metadata);
        var metadataTitle = FirstNonEmpty(
            ReadString(metadata, "displayName"),
            ReadString(metadata, "title"),
            ReadString(metadata, "originalTitle"),
            ReadString(metadata, "sourceName"));
        var metadataCategory = FirstNonEmpty(
            ReadString(metadata, "category"),
            ReadString(metadata, "type"));
        var metadataEpisodeCount = ParseEpisodeCount(
            FirstNonEmpty(ReadString(metadata, "episodeCount"), ReadString(metadata, "episode_count")));

        var detailPath = FirstExistingFile(root, DetailInfoFileCandidates);
        var detailFields = ParseDetailedText(ReadTextFile(detailPath));
        var plainPath = FirstNonEmptyTextFile(root, PlainIntroFileCandidates);
        var plainText = ReadTextFile(plainPath);
        var plainFields = ParseDetailedText(plainText);
        var plainIntro = ExtractIntro(plainText);

        var intro = FirstNonEmpty(
            metadataIntro,
            plainIntro,
            detailFields.GetValueOrDefault("intro"));
        string? introPath = null;
        var introSource = "";
        if (!string.IsNullOrWhiteSpace(metadataIntro))
        {
            introSource = "shortdrama-project.json";
        }
        else if (!string.IsNullOrWhiteSpace(plainIntro))
        {
            introPath = plainPath;
            introSource = Path.GetFileName(plainPath) ?? "";
        }
        else if (!string.IsNullOrWhiteSpace(intro))
        {
            introPath = detailPath;
            introSource = detailPath is null ? "" : $"{Path.GetFileName(detailPath)}:简介";
        }

        if (string.IsNullOrWhiteSpace(intro))
        {
            var fallback = SingleFallbackTextFile(root);
            var fallbackIntro = ExtractIntro(ReadTextFile(fallback));
            if (!string.IsNullOrWhiteSpace(fallbackIntro))
            {
                intro = fallbackIntro;
                introPath = fallback;
                introSource = Path.GetFileName(fallback) ?? "";
            }
        }

        return new ExternalDramaInfo(
            Title: FirstNonEmpty(
                metadataTitle,
                plainFields.GetValueOrDefault("title"),
                detailFields.GetValueOrDefault("title")),
            Intro: Limit(intro, 4000),
            Category: FirstNonEmpty(
                metadataCategory,
                plainFields.GetValueOrDefault("category"),
                detailFields.GetValueOrDefault("category")),
            DeclaredEpisodeCount: Math.Max(
                metadataEpisodeCount,
                Math.Max(
                    ParseEpisodeCount(plainFields.GetValueOrDefault("episode_count")),
                    ParseEpisodeCount(detailFields.GetValueOrDefault("episode_count")))),
            IntroPath: introPath,
            IntroSource: introSource);
    }

    internal static string ExtractIntro(string? value)
    {
        var text = Normalize(value);
        if (string.IsNullOrWhiteSpace(text) || IsPlaceholderIntro(text)) return "";
        var fields = ParseDetailedText(text);
        if (fields.Count == 0) return text;
        return fields.GetValueOrDefault("intro")?.Trim() ?? "";
    }

    internal static IReadOnlyDictionary<string, string> ParseDetailedText(string? value)
    {
        var text = Normalize(value);
        if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, string>();
        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var activeKey = "";
        var recognized = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var match = DetailLineRegex().Match(rawLine);
            var sourceKey = match.Success ? match.Groups["key"].Value.Trim() : "";
            if (match.Success && DetailKeyAliases.TryGetValue(sourceKey, out var normalizedKey))
            {
                activeKey = normalizedKey;
                recognized++;
                if (!fields.TryGetValue(activeKey, out var lines))
                    fields[activeKey] = lines = [];
                lines.Add(match.Groups["value"].Value.Trim());
                continue;
            }
            if (activeKey == "intro")
                fields[activeKey].Add(rawLine.Trim());
        }
        if (recognized == 0) return new Dictionary<string, string>();
        return fields
            .Select(pair => new { pair.Key, Value = string.Join('\n', pair.Value).Trim() })
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsPlaceholderIntro(string? value)
    {
        var text = Normalize(value);
        return string.IsNullOrWhiteSpace(text) ||
               text.EndsWith("，待补充简介。", StringComparison.Ordinal) ||
               text.Equals("待补充简介", StringComparison.Ordinal) ||
               text.Equals("待补充简介。", StringComparison.Ordinal);
    }

    internal static string? FindIntroPath(string projectDirectory) =>
        Read(projectDirectory, new JsonObject()).IntroPath;

    private static string ReadMetadataIntro(JsonObject metadata)
    {
        foreach (var key in MetadataIntroKeys)
        {
            var intro = ExtractIntro(ReadString(metadata, key));
            if (!string.IsNullOrWhiteSpace(intro)) return intro;
        }
        return "";
    }

    private static string? FirstExistingFile(string projectDirectory, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(projectDirectory, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string? FirstNonEmptyTextFile(string projectDirectory, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(projectDirectory, name);
            if (File.Exists(path) && !string.IsNullOrWhiteSpace(ReadTextFile(path))) return path;
        }
        return null;
    }

    private static string? SingleFallbackTextFile(string projectDirectory)
    {
        var files = Directory.EnumerateFiles(projectDirectory, "*.txt", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return !name.StartsWith(".", StringComparison.Ordinal) &&
                       !string.Equals(name, "短剧信息.txt", StringComparison.OrdinalIgnoreCase) &&
                       !name.StartsWith(".weixin-channel", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return files.Length == 1 ? files[0] : null;
    }

    private static string ReadTextFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
        foreach (var encoding in EnumerateEncodings())
        {
            try { return Normalize(File.ReadAllText(path, encoding)); }
            catch (DecoderFallbackException) { }
            catch (IOException) { }
        }
        return "";
    }

    private static IEnumerable<Encoding> EnumerateEncodings()
    {
        yield return new UTF8Encoding(false, true);
        foreach (var name in new[] { "gb18030", "gbk" })
        {
            Encoding? encoding = null;
            try { encoding = Encoding.GetEncoding(name); } catch { }
            if (encoding is not null) yield return encoding;
        }
    }

    private static int ParseEpisodeCount(string? value)
    {
        var match = EpisodeCountRegex().Match(value ?? "");
        return match.Success && int.TryParse(match.Value, out var count) ? count : 0;
    }

    private static string ReadString(JsonObject metadata, string key)
    {
        if (!metadata.TryGetPropertyValue(key, out var node) || node is null) return "";
        try { return node.GetValue<string>()?.Trim() ?? ""; }
        catch { return node.ToJsonString().Trim('"').Trim(); }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static string Normalize(string? value) =>
        (value ?? "").TrimStart('\ufeff').Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static string Limit(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Length <= maximum ? value : value[..maximum];

    [GeneratedRegex(@"^\s*(?<key>[^：:\r\n]{1,20})\s*[：:]\s*(?<value>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex DetailLineRegex();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeCountRegex();
}
