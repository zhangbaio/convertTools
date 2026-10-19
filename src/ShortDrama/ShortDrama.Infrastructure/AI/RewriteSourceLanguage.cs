using System.Text.Json.Nodes;

namespace ShortDrama.Infrastructure.AI;

/// <summary>根据原剧名和原简介判断改写输出语言。中文保持原有字数规则。</summary>
public static class RewriteSourceLanguage
{
    public const string Chinese = "zh";
    public const string English = "en";
    public const string Japanese = "ja";
    public const string Korean = "ko";
    public const string Thai = "th";

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

    public static string DisplayName(string code) => code switch
    {
        English => "英语",
        Japanese => "日语",
        Korean => "韩语",
        Thai => "泰语",
        _ => "中文",
    };

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
