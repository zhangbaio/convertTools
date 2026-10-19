using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TikTokPublisher.Core.Services;

public static partial class ArticlesChineseText
{
    public static string TranslateName(string? english) =>
        JoinName(Split(english).Select(TranslateToken));

    public static string TranslateShortName(string? english)
    {
        var parts = Split(english).Select(TranslateToken).ToArray();
        return parts.Length switch
        {
            0 => string.Empty,
            <= 2 => JoinName(parts),
            _ => $"{parts[0]}·{parts[^1]}",
        };
    }

    public static string TranslateStreet(string? english)
    {
        var text = (english ?? string.Empty).Trim();
        if (text.Length == 0)
            return string.Empty;

        var match = StreetPattern().Match(text);
        if (!match.Success)
            return string.Join(' ', Split(text).Select(TranslateToken));

        return $"{match.Groups[1].Value} {TranslateToken(match.Groups[2].Value)}{TranslateToken(match.Groups[3].Value)}";
    }

    public static string TranslateCity(string? english)
    {
        var text = (english ?? string.Empty).Trim();
        if (text.Length == 0)
            return string.Empty;

        var match = CityPattern().Match(text);
        if (!match.Success)
            return string.Join(' ', Split(text).Select(TranslateToken));

        return $"{TranslateToken(match.Groups[1].Value)}, {TranslateToken(match.Groups[2].Value)} {match.Groups[3].Value}";
    }

    public static string TranslateFiledAt(string? english)
    {
        var text = (english ?? string.Empty).Trim();
        if (!DateTime.TryParseExact(text, "MM/dd/yyyy hh:mm tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            return text;

        var meridiem = value.Hour >= 12 ? "下午" : "上午";
        var hour = value.Hour % 12;
        if (hour == 0)
            hour = 12;
        return $"{value:yyyy年MM月dd日} {meridiem}{hour:00}:{value:mm}";
    }

    public static string TranslateSignedOn(string? english)
    {
        var text = (english ?? string.Empty).Trim().TrimEnd('.');
        if (!DateTime.TryParseExact(text, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            return text;

        return $"{value.Year}年{value.Month}月{value.Day}日";
    }

    private static string JoinName(IEnumerable<string> parts) => string.Join("·", parts);

    private static IEnumerable<string> Split(string? text) =>
        (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static string TranslateToken(string token)
    {
        var key = token.Trim().Trim('.').ToUpperInvariant();
        if (key.Length == 0)
            return string.Empty;
        if (Lexicon.TryGetValue(key, out var translated))
            return translated;
        if (key.All(char.IsDigit))
            return key;
        return Transliterate(key);
    }

    private static string Transliterate(string word)
    {
        var lower = word.ToLowerInvariant();
        var result = new StringBuilder();
        var index = 0;
        while (index < lower.Length)
        {
            var matched = Syllables.FirstOrDefault(pair => lower.AsSpan(index).StartsWith(pair.Latin));
            if (matched.Latin is null)
            {
                result.Append(Letter(lower[index]));
                index++;
                continue;
            }

            result.Append(matched.Zh);
            index += matched.Latin.Length;
        }

        return result.ToString();
    }

    private static string Letter(char value) => value switch
    {
        'a' => "阿",
        'b' => "布",
        'c' => "克",
        'd' => "德",
        'e' => "伊",
        'f' => "夫",
        'g' => "格",
        'h' => "赫",
        'i' => "伊",
        'j' => "杰",
        'k' => "克",
        'l' => "尔",
        'm' => "姆",
        'n' => "恩",
        'o' => "奥",
        'p' => "普",
        'q' => "奎",
        'r' => "尔",
        's' => "斯",
        't' => "特",
        'u' => "尤",
        'v' => "夫",
        'w' => "威",
        'x' => "克斯",
        'y' => "伊",
        'z' => "兹",
        _ => string.Empty,
    };

    private static readonly (string Latin, string Zh)[] Syllables =
    [
        ("tion", "申"), ("ight", "艾特"), ("ough", "奥"),
        ("th", "斯"), ("ch", "奇"), ("sh", "什"), ("ph", "夫"),
        ("an", "安"), ("en", "恩"), ("in", "因"), ("on", "昂"), ("un", "恩"),
        ("ar", "尔"), ("er", "尔"), ("ir", "尔"), ("or", "奥"), ("ur", "尔"),
        ("ai", "艾"), ("ay", "埃"), ("ea", "伊"), ("ee", "伊"), ("oo", "乌"), ("ou", "奥"),
        ("a", "阿"), ("e", "伊"), ("i", "伊"), ("o", "奥"), ("u", "尤"), ("y", "伊"),
    ];

    private static readonly Dictionary<string, string> Lexicon = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ANTHONY"] = "安东尼", ["JAMES"] = "詹姆斯", ["MARIA"] = "玛丽亚", ["ROBERT"] = "罗伯特",
        ["LINDA"] = "琳达", ["DAVID"] = "戴维", ["SARAH"] = "萨拉", ["MICHAEL"] = "迈克尔",
        ["CLYDE"] = "克莱德", ["LEE"] = "李", ["ANN"] = "安", ["MARIE"] = "玛丽",
        ["RAY"] = "雷", ["JO"] = "乔", ["PAUL"] = "保罗",
        ["CLUNIS"] = "克卢尼斯", ["HARRIS"] = "哈里斯", ["WALKER"] = "沃克", ["BROOKS"] = "布鲁克斯",
        ["REED"] = "里德", ["COLEMAN"] = "科尔曼", ["HAYES"] = "海斯", ["FOSTER"] = "福斯特",
        ["OLOH"] = "奥洛", ["OAK"] = "橡树", ["MAPLE"] = "枫树", ["PINE"] = "松树",
        ["CEDAR"] = "雪松", ["HIGHLAND"] = "高地", ["LAKEVIEW"] = "湖景", ["RIVER"] = "河滨",
        ["RD"] = "路", ["ST"] = "街", ["AVE"] = "大道", ["DR"] = "路", ["LN"] = "巷",
        ["BLVD"] = "大道", ["CT"] = "巷", ["WAY"] = "路",
        ["SUMRALL"] = "萨姆罗尔", ["JACKSON"] = "杰克逊", ["HATTIESBURG"] = "哈蒂斯堡",
        ["GULFPORT"] = "格尔夫波特", ["AUSTIN"] = "奥斯汀", ["DALLAS"] = "达拉斯",
        ["ATLANTA"] = "亚特兰大", ["MEMPHIS"] = "孟菲斯", ["BIRMINGHAM"] = "伯明翰",
        ["NEW ORLEANS"] = "新奥尔良", ["BATON ROUGE"] = "巴吞鲁日", ["NASHVILLE"] = "纳什维尔",
        ["MS"] = "密西西比州", ["TX"] = "得克萨斯州", ["GA"] = "佐治亚州", ["TN"] = "田纳西州",
        ["AL"] = "亚拉巴马州", ["LA"] = "路易斯安那州", ["NC"] = "北卡罗来纳州", ["FL"] = "佛罗里达州",
    };

    [GeneratedRegex(@"^(\d+)\s+(.+?)\s+(Rd|St|Ave|Dr|Ln|Blvd|Ct|Way)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex StreetPattern();

    [GeneratedRegex(@"^(.+),\s*([A-Za-z]{2})\s+(\d{5})$")]
    private static partial Regex CityPattern();
}
