using System.Globalization;
using System.Text;

namespace ShortDrama.Infrastructure.Files;

internal enum PosterTitleScript
{
    Chinese,
    Japanese,
    Korean,
    Thai,
    Arabic,
    Latin,
}

internal static partial class PosterTitleProgrammaticRenderer
{
    private const string JapaneseNoLineStart = "ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶー、。！？）」』】";
    private const string JapaneseNoLineEnd = "（「『【";
    private const string ThaiLeadingVowels = "เแโใไ";

    private static readonly string[] ChineseFonts =
    [
        "C:/Windows/Fonts/msyhbd.ttc",
        "C:/Windows/Fonts/msyh.ttc",
        "C:/Windows/Fonts/simhei.ttf",
        "C:/Windows/Fonts/simsun.ttc",
        "/System/Library/Fonts/PingFang.ttc",
        "/System/Library/Fonts/STHeiti Medium.ttc",
    ];

    private static readonly string[] JapaneseFonts =
    [
        "C:/Windows/Fonts/YuGothB.ttc",
        "C:/Windows/Fonts/YuGothM.ttc",
        "C:/Windows/Fonts/meiryob.ttc",
        "C:/Windows/Fonts/msyhbd.ttc",
    ];

    private static readonly string[] KoreanFonts =
    [
        "C:/Windows/Fonts/malgunbd.ttf",
        "C:/Windows/Fonts/malgun.ttf",
        "C:/Windows/Fonts/msyhbd.ttc",
    ];

    private static readonly string[] ThaiFonts =
    [
        "C:/Windows/Fonts/LeelaUIb.ttf",
        "C:/Windows/Fonts/LeelawUI.ttf",
        "C:/Windows/Fonts/msyhbd.ttc",
    ];

    private static readonly string[] ArabicFonts =
    [
        "C:/Windows/Fonts/segoeuib.ttf",
        "C:/Windows/Fonts/tahomabd.ttf",
        "C:/Windows/Fonts/arialbd.ttf",
        "C:/Windows/Fonts/segoeui.ttf",
    ];

    private static readonly string[] LatinFonts =
    [
        "C:/Windows/Fonts/segoeuib.ttf",
        "C:/Windows/Fonts/arialbd.ttf",
        "C:/Windows/Fonts/segoeui.ttf",
        "C:/Windows/Fonts/arial.ttf",
    ];

    private readonly record struct TitleToken(string Text, bool CanBreakBefore);

    internal static PosterTitleScript ClassifyTitleScript(string? title)
    {
        var han = 0;
        var kana = 0;
        var hangul = 0;
        var thai = 0;
        var arabic = 0;
        var latin = 0;
        foreach (var character in title ?? string.Empty)
        {
            if (character is >= '\u4E00' and <= '\u9FFF')
                han++;
            else if (character is >= '\u3040' and <= '\u30FF' or >= '\u31F0' and <= '\u31FF' or >= '\uFF66' and <= '\uFF9D')
                kana++;
            else if (character is >= '\uAC00' and <= '\uD7AF' or >= '\u1100' and <= '\u11FF' or >= '\u3130' and <= '\u318F')
                hangul++;
            else if (character is >= '\u0E00' and <= '\u0E7F')
                thai++;
            else if (IsRightToLeftLetter(character))
                arabic++;
            else if (IsLatinLetter(character))
                latin++;
        }

        if (kana > 0)
            return PosterTitleScript.Japanese;
        if (thai > 0)
            return PosterTitleScript.Thai;
        if (arabic > 0)
            return PosterTitleScript.Arabic;
        if (hangul > 0)
            return PosterTitleScript.Korean;
        if (latin > 0 && han == 0)
            return PosterTitleScript.Latin;
        if (latin >= 4 && latin >= han)
            return PosterTitleScript.Latin;
        if (han > 0)
            return PosterTitleScript.Chinese;
        return PosterTitleScript.Latin;
    }

    internal static string FormatMeasuredPosterLines(string? title, float maxWidth, Func<string, float> measure)
    {
        var original = NormalizeNewlines(title);
        if (original.Length == 0)
            return string.Empty;

        var script = ClassifyTitleScript(original);
        if (script == PosterTitleScript.Chinese || maxWidth <= 0)
            return FormatBalancedPosterLines(original);

        var prepared = PreparePosterTitle(original, script);
        var lines = new List<string>();
        foreach (var paragraph in prepared.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            lines.AddRange(WrapParagraph(paragraph, script, maxWidth, measure));
        return lines.Count == 0 ? prepared : string.Join('\n', lines);
    }

    private static string FormatBalancedPosterLines(string? title)
    {
        var original = NormalizeNewlines(title);
        if (original.Length == 0)
            return string.Empty;

        var script = ClassifyTitleScript(original);
        if (script == PosterTitleScript.Chinese)
            return FormatTitleLines(original);

        var prepared = PreparePosterTitle(original, script);
        var lines = new List<string>();
        foreach (var paragraph in prepared.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            lines.AddRange(BalanceParagraph(paragraph, script));
        return lines.Count == 0 ? prepared : string.Join('\n', lines);
    }

    private static string PreparePosterTitle(string title, PosterTitleScript script)
    {
        var text = script == PosterTitleScript.Latin ? RestoreCamelCaseSpaces(title) : title;
        return CollapseSpaces(text);
    }

    private static string RestoreCamelCaseSpaces(string text)
    {
        if (text.Contains(' ') || text.Contains('\n'))
            return text;

        var builder = new StringBuilder(text.Length + 8);
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (index > 0 && char.IsUpper(current))
            {
                var previous = text[index - 1];
                var nextIsLower = index + 1 < text.Length && char.IsLower(text[index + 1]);
                if (char.IsLower(previous) || (char.IsUpper(previous) && nextIsLower))
                    builder.Append(' ');
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    private static List<string> WrapParagraph(string text, PosterTitleScript script, float maxWidth, Func<string, float> measure)
    {
        var tokens = Tokenize(text, script);
        if (tokens.Count == 0)
            return [];

        var separator = UsesWordSpaces(text, script) ? " " : string.Empty;
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var token in tokens)
        {
            if (current.Length == 0)
            {
                current.Append(token.Text);
                continue;
            }

            var joiner = token.CanBreakBefore ? separator : string.Empty;
            var candidate = current.ToString() + joiner + token.Text;
            if (token.CanBreakBefore && measure(candidate) > maxWidth)
            {
                lines.Add(current.ToString());
                current.Clear();
                current.Append(token.Text);
                continue;
            }

            current.Append(joiner);
            current.Append(token.Text);
        }

        if (current.Length > 0)
            lines.Add(current.ToString());
        return lines;
    }

    private static List<string> BalanceParagraph(string text, PosterTitleScript script)
    {
        var tokens = Tokenize(text, script);
        if (tokens.Count <= 1)
            return [text];
        if (UsesWordSpaces(text, script))
            return BalanceWords(tokens);
        if (tokens.Count <= 6)
            return [string.Concat(tokens.Select(token => token.Text))];

        var lineCount = tokens.Count <= 16 ? 2 : 3;
        return BalanceGlued(tokens, lineCount);
    }

    private static List<string> BalanceWords(List<TitleToken> tokens)
    {
        var words = tokens.Select(token => token.Text).ToArray();
        var lineCount = words.Length <= 4 ? 2 : 3;
        var lines = new List<string>(lineCount);
        var target = Math.Max(1, (int)Math.Ceiling(words.Length / (double)lineCount));
        var index = 0;
        for (var lineIndex = 0; lineIndex < lineCount && index < words.Length; lineIndex++)
        {
            var remainingLines = lineCount - lineIndex;
            var take = lineIndex == lineCount - 1
                ? words.Length - index
                : Math.Max(1, Math.Min(target, words.Length - index - (remainingLines - 1)));
            lines.Add(string.Join(' ', words.Skip(index).Take(take)));
            index += take;
        }

        return lines;
    }

    private static List<string> BalanceGlued(List<TitleToken> tokens, int lineCount)
    {
        var lines = new List<string>();
        var index = 0;
        for (var line = 0; line < lineCount && index < tokens.Count; line++)
        {
            var remainingLines = lineCount - line;
            if (remainingLines <= 1)
            {
                lines.Add(ConcatTokens(tokens, index, tokens.Count));
                break;
            }

            var remaining = tokens.Count - index;
            var ideal = index + (int)Math.Ceiling(remaining / (double)remainingLines);
            var split = NearestBreak(tokens, index + 1, tokens.Count - (remainingLines - 1), ideal);
            if (split <= index)
            {
                lines.Add(ConcatTokens(tokens, index, tokens.Count));
                break;
            }

            lines.Add(ConcatTokens(tokens, index, split));
            index = split;
        }

        return lines;
    }

    private static int NearestBreak(List<TitleToken> tokens, int min, int max, int ideal)
    {
        var best = -1;
        var bestDistance = int.MaxValue;
        var limit = Math.Min(max, tokens.Count - 1);
        for (var index = Math.Max(1, min); index <= limit; index++)
        {
            if (!tokens[index].CanBreakBefore)
                continue;

            var distance = Math.Abs(index - ideal);
            if (distance >= bestDistance)
                continue;

            best = index;
            bestDistance = distance;
        }

        return best;
    }

    private static List<TitleToken> Tokenize(string text, PosterTitleScript script)
    {
        if (UsesWordSpaces(text, script))
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var tokens = new List<TitleToken>(words.Length);
            for (var index = 0; index < words.Length; index++)
                tokens.Add(new TitleToken(words[index], index > 0));
            return tokens;
        }

        var graphemes = EnumerateGraphemes(text);
        var units = new List<TitleToken>(graphemes.Count);
        for (var index = 0; index < graphemes.Count; index++)
            units.Add(new TitleToken(graphemes[index], CanBreakBefore(graphemes, index, script)));
        return units;
    }

    private static bool UsesWordSpaces(string text, PosterTitleScript script) =>
        script is PosterTitleScript.Latin or PosterTitleScript.Arabic
        || (script is PosterTitleScript.Korean or PosterTitleScript.Japanese && text.Contains(' '));

    private static bool CanBreakBefore(IReadOnlyList<string> graphemes, int index, PosterTitleScript script)
    {
        if (index <= 0)
            return false;

        var current = graphemes[index];
        var previous = graphemes[index - 1];
        if (script == PosterTitleScript.Japanese)
        {
            if (StartsWithSet(current, JapaneseNoLineStart) || StartsWithSet(previous, JapaneseNoLineEnd))
                return false;
        }

        if (script == PosterTitleScript.Thai && StartsWithSet(previous, ThaiLeadingVowels))
            return false;

        return true;
    }

    private static bool StartsWithSet(string text, string set) =>
        text.Length > 0 && set.Contains(text[0]);

    private static List<string> EnumerateGraphemes(string text)
    {
        var graphemes = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (element.Length > 0)
                graphemes.Add(element);
        }

        return graphemes;
    }

    private static string ConcatTokens(List<TitleToken> tokens, int start, int end)
    {
        var builder = new StringBuilder();
        for (var index = start; index < end && index < tokens.Count; index++)
            builder.Append(tokens[index].Text);
        return builder.ToString();
    }

    private static string CollapseSpaces(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (character is ' ' or '\t' or '\u00A0' or '\u3000')
            {
                pendingSpace = builder.Length > 0 && builder[^1] != '\n';
                continue;
            }

            if (character == '\n')
            {
                pendingSpace = false;
                if (builder.Length > 0 && builder[^1] != '\n')
                    builder.Append('\n');
                continue;
            }

            if (pendingSpace)
                builder.Append(' ');
            pendingSpace = false;
            builder.Append(character);
        }

        return builder.ToString().Trim();
    }

    private static string NormalizeNewlines(string? title) =>
        (title ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Trim();

    private static bool IsLatinLetter(char character) =>
        character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '\u00C0' and <= '\u024F') or (>= '\u1E00' and <= '\u1EFF');

    private static bool IsRightToLeftLetter(char character) =>
        character is (>= '\u0590' and <= '\u05FF')
            or (>= '\u0600' and <= '\u06FF')
            or (>= '\u0750' and <= '\u077F')
            or (>= '\uFB50' and <= '\uFDFF')
            or (>= '\uFE70' and <= '\uFEFF');

    private static IEnumerable<string> FontFilesFor(PosterTitleScript script) => script switch
    {
        PosterTitleScript.Japanese => JapaneseFonts,
        PosterTitleScript.Korean => KoreanFonts,
        PosterTitleScript.Thai => ThaiFonts,
        PosterTitleScript.Arabic => ArabicFonts,
        PosterTitleScript.Latin => LatinFonts,
        _ => ChineseFonts,
    };

    private static IEnumerable<string> FontFamilyNamesFor(PosterTitleScript script) => script switch
    {
        PosterTitleScript.Japanese => ["Yu Gothic", "Meiryo", "Microsoft YaHei"],
        PosterTitleScript.Korean => ["Malgun Gothic", "Microsoft YaHei"],
        PosterTitleScript.Thai => ["Leelawadee UI", "Tahoma", "Microsoft YaHei"],
        PosterTitleScript.Arabic => ["Segoe UI", "Tahoma", "Arial"],
        PosterTitleScript.Latin => ["Segoe UI", "Arial", "Microsoft YaHei"],
        _ => ["Microsoft YaHei", "SimHei", "PingFang SC", "Arial Unicode MS"],
    };
}
