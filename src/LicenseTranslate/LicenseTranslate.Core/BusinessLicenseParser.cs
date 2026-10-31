using System.Text.RegularExpressions;

namespace LicenseTranslate.Core;

public static class BusinessLicenseParser
{
    public static BusinessLicenseDraft Parse(string sourcePath, string ocrText)
    {
        var lines = ocrText.Replace("\r", "")
            .Split('\n')
            .Select(NormalizeLine)
            .Where(line => line.Length > 0)
            .ToList();
        var flat = string.Join("\n", lines);
        var individual = flat.Contains("个体工商户", StringComparison.Ordinal) || flat.Contains("经营者", StringComparison.Ordinal);
        var dates = Dates(flat).ToList();
        var draft = new BusinessLicenseDraft
        {
            SourcePath = sourcePath,
            Kind = individual ? "个体工商户" : "公司",
            CreditCode = Regex.Match(flat.Replace(" ", ""), @"[0-9A-Z]{18}").Value,
            Name = FirstLine(lines, line => line.Contains("有限公司", StringComparison.Ordinal) || line.Contains("工作室", StringComparison.Ordinal)),
            Type = individual
                ? "个体工商户"
                : FirstLine(lines, line => line.Contains("有限责任公司", StringComparison.Ordinal)),
            PersonName = Person(lines, individual),
            ExtraLabel = individual ? "组成形式" : "注册资本",
            ExtraValue = individual
                ? FirstLine(lines, line => line.Contains("个人经营", StringComparison.Ordinal) || line.Contains("家庭经营", StringComparison.Ordinal))
                : Regex.Match(flat, @"\d+(?:\.\d+)?万元").Value,
            DateLabel = individual ? "注册日期" : "成立日期",
            DateValue = dates.FirstOrDefault() ?? "",
            AddressLabel = individual ? "经营场所" : "住所",
            Address = Address(lines),
            Scope = Scope(lines),
            Authority = FirstLine(lines, line => line.Contains("市场监督管理局", StringComparison.Ordinal)),
            IssueDate = dates.Skip(1).FirstOrDefault() ?? dates.FirstOrDefault() ?? "",
            PersonRole = individual ? "经营者" : "法定代表人",
            DuplicateNo = Regex.Match(flat, @"副本编号[:：]?([0-9]+)").Groups[1].Value,
        };
        if (!string.IsNullOrWhiteSpace(draft.Type))
            draft.Type = Regex.Replace(draft.Type, @"^[^有个]+", "");
        Cleanup(draft);
        return draft;
    }

    public static string NormalizeLine(string text)
    {
        text = text.Replace('\u3000', ' ').Replace("•", ".").Replace("．", ".");
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff])\s+(?=[\u4e00-\u9fff])", "");
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff])\s+(?=[0-9A-Za-z（(])", "");
        text = Regex.Replace(text, @"(?<=[0-9A-Za-z）)])\s+(?=[\u4e00-\u9fff])", "");
        text = Regex.Replace(text, @"(?<=[0-9A-Z])\s+(?=[0-9A-Z])", "");
        text = text.Replace("日其", "日期").Replace("筑划", "策划").Replace("电竟", "电竞").Replace("其体", "具体");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string Person(List<string> lines, bool individual)
    {
        var label = individual ? "经营者" : "法定代表人";
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var index = line.IndexOf(label, StringComparison.Ordinal);
            if (index < 0) continue;
            var tail = line[(index + label.Length)..].Trim(' ', ':', '：');
            var named = Regex.Match(tail, @"[\u4e00-\u9fff]{2,4}");
            if (named.Success) return named.Value;
            if (i + 1 < lines.Count)
            {
                var next = Regex.Match(lines[i + 1], @"^[\u4e00-\u9fff]{2,4}$");
                if (next.Success) return next.Value;
            }
        }
        return "";
    }

    private static string Scope(List<string> lines)
    {
        var start = lines.FindIndex(line => line.Contains("一般项目", StringComparison.Ordinal) || line.Contains("许可项目", StringComparison.Ordinal) || line.StartsWith("经营范围", StringComparison.Ordinal));
        if (start < 0) return "";
        var taken = new List<string>();
        for (var i = start; i < lines.Count; i++)
        {
            var line = lines[i];
            if (i > start && (line.Contains("国家企业信用", StringComparison.Ordinal) || line.Contains("市场主体应当", StringComparison.Ordinal) || line.Contains("登记机关", StringComparison.Ordinal) || line.StartsWith("屋", StringComparison.Ordinal) || Regex.IsMatch(line, @"^\d{4}年") || line.Contains("万元")))
                break;
            taken.Add(line.StartsWith("经营范围", StringComparison.Ordinal) ? line["经营范围".Length..] : line);
        }
        return string.Join("", taken).Trim();
    }

    private static string Address(List<string> lines)
    {
        var index = lines.FindIndex(line => line.Contains('区') && (line.Contains('号') || line.Contains('街') || line.Contains('路')));
        if (index < 0) return "";
        var address = lines[index];
        if (index + 1 < lines.Count && address.Contains('（') && !address.Contains('）'))
            address += lines[index + 1];
        var house = lines.FirstOrDefault(line => line.StartsWith("屋", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(house) && !address.Contains(house, StringComparison.Ordinal))
            address += house;
        return address;
    }

    private static string FirstLine(List<string> lines, Func<string, bool> match) =>
        lines.FirstOrDefault(match) ?? "";

    private static void Cleanup(BusinessLicenseDraft draft)
    {
        draft.Name = Trim(draft.Name);
        draft.Type = Trim(draft.Type);
        draft.Address = Trim(draft.Address);
        draft.Scope = Trim(draft.Scope);
        draft.Authority = Trim(draft.Authority);
        if (draft.Name.Contains('（'))
        {
            var head = draft.Name.Split('（')[0].Trim();
            if (head.Length >= 4) draft.Name = head;
        }
        draft.Name = Regex.Replace(draft.Name, @"^(名称|称)", "");
    }

    private static string Trim(string value) => value.Trim(' ', ':', '：', '；', ';', '。');

    private static IEnumerable<string> Dates(string text) =>
        Regex.Matches(text, @"\d{4}年\d{1,2}月\d{1,2}日").Select(match => match.Value);
}
