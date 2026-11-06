using System.Globalization;
using System.Text.RegularExpressions;
using NPinyin;

namespace LicenseTranslate.Core;

public static class BusinessLicenseTranslator
{
    public static void ApplyFixed(BusinessLicenseDraft draft)
    {
        var individual = draft.Kind == "个体工商户";
        draft.EnglishPersonRole = "Legal Representative";
        draft.EnglishPersonName = PersonName(draft.PersonName);
        draft.EnglishExtraLabel = individual ? "Form of Organization" : "Registered Capital";
        draft.EnglishExtraValue = individual ? OrganizationForm(draft.ExtraValue) : Capital(draft.ExtraValue);
        draft.EnglishDateLabel = individual ? "Registration Date" : "Date of Establishment";
        draft.EnglishDateValue = DateText(draft.DateValue);
        draft.EnglishAddressLabel = individual ? "Place of Business" : "Domicile";
        draft.EnglishIssueDate = DateText(draft.IssueDate);
        draft.EnglishDuplicateNo = string.IsNullOrWhiteSpace(draft.DuplicateNo) ? "" : draft.DuplicateNo;
    }

    public static string PersonName(string chinese)
    {
        chinese = chinese.Trim();
        if (chinese.Length == 0) return "";
        var surname = chinese[..1] == "吕" ? "Lyu" : Title(Pinyin.GetPinyin(chinese[..1]));
        var given = chinese.Length == 1 ? "" : Title(Pinyin.GetPinyin(chinese[1..]).Replace(" ", ""));
        return string.IsNullOrEmpty(given) ? surname : $"{surname} {given}";
    }

    private static string OrganizationForm(string value) => value.Trim() switch
    {
        "个人经营" => "Individual Operation",
        "家庭经营" => "Family Operation",
        _ => value.Trim(),
    };

    private static string Capital(string value)
    {
        var arabic = Regex.Match(value, @"(\d+(?:\.\d+)?)万");
        if (arabic.Success)
            return FormatCapital(decimal.Parse(arabic.Groups[1].Value, CultureInfo.InvariantCulture));
        var chinese = Regex.Match(value, @"([零〇一二三四五六七八九十百千万壹贰叁肆伍陆柒捌玖拾佰仟]+)万");
        if (!chinese.Success) return value.Trim();
        return FormatCapital(ChineseNumber(chinese.Groups[1].Value));
    }

    private static string FormatCapital(decimal wan) =>
        "CNY " + (wan * 10000m).ToString("N0", CultureInfo.InvariantCulture);

    private static decimal ChineseNumber(string text)
    {
        var digits = new Dictionary<char, int>
        {
            ['零'] = 0, ['〇'] = 0, ['一'] = 1, ['二'] = 2, ['三'] = 3, ['四'] = 4, ['五'] = 5, ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9,
            ['壹'] = 1, ['贰'] = 2, ['叁'] = 3, ['肆'] = 4, ['伍'] = 5, ['陆'] = 6, ['柒'] = 7, ['捌'] = 8, ['玖'] = 9,
        };
        var units = new Dictionary<char, int> { ['十'] = 10, ['拾'] = 10, ['百'] = 100, ['佰'] = 100, ['千'] = 1000, ['仟'] = 1000 };
        decimal total = 0;
        decimal current = 0;
        foreach (var character in text)
        {
            if (digits.TryGetValue(character, out var digit))
                current = digit;
            else if (units.TryGetValue(character, out var unit))
            {
                total += (current == 0 ? 1 : current) * unit;
                current = 0;
            }
        }
        return total + current;
    }

    private static string DateText(string value)
    {
        var match = Regex.Match(value, @"(\d{4})年(\d{1,2})月(\d{1,2})日");
        if (!match.Success) return value.Trim();
        var date = new DateTime(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
        return date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
    }

    private static string Title(string pinyin)
    {
        var parts = pinyin.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Select(part =>
            part.Length == 0 ? part : char.ToUpper(part[0]) + part[1..].ToLowerInvariant()));
    }
}
