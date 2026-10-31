using System.Text;

namespace LicenseTranslate.Core;

public static class BusinessLicensePdfWriter
{
    public static void Write(string path, BusinessLicenseDraft draft)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = new List<(string Text, int Size)>
        {
            ("Business License", 18),
            ("(Duplicate)", 12),
        };
        if (!string.IsNullOrWhiteSpace(draft.EnglishDuplicateNo))
            lines.Add(("Duplicate No.: " + draft.EnglishDuplicateNo, 11));
        lines.Add(("Unified Social Credit Code: " + draft.CreditCode, 12));
        lines.Add(("Scan the QR code to learn more about registration, filing, licensing and supervision information.", 10));
        lines.Add(("", 10));
        lines.Add((draft.Kind == "个体工商户" ? "Name: " + draft.EnglishName : "Name: " + draft.EnglishName, 11));
        lines.Add(("Type: " + draft.EnglishType, 11));
        if (!string.IsNullOrWhiteSpace(draft.EnglishExtraValue))
            lines.Add((draft.EnglishExtraLabel + ": " + draft.EnglishExtraValue, 11));
        lines.Add((draft.EnglishPersonRole + ": " + draft.EnglishPersonName, 11));
        lines.Add((draft.EnglishDateLabel + ": " + draft.EnglishDateValue, 11));
        lines.Add((draft.EnglishAddressLabel + ": " + draft.EnglishAddress, 11));
        lines.Add(("", 10));
        lines.Add(("Business Scope", 12));
        foreach (var line in Wrap(draft.EnglishScope, 95))
            lines.Add((line, 10));
        lines.Add(("", 10));
        lines.Add(("Registration Authority: " + draft.EnglishAuthority, 11));
        lines.Add((draft.EnglishIssueDate, 11));
        lines.Add(("", 10));
        lines.Add(("http://www.gsxt.gov.cn", 10));
        foreach (var line in Wrap("Market entities shall submit and publicize their annual reports through the National Enterprise Credit Information Publicity System from January 1 to June 30 each year.", 95))
            lines.Add((line, 10));
        lines.Add(("Formulated under the Supervision of the State Administration for Market Regulation", 10));

        var pages = new List<List<(string Text, int Size)>>();
        for (var index = 0; index < lines.Count; index += 46)
            pages.Add(lines.Skip(index).Take(46).ToList());
        if (pages.Count == 0)
            pages.Add([("Business License", 18)]);

        var objects = new List<string>();
        var pageIds = new List<int>();
        var fontId = 3;
        var nextId = 4;
        foreach (var page in pages)
        {
            var contentId = nextId++;
            var pageId = nextId++;
            pageIds.Add(pageId);
            var content = Content(page);
            objects.Add($"{contentId} 0 obj\n<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream\nendobj\n");
            objects.Add($"{pageId} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents {contentId} 0 R /Resources << /Font << /F1 {fontId} 0 R >> >> >>\nendobj\n");
        }

        var kids = string.Join(' ', pageIds.Select(id => $"{id} 0 R"));
        var catalog = "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n";
        var pageTree = $"2 0 obj\n<< /Type /Pages /Count {pages.Count} /Kids [{kids}] >>\nendobj\n";
        var font = $"{fontId} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n";
        var body = catalog + pageTree + font + string.Join("", objects);
        var header = "%PDF-1.4\n";
        var offsets = new List<int> { 0 };
        var cursor = Encoding.ASCII.GetByteCount(header);
        foreach (var chunk in new[] { catalog, pageTree, font }.Concat(objects))
        {
            offsets.Add(cursor);
            cursor += Encoding.ASCII.GetByteCount(chunk);
        }
        var xref = cursor;
        var xrefBuilder = new StringBuilder();
        xrefBuilder.Append("xref\n").Append("0 ").Append(offsets.Count).Append('\n');
        xrefBuilder.Append("0000000000 65535 f \n");
        for (var i = 1; i < offsets.Count; i++)
            xrefBuilder.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        xrefBuilder.Append("trailer\n<< /Size ").Append(offsets.Count).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        File.WriteAllText(path, header + body + xrefBuilder, Encoding.ASCII);
    }

    private static string Content(List<(string Text, int Size)> lines)
    {
        var builder = new StringBuilder();
        builder.Append("BT\n");
        var y = 800;
        foreach (var (text, size) in lines)
        {
            builder.Append("/F1 ").Append(size).Append(" Tf\n");
            builder.Append("1 0 0 1 48 ").Append(y).Append(" Tm\n(").Append(Escape(text)).Append(") Tj\n");
            y -= size + 6;
        }
        builder.Append("ET");
        return builder.ToString();
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var remaining = Ascii(text);
        if (remaining.Length == 0)
        {
            yield return "";
            yield break;
        }
        while (remaining.Length > width)
        {
            var cut = remaining.LastIndexOf(' ', width);
            if (cut < 20) cut = width;
            yield return remaining[..cut].Trim();
            remaining = remaining[cut..].Trim();
        }
        if (remaining.Length > 0)
            yield return remaining;
    }

    private static string Ascii(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(ch switch
            {
                >= ' ' and <= '~' => ch,
                '\r' or '\n' or '\t' => ' ',
                '—' or '–' => '-',
                '‘' or '’' or '′' => '\'',
                '“' or '”' => '"',
                _ => '?',
            });
        }
        return builder.ToString();
    }

    private static string Escape(string text) =>
        Ascii(text).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
