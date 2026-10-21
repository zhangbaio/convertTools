using System.Text.Json.Nodes;

namespace ShortDrama.Infrastructure.AI;

/// <summary>根据原剧名和原简介判断改写输出语言。中文保持原有字数规则。</summary>
public static class RewriteSourceLanguage
{
    public const string Chinese = "zh";
    public const string English = "en";
    public const string Indonesian = "id";
    public const string Portuguese = "pt";
    public const string Japanese = "ja";
    public const string Thai = "th";
    public const string Spanish = "es";
    public const string Korean = "ko";
    public const string Turkish = "tr";
    public const string Hindi = "hi";
    public const string CachePropertyName = "rewriteSourceLanguage";

    public static readonly IReadOnlySet<string> AllowedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Chinese, English, Indonesian, Portuguese, Japanese, Thai, Spanish, Korean, Turkish, Hindi,
    };

    public readonly record struct Limits(
        int TitleMin,
        int TitleMax,
        int TaglineMin,
        int TaglineMax,
        int SynopsisMin,
        int SynopsisMax);

    public static bool IsChinese(string? code) =>
        string.IsNullOrWhiteSpace(code) ||
        string.Equals(code.Trim(), Chinese, StringComparison.OrdinalIgnoreCase);

    public readonly record struct ImportedText(string Title, string Synopsis);

    public static string Detect(string? title, string? synopsis)
    {
        // 剧名本身已经能确定语言时，不用改写后的简介反过来覆盖。
        // 例如原剧名是英文，短剧信息里的简介却是上一轮生成的中文。
        var titleLanguage = Classify(title);
        if (!IsChinese(titleLanguage))
            return titleLanguage;

        return Classify($"{title}\n{synopsis}");
    }

    /// <summary>
    /// 读取导入时保存的原剧名和原简介。短剧信息里的简介可能已经被改写成中文。
    /// </summary>
    public static ImportedText ReadImported(string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
            return new ImportedText("", "");

        var title = "";
        var synopsis = "";
        ReadJsonFile(Path.Combine(projectDir, "shortdrama-project.json"), ref title, ref synopsis);
        ReadJsonFile(Path.Combine(projectDir, "metadata.json"), ref title, ref synopsis);
        ReadIntroFile(Path.Combine(projectDir, "简介.txt"), ref title, ref synopsis);
        var sourceDir = ReadJsonString(Path.Combine(projectDir, "shortdrama-project.json"), "sourceProjectDir");
        if (!string.IsNullOrWhiteSpace(sourceDir) && Directory.Exists(sourceDir))
        {
            ReadJsonFile(Path.Combine(sourceDir, "metadata.json"), ref title, ref synopsis);
            ReadIntroFile(Path.Combine(sourceDir, "简介.txt"), ref title, ref synopsis);
        }

        return new ImportedText(title, synopsis);
    }

    private static string Classify(string? text)
    {
        var cjk = 0;
        var latin = 0;
        var hangul = 0;
        var kana = 0;
        var thai = 0;
        foreach (var ch in text ?? "")
        {
            if (ch is >= '\u4E00' and <= '\u9FFF')
                cjk++;
            else if (ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))
                latin++;
            else if (ch is >= '\uAC00' and <= '\uD7AF')
                hangul++;
            else if (ch is (>= '\u3040' and <= '\u30FF'))
                kana++;
            else if (ch is >= '\u0E00' and <= '\u0E7F')
                thai++;
        }

        if (kana >= 2 && kana + cjk >= latin)
            return Japanese;
        if (hangul >= 2 && hangul >= cjk && hangul >= latin)
            return Korean;
        if (thai >= 2 && thai >= cjk && thai >= latin)
            return Thai;
        if (latin >= 8 && cjk == 0)
            return English;
        if (latin >= 8 && latin > cjk * 2)
            return English;
        return Chinese;
    }

    private static void ReadJsonFile(string path, ref string title, ref string synopsis)
    {
        if (!File.Exists(path))
            return;

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception)
        {
            return;
        }

        if (root is null)
            return;

        Prefer(ref title, JsonString(root, "originalTitle", "title", "sourceName", "displayName"));
        Prefer(ref synopsis, JsonString(root, "intro", "description", "desc"));
    }

    private static string ReadJsonString(string path, string name)
    {
        if (!File.Exists(path))
            return "";

        try
        {
            return (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?[name]?.ToString()?.Trim() ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static void ReadIntroFile(string path, ref string title, ref string synopsis)
    {
        if (!File.Exists(path))
            return;

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception)
        {
            return;
        }

        var parsedTitle = "";
        var parsedSynopsis = "";
        var synopsisLines = new List<string>();
        var inSynopsis = false;
        foreach (var raw in text.Split(['\r', '\n']))
        {
            var line = raw.Trim();
            if (line.StartsWith("标题", StringComparison.Ordinal) ||
                line.StartsWith("原剧名", StringComparison.Ordinal))
            {
                parsedTitle = ValueAfterLabel(line);
                inSynopsis = false;
                continue;
            }

            if (line.StartsWith("简介", StringComparison.Ordinal))
            {
                var inline = ValueAfterLabel(line);
                if (!string.IsNullOrWhiteSpace(inline))
                    synopsisLines.Add(inline);
                inSynopsis = true;
                continue;
            }

            if (line.StartsWith("集数", StringComparison.Ordinal))
            {
                inSynopsis = false;
                continue;
            }

            if (inSynopsis && line.Length > 0)
                synopsisLines.Add(line);
        }

        parsedSynopsis = string.Join(' ', synopsisLines).Trim();
        Prefer(ref title, parsedTitle);
        Prefer(ref synopsis, parsedSynopsis);
    }

    private static string ValueAfterLabel(string line)
    {
        var index = line.IndexOfAny([':', '：']);
        return index < 0 ? "" : line[(index + 1)..].Trim();
    }

    private static string JsonString(JsonObject root, params string[] names)
    {
        foreach (var name in names)
        {
            var value = root[name]?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private static void Prefer(ref string current, string incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming))
            return;
        if (string.IsNullOrWhiteSpace(current) || IsChinese(Classify(current)) && !IsChinese(Classify(incoming)))
            current = incoming.Trim();
    }

    public static string? ReadCached(string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
            return null;
        var cached = ReadJsonString(Path.Combine(projectDir, "shortdrama-project.json"), CachePropertyName);
        return TryNormalize(cached, out var code) ? code : null;
    }

    public static void SaveCached(string projectDir, string code)
    {
        if (!TryNormalize(code, out var normalized))
            return;

        var path = Path.Combine(projectDir, "shortdrama-project.json");
        JsonObject root;
        if (File.Exists(path))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            }
            catch (Exception)
            {
                root = new JsonObject();
            }
        }
        else
        {
            root = new JsonObject();
        }

        root[CachePropertyName] = normalized;
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public static string BuildDetectionPrompt(string? title, string? synopsis) => $"""
        判断下面短剧原剧名和原简介的语言。只返回一个语言代码，不要解释。
        只能返回：zh、en、id、pt、ja、th、es、ko、tr、hi。
        zh=中文，en=英语，id=印尼语，pt=葡萄牙语，ja=日语，th=泰语，es=西班牙语，ko=韩语，tr=土耳其语，hi=印地语。
        如果不是以上任何一种语言，只返回 unsupported。

        原剧名：{title}
        原简介：{synopsis}
        """;

    public static bool TryParseDetectedLanguage(string? content, out string code, out string failure)
    {
        code = "";
        var text = StripCodeFence(content);
        var token = FirstToken(text);
        if (TryNormalize(token, out var normalized))
        {
            code = normalized;
            failure = "";
            return true;
        }

        var shown = string.IsNullOrWhiteSpace(text) ? "空" : TrimForLog(text);
        failure = token.Equals("unsupported", StringComparison.OrdinalIgnoreCase)
            ? "改写失败：原剧名和原简介的语言不在允许范围内。允许中文、英语、印尼语、葡语、日语、泰语、西语、韩语、土耳其语、印地语。"
            : $"改写失败：源语言识别结果「{shown}」不在允许范围内。允许中文、英语、印尼语、葡语、日语、泰语、西语、韩语、土耳其语、印地语。";
        return false;
    }

    public static bool TryNormalize(string? code, out string normalized)
    {
        normalized = (code ?? string.Empty).Trim().ToLowerInvariant();
        if (!AllowedCodes.Contains(normalized))
        {
            normalized = "";
            return false;
        }

        return true;
    }

    public static string DisplayName(string code) => (code ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        English => "英语",
        Indonesian => "印尼语",
        Portuguese => "葡语",
        Japanese => "日语",
        Thai => "泰语",
        Spanish => "西语",
        Korean => "韩语",
        Turkish => "土耳其语",
        Hindi => "印地语",
        _ => "中文",
    };

    private static string StripCodeFence(string? content)
    {
        var text = (content ?? string.Empty).Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', lines.Where(line => !line.Trim().StartsWith("```", StringComparison.Ordinal))).Trim();
    }

    private static string FirstToken(string text)
    {
        var token = new string((text ?? string.Empty).Trim().TakeWhile(ch => char.IsLetter(ch) || ch == '-').ToArray());
        return token.Trim().ToLowerInvariant();
    }

    private static string TrimForLog(string text)
    {
        var compact = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 40 ? compact : compact[..40];
    }

    public static Limits GetLimits(string code) => IsChinese(code)
        ? new Limits(6, 15, 8, 20, 100, 300)
        : new Limits(8, 80, 12, 120, 40, 900);

    public static string Instruction(string code)
    {
        if (IsChinese(code))
            return "";

        var limits = GetLimits(code);
        return $"""

输出语言：
原剧名和原简介使用的语言就是本次输出语言。title、tagline、synopsis 必须全部使用这种语言，禁止翻译成中文。
当前识别为{DisplayName(code)}。
title 使用 {limits.TitleMin}-{limits.TitleMax} 个字符，tagline 使用 {limits.TaglineMin}-{limits.TaglineMax} 个字符，synopsis 使用 {limits.SynopsisMin}-{limits.SynopsisMax} 个字符。
不要套用中文短剧标题套路，不要输出中文宣传词。
""";
    }
}
