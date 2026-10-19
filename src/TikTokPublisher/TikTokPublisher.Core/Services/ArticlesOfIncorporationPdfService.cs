using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// Fills the cleared Mississippi articles-of-incorporation template.
/// Positions are the original text baselines, in the same top-left point space PDFsharp uses.
/// </summary>
public static class ArticlesOfIncorporationPdfService
{
    private const string FontFamily = "Times New Roman";
    private const double FilingNumberRight = 563.93;
    private const double FilingNumberBaseline = 51.38;
    private const double FilingNumberSize = 22.52;
    // Windows Times New Roman draws larger than the Times embedded in the original filing.
    // These scales bring the filled glyphs back to that original cap height and width.
    private const double RegularSizeScale = 9.875 / 11.0;
    private const double BoldSizeScale = 14.0 / 15.375;
    private const double BoldWidthScale = (97.625 / 112.60) / BoldSizeScale;
    private const double StampCenterX = 500.465;
    private const double BusinessIdBaseline = 71.16;
    private const double FiledBaseline = 83.79;
    private const double StampSize = 12.09;
    private const double BodySize = 15.93;
    private const double AgentX = 114.01;
    private const double AgentNameBaseline = 348.57;
    private const double AgentStreetBaseline = 367.80;
    private const double AgentCityBaseline = 384.28;
    private const double SignedOnX = 432.62;
    private const double SignedOnBaseline = 619.40;
    private const double SignedOnSize = 16.48;
    private const double IncorporatorNameX = 50.28;
    private const double IncorporatorAddressX = 343.63;
    private const double IncorporatorFirstBaseline = 659.50;
    private const double IncorporatorCityBaseline = 675.98;

    public static void Render(string outputPath, ArticlesOfIncorporationFields fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(fields);
        TikTokTimestampCertificateService.EnsureSharedFontResolver();

        var templatePath = ResolveTemplatePath("template.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var document = PdfReader.Open(templatePath, PdfDocumentOpenMode.Modify);
        var page = document.Pages[0];
        using (var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append))
        {
            var bold = new XFont(FontFamily, FilingNumberSize * BoldSizeScale, XFontStyleEx.Bold);
            var stamp = new XFont(FontFamily, StampSize * RegularSizeScale, XFontStyleEx.Regular);
            var body = new XFont(FontFamily, BodySize * RegularSizeScale, XFontStyleEx.Regular);
            var date = new XFont(FontFamily, SignedOnSize * BoldSizeScale, XFontStyleEx.BoldItalic);

            DrawScaled(graphics, fields.FilingNumber, bold, FilingNumberRight, FilingNumberBaseline, BoldWidthScale, Align.Right);
            Draw(graphics, Prefix(fields.BusinessId, "Business ID: "), stamp, StampCenterX, BusinessIdBaseline, XStringFormats.BaseLineCenter);
            Draw(graphics, Prefix(fields.FiledAt, "Filed: "), stamp, StampCenterX, FiledBaseline, XStringFormats.BaseLineCenter);
            Draw(graphics, fields.RegisteredAgentName, body, AgentX, AgentNameBaseline);
            Draw(graphics, fields.RegisteredAgentStreet, body, AgentX, AgentStreetBaseline);
            Draw(graphics, fields.RegisteredAgentCity, body, AgentX, AgentCityBaseline);
            DrawSignedOn(graphics, fields.SignedOn, date, body);
            Draw(graphics, ArticlesOfIncorporationFields.ToShortName(fields.RegisteredAgentName), body, IncorporatorNameX, IncorporatorFirstBaseline);
            Draw(graphics, fields.RegisteredAgentStreet, body, IncorporatorAddressX, IncorporatorFirstBaseline);
            Draw(graphics, fields.RegisteredAgentCity, body, IncorporatorAddressX, IncorporatorCityBaseline);
        }

        document.Save(outputPath);
    }

    public static string ChineseOutputPath(string englishPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(englishPath))!;
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(englishPath) + "-中文.pdf");
    }

    public static void RenderChinese(string outputPath, ArticlesOfIncorporationFields fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(fields);
        TikTokTimestampCertificateService.EnsureSharedFontResolver();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var document = PdfReader.Open(ResolveTemplatePath("template-zh.pdf"), PdfDocumentOpenMode.Modify);
        var page = document.Pages[0];
        using (var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append))
        {
            var filing = new XFont("SimSun", FilingNumberSize, XFontStyleEx.Regular);
            var stamp = new XFont("SimSun", StampSize, XFontStyleEx.Regular);
            var body = new XFont("SimSun", BodySize, XFontStyleEx.Regular);

            Draw(graphics, fields.FilingNumber, filing, FilingNumberRight, FilingNumberBaseline, XStringFormats.BaseLineRight);
            DrawFittedCenter(graphics, Prefix(fields.BusinessId, "商业ID："), stamp, StampCenterX, 66, 145);
            DrawFittedCenter(graphics, TranslateFiledLine(fields.FiledAt), new XFont("SimSun", 8.5, XFontStyleEx.Regular), StampCenterX, 78, 145);
            Draw(graphics, ArticlesChineseText.TranslateName(fields.RegisteredAgentName), body, AgentX, AgentNameBaseline);
            Draw(graphics, ArticlesChineseText.TranslateStreet(fields.RegisteredAgentStreet), body, AgentX, AgentStreetBaseline);
            Draw(graphics, ArticlesChineseText.TranslateCity(fields.RegisteredAgentCity), body, AgentX, AgentCityBaseline);
            DrawChineseSignedOn(graphics, fields.SignedOn, body);
            Draw(graphics, ArticlesChineseText.TranslateShortName(fields.RegisteredAgentName), body, IncorporatorNameX, IncorporatorFirstBaseline);
            Draw(graphics, ArticlesChineseText.TranslateStreet(fields.RegisteredAgentStreet), body, IncorporatorAddressX, IncorporatorFirstBaseline);
            Draw(graphics, ArticlesChineseText.TranslateCity(fields.RegisteredAgentCity), body, IncorporatorAddressX, IncorporatorCityBaseline);
        }

        document.Save(outputPath);
    }

    private static string TranslateFiledLine(string filedAt)
    {
        var translated = ArticlesChineseText.TranslateFiledAt(filedAt);
        return string.IsNullOrWhiteSpace(translated) ? string.Empty : translated;
    }

    private static void DrawChineseSignedOn(XGraphics graphics, string signedOn, XFont font)
    {
        var date = ArticlesChineseText.TranslateSignedOn(signedOn);
        if (string.IsNullOrWhiteSpace(date) || date == signedOn.Trim().TrimEnd('.'))
            return;

        graphics.DrawRectangle(XBrushes.White, new XRect(36, 568, 534, 56));
        var paragraph = "通过在提供的空格中填写我的姓名，我证明我有权代表该实体提交本文件，已审阅该文件，并据我所知和所信，截至"
            + date
            + "，该文件真实、准确且完整。";
        var lines = Wrap(graphics, paragraph, font, 510);
        for (var i = 0; i < lines.Count && i < 3; i++)
            graphics.DrawString(lines[i], font, XBrushes.Black, new XPoint(40.4, 585.9 + i * 16.5));
    }

    private static void DrawFittedCenter(XGraphics graphics, string text, XFont font, double center, double baseline, double maxWidth)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var width = graphics.MeasureString(text, font).Width;
        if (width > maxWidth && width > 0)
            font = new XFont("SimSun", Math.Max(7, font.Size * maxWidth / width), XFontStyleEx.Regular);
        Draw(graphics, text, font, center, baseline, XStringFormats.BaseLineCenter);
    }

    private static List<string> Wrap(XGraphics graphics, string text, XFont font, double maxWidth)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var ch in text)
        {
            var candidate = current + ch;
            if (current.Length > 0 && graphics.MeasureString(candidate, font).Width > maxWidth)
            {
                lines.Add(current);
                current = ch.ToString();
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0)
            lines.Add(current);
        return lines;
    }

    private static void DrawSignedOn(XGraphics graphics, string value, XFont dateFont, XFont periodFont)
    {
        var date = value.Trim().TrimEnd('.');
        if (date.Length == 0)
            return;

        var width = graphics.MeasureString(date, dateFont).Width * BoldWidthScale;
        DrawScaled(graphics, date, dateFont, SignedOnX, SignedOnBaseline, BoldWidthScale, Align.Left);
        graphics.DrawString(".", periodFont, XBrushes.Black, new XPoint(SignedOnX + width, SignedOnBaseline));
    }

    private enum Align { Left, Right }

    private static void DrawScaled(
        XGraphics graphics,
        string value,
        XFont font,
        double anchor,
        double baseline,
        double widthScale,
        Align align)
    {
        var text = value.Trim();
        if (text.Length == 0)
            return;

        var width = graphics.MeasureString(text, font).Width;
        var left = align == Align.Right ? anchor - width * widthScale : anchor;
        var state = graphics.Save();
        graphics.TranslateTransform(left, 0);
        graphics.ScaleTransform(widthScale, 1);
        graphics.DrawString(text, font, XBrushes.Black, new XPoint(0, baseline));
        graphics.Restore(state);
    }

    private static void Draw(XGraphics graphics, string value, XFont font, double x, double baseline, XStringFormat? format = null)
    {
        var text = value.Trim();
        if (text.Length == 0)
            return;
        graphics.DrawString(text, font, XBrushes.Black, new XPoint(x, baseline), format ?? XStringFormats.Default);
    }

    private static string Prefix(string value, string prefix)
    {
        var text = value.Trim();
        return text.Length == 0 ? string.Empty : prefix + text;
    }

    private static string ResolveTemplatePath(string fileName)
    {
        var direct = Path.Combine(AppContext.BaseDirectory, "articles-of-incorporation", fileName);
        if (File.Exists(direct))
            return direct;

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "src",
                "TikTokPublisher",
                "TikTokPublisher.Core",
                "Resources",
                "ArticlesOfIncorporation",
                fileName);
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException("未找到公司注册文件模板。", direct);
    }
}

public readonly record struct ArticlesFilingStamp(
    string FilingNumber,
    string BusinessId,
    string FiledAt,
    string SignedOn,
    string RegistrantName,
    string RegistrantShortName,
    string Street,
    string City);

public sealed record ArticlesOfIncorporationFields(
    string FilingNumber,
    string BusinessId,
    string FiledAt,
    string RegisteredAgentName,
    string RegisteredAgentStreet,
    string RegisteredAgentCity,
    string SignedOn,
    string IncorporatorName,
    string IncorporatorStreet,
    string IncorporatorCity)
{
    public static ArticlesOfIncorporationFields Sample { get; } = new(
        "2026357992",
        "1543026",
        "04/19/2026 01:37 AM",
        "ANTHONY CLYDE CLUNIS",
        "1562 Oloh Rd",
        "Sumrall, MS 39482",
        "04/19/2026",
        "ANTHONY CLUNIS",
        "1562 Oloh Rd",
        "Sumrall, MS 39482");

    public static ArticlesFilingStamp CreateRandomFilingStamp(DateOnly today, Random? random = null)
    {
        var earliest = new DateOnly(2025, 1, 1);
        if (today < earliest)
            throw new ArgumentOutOfRangeException(nameof(today), "当前日期早于 2025 年，无法生成备案日期。");

        var rng = random ?? Random.Shared;
        var day = DateOnly.FromDayNumber(earliest.DayNumber + rng.Next(today.DayNumber - earliest.DayNumber + 1));
        var hour = rng.Next(1, 13);
        var minute = rng.Next(0, 60);
        var meridiem = rng.Next(2) == 0 ? "AM" : "PM";
        var fullName = $"{Pick(rng, GivenNames)} {Pick(rng, MiddleNames)} {Pick(rng, FamilyNames)}";
        var place = Places[rng.Next(Places.Length)];
        return new ArticlesFilingStamp(
            $"{day.Year}{rng.Next(0, 1_000_000):000000}",
            $"154{rng.Next(0, 10_000):0000}",
            $"{day:MM/dd/yyyy} {hour:00}:{minute:00} {meridiem}",
            $"{day:MM/dd/yyyy}",
            fullName,
            ToShortName(fullName),
            $"{rng.Next(100, 10000)} {Pick(rng, StreetNames)} {Pick(rng, StreetSuffixes)}",
            $"{place.City}, {place.State} {place.Zip}");
    }

    private static string Pick(Random random, string[] values) => values[random.Next(values.Length)];

    private static readonly string[] GivenNames = ["ANTHONY", "JAMES", "MARIA", "ROBERT", "LINDA", "DAVID", "SARAH", "MICHAEL"];
    private static readonly string[] MiddleNames = ["CLYDE", "LEE", "ANN", "JAMES", "MARIE", "RAY", "JO", "PAUL"];
    private static readonly string[] FamilyNames = ["CLUNIS", "HARRIS", "WALKER", "BROOKS", "REED", "COLEMAN", "HAYES", "FOSTER"];
    private static readonly string[] StreetNames = ["Oloh", "Oak", "Maple", "Pine", "Cedar", "Highland", "Lakeview", "River"];
    private static readonly string[] StreetSuffixes = ["Rd", "St", "Ave", "Dr", "Ln", "Blvd", "Ct", "Way"];
    private static readonly (string City, string State, string Zip)[] Places =
    [
        ("Sumrall", "MS", "39482"),
        ("Jackson", "MS", "39201"),
        ("Hattiesburg", "MS", "39401"),
        ("Gulfport", "MS", "39501"),
        ("Austin", "TX", "78701"),
        ("Dallas", "TX", "75201"),
        ("Atlanta", "GA", "30303"),
        ("Memphis", "TN", "38103"),
        ("Birmingham", "AL", "35203"),
        ("New Orleans", "LA", "70112"),
        ("Baton Rouge", "LA", "70801"),
        ("Nashville", "TN", "37201"),
    ];

    public static string ToShortName(string? fullName)
    {
        var parts = (fullName ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => string.Empty,
            <= 2 => string.Join(' ', parts),
            _ => $"{parts[0]} {parts[^1]}",
        };
    }

    public bool HasAnyValue() =>
        new[]
        {
            FilingNumber, BusinessId, FiledAt, RegisteredAgentName, RegisteredAgentStreet, RegisteredAgentCity,
            SignedOn, IncorporatorName, IncorporatorStreet, IncorporatorCity,
        }.Any(value => !string.IsNullOrWhiteSpace(value));
}
