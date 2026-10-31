using System.Globalization;
using System.Text.RegularExpressions;
using NPinyin;

namespace PlatformPublisher.Desktop.Licensing;

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
        var match = Regex.Match(value, @"(\d+(?:\.\d+)?)万");
        if (!match.Success) return value.Trim();
        var amount = decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 10000m;
        return "CNY " + amount.ToString("N0", CultureInfo.InvariantCulture);
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
