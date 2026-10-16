using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TikTokPublisher.Core.Services;

internal sealed record ExternalDramaInfo(
    string Title,
    string Intro,
    string Category,
    int DeclaredEpisodeCount,
    string PosterUrl,
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

    private static readonly string[] DownloaderFolderPrefixes = ["红果_"];

    private static readonly HashSet<string> WeakIntroLabels = new(StringComparer.Ordinal)
    {
        "短 剧 简 介",
        "短剧简介",
    };

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
            ["封面链接"] = "poster_url",
            ["封面地址"] = "poster_url",
            ["海报链接"] = "poster_url",
            ["海报地址"] = "poster_url",
        };

    static ExternalDramaInfoReader() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    internal static ExternalDramaInfo Read(string projectDirectory, JsonObject? metadata = null)
    {
        var root = Path.GetFullPath(projectDirectory);
        var folderName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        metadata ??= new JsonObject();
        var metadataIntro = ReadMetadataIntro(metadata);
        if (IsWeakIntroText(metadataIntro))
            metadataIntro = "";

        // 本地导入常把文件夹名写进 displayName（例如「红果_xxx」）；这类占位剧名不能压过简介里的【剧名】。
        var metadataTitle = FirstNonEmpty(
            ReadString(metadata, "displayName"),
            ReadString(metadata, "title"),
            ReadString(metadata, "originalTitle"),
            ReadString(metadata, "sourceName"));
        if (string.Equals(metadataTitle, folderName, StringComparison.Ordinal))
            metadataTitle = "";
        var metadataCategory = FirstNonEmpty(
            ReadString(metadata, "category"),
            ReadString(metadata, "type"));
        var metadataEpisodeCount = ParseEpisodeCount(
            FirstNonEmpty(ReadString(metadata, "episodeCount"), ReadString(metadata, "episode_count")));
        var metadataPosterUrl = FirstNonEmpty(
            ReadString(metadata, "posterUrl"),
            ReadString(metadata, "poster_url"),
            ReadString(metadata, "cover_url"));

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
            PosterUrl: FirstNonEmpty(
                metadataPosterUrl,
                plainFields.GetValueOrDefault("poster_url"),
                detailFields.GetValueOrDefault("poster_url")),
            IntroPath: introPath,
            IntroSource: introSource);
    }

    internal static string ExtractIntro(string? value)
    {
        var text = Normalize(value);
        if (string.IsNullOrWhiteSpace(text) || IsPlaceholderIntro(text)) return "";
        var fields = ParseDetailedText(text);
        if (fields.Count == 0)
            return IsWeakIntroText(text) ? "" : text;
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
            var line = rawLine.Trim();
            if (TrySplitDetailFieldLine(rawLine, out var sourceKey, out var fieldValue))
            {
                if (DetailKeyAliases.TryGetValue(sourceKey, out var normalizedKey))
                {
                    activeKey = normalizedKey;
                    recognized++;
                    if (!fields.TryGetValue(activeKey, out var lines))
                        fields[activeKey] = lines = [];
                    lines.Add(fieldValue);
                    continue;
                }

                // 形如【封面链接】的未知字段，结束当前多行简介收集，避免把尾部版权信息拼进简介。
                activeKey = "";
                continue;
            }

            if (DecorativeLineRegex().IsMatch(line))
            {
                if (activeKey == "intro")
                    activeKey = "";
                continue;
            }

            if (activeKey == "intro")
            {
                if (!fields.TryGetValue(activeKey, out var lines))
                    fields[activeKey] = lines = [];
                lines.Add(line);
            }
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

    internal static bool IsWeakIntroText(string? value)
    {
        var text = Normalize(value);
        if (string.IsNullOrWhiteSpace(text)) return true;
        var lines = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length == 0) return true;
        return lines.All(line =>
            DecorativeLineRegex().IsMatch(line) || WeakIntroLabels.Contains(line));
    }

    internal static bool ShouldReplaceExternalIntro(string? existingIntro)
    {
        var text = Normalize(existingIntro);
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (IsPlaceholderIntro(text)) return true;
        if (IsWeakIntroText(text)) return true;
        var fields = ParseDetailedText(text);
        if (fields.TryGetValue("intro", out var introField))
            return !string.Equals(introField.Trim(), text, StringComparison.Ordinal);
        return fields.Count > 0;
    }

    internal static string TitleFromFolderName(string? name)
    {
        var text = (name ?? "").Trim();
        foreach (var prefix in DownloaderFolderPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length)
            {
                var stripped = text[prefix.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(stripped))
                    return stripped;
            }
        }

        return text;
    }

    internal static string AuthoritativeProjectTitle(string? value, string projectDirName, string? preferredTitle = null)
    {
        var text = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) return "";
        var preferred = (preferredTitle ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(preferred) &&
            string.Equals(text, projectDirName, StringComparison.Ordinal))
        {
            return "";
        }

        return text;
    }

    internal static bool ShouldReplaceSeedTitle(
        string? existing,
        string projectDirName,
        string sourceDirName,
        string title)
    {
        var text = (existing ?? "").Trim();
        var target = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (string.IsNullOrWhiteSpace(target) || string.Equals(text, target, StringComparison.Ordinal))
            return false;

        var sourceName = string.IsNullOrWhiteSpace(sourceDirName) ? projectDirName : sourceDirName.Trim();
        var seedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            projectDirName,
            projectDirName.TrimStart('_'),
            sourceName,
            sourceName.TrimStart('_'),
            "_" + sourceName.TrimStart('_'),
        };
        if (seedNames.Contains(text)) return true;

        var stripped = TitleFromFolderName(text);
        return !string.IsNullOrWhiteSpace(stripped) &&
               !string.Equals(stripped, text, StringComparison.Ordinal) &&
               string.Equals(stripped, target, StringComparison.Ordinal);
    }

    internal static string? FindIntroPath(string projectDirectory) =>
        Read(projectDirectory, new JsonObject()).IntroPath;

    private static bool TrySplitDetailFieldLine(string rawLine, out string sourceKey, out string fieldValue)
    {
        var bracket = BracketLineRegex().Match(rawLine);
        if (bracket.Success)
        {
            sourceKey = NormalizeDetailKey(bracket.Groups["key"].Value);
            fieldValue = bracket.Groups["value"].Value.Trim();
            return true;
        }

        var colon = DetailLineRegex().Match(rawLine);
        if (colon.Success)
        {
            sourceKey = NormalizeDetailKey(colon.Groups["key"].Value);
            fieldValue = colon.Groups["value"].Value.Trim();
            return true;
        }

        sourceKey = "";
        fieldValue = "";
        return false;
    }

    private static string NormalizeDetailKey(string? value) =>
        KeyWhitespaceRegex().Replace((value ?? "").Trim(), "");

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

    // 兼容「剧名：xxx」；排除【以免和括号字段冲突。
    [GeneratedRegex(@"^\s*(?<key>[^：:\r\n【]{1,40})\s*[：:]\s*(?<value>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex DetailLineRegex();

    // 红果下载器：【剧　　名】xxx / 【剧情简介】
    [GeneratedRegex(@"^\s*【(?<key>[^】]{1,40})】(?<value>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex BracketLineRegex();

    [GeneratedRegex(@"^[\s\-_=─━═＊*]{3,}$", RegexOptions.CultureInvariant)]
    private static partial Regex DecorativeLineRegex();

    [GeneratedRegex(@"[\s\u3000]+", RegexOptions.CultureInvariant)]
    private static partial Regex KeyWhitespaceRegex();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeCountRegex();
}
