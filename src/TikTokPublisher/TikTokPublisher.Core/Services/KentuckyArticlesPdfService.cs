using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;

namespace TikTokPublisher.Core.Services;

public static class KentuckyArticlesPdfService
{
    public static void Render(string outputPath, KentuckyArticlesFields fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(fields);
        TikTokTimestampCertificateService.EnsureSharedFontResolver();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var document = PdfReader.Open(ArticlesOfIncorporationPdfService.ResolveTemplatePath("template-ky.pdf"), PdfDocumentOpenMode.Modify);
        var page = document.Pages[0];
        using (var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append))
        {
            var smallBold = new XFont("Arial", 8.24, XFontStyleEx.Bold);
            var stamp = new XFont("Arial", 9.74, XFontStyleEx.Bold);
            var body = new XFont("Arial", 11.24, XFontStyleEx.Regular);
            var address = JoinAddress(fields.Street, fields.CityLine);

            Draw(graphics, fields.FilingNumber, stamp, 456.8, 45.3);
            Draw(graphics, "Michael G. Adams", smallBold, 456.8, 56.1);
            Draw(graphics, "Secretary of State", smallBold, 456.8, 66.6);
            Draw(graphics, "Received and Filed", new XFont("Arial", 8.24, XFontStyleEx.Regular), 456.8, 77.1);
            DrawFitted(graphics, fields.FiledAt, "Arial", 9.74, XFontStyleEx.Bold, 456.8, 88.8, 100, center: false);
            Draw(graphics, "Fee receipt: $40", stamp, 456.8, 100.8);
            DrawFitted(graphics, fields.CompanyName, "Arial", 11.24, XFontStyleEx.Bold, 306.05, 264.8, 340, center: true);
            DrawFitted(graphics, address, "Arial", 11.24, XFontStyleEx.Bold, 306.15, 375.8, 360, center: true);
            DrawFitted(graphics, address, "Arial", 11.24, XFontStyleEx.Bold, 306.15, 420.1, 360, center: true);
            DrawFitted(graphics, address, "Arial", 11.24, XFontStyleEx.Regular, 309.0, 464.7, 250, center: false);
            Draw(graphics, fields.EffectiveOn, body, 194.3, 499.9);
            DrawFitted(graphics, fields.EffectiveOn, "Arial", 11.24, XFontStyleEx.Regular, 444, 624.4, 115, center: false);
        }

        document.Save(outputPath);
    }

    public static void RenderChinese(string outputPath, KentuckyArticlesFields fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(fields);
        TikTokTimestampCertificateService.EnsureSharedFontResolver();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var document = PdfReader.Open(ArticlesOfIncorporationPdfService.ResolveTemplatePath("template-ky-zh.pdf"), PdfDocumentOpenMode.Modify);
        var page = document.Pages[0];
        using (var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append))
        {
            var small = new XFont("SimSun", 8.24, XFontStyleEx.Regular);
            var stamp = new XFont("SimSun", 9.74, XFontStyleEx.Regular);
            var address = ArticlesChineseText.TranslateKentuckyAddress(fields.Street, fields.CityLine);

            Draw(graphics, fields.FilingNumber, stamp, 456.8, 45.3);
            Draw(graphics, "迈克尔·G·亚当斯", small, 456.8, 56.1);
            Draw(graphics, "州务卿", small, 456.8, 66.6);
            Draw(graphics, "已接收并归档", small, 456.8, 77.1);
            DrawFitted(graphics, ArticlesChineseText.TranslateKentuckyFiledAt(fields.FiledAt), "SimSun", 8.24, XFontStyleEx.Regular, 456.8, 88.8, 100, center: false);
            Draw(graphics, "费用收据：$40", small, 456.8, 100.8);
            DrawFitted(graphics, ArticlesChineseText.TranslateKentuckyCompany(fields.CompanyName), "SimSun", 11.24, XFontStyleEx.Regular, 306.05, 264.8, 340, center: true);
            DrawFitted(graphics, address, "SimSun", 11.24, XFontStyleEx.Regular, 306.15, 375.8, 360, center: true);
            DrawFitted(graphics, address, "SimSun", 11.24, XFontStyleEx.Regular, 306.15, 420.1, 360, center: true);
            DrawFitted(graphics, address, "SimSun", 11.24, XFontStyleEx.Regular, 309.0, 464.7, 250, center: false);
            DrawFitted(graphics, ArticlesChineseText.TranslateKentuckyEffectiveOn(fields.EffectiveOn), "SimSun", 11.24, XFontStyleEx.Regular, 116, 499.9, 190, center: false);
            graphics.DrawRectangle(XBrushes.White, new XRect(234, 598, 330, 16));
            DrawFitted(
                graphics,
                "本人HUGO MAGDIEL DOMINGUEZ，同意于" + ArticlesChineseText.TranslateKentuckyEffectiveOn(fields.EffectiveOn),
                "SimSun",
                11.24,
                XFontStyleEx.Regular,
                234,
                610.9,
                330,
                center: false);
        }

        document.Save(outputPath);
    }

    public static KentuckyArticlesFields CreateRandom(DateOnly today, Random? random = null)
    {
        var earliest = new DateOnly(2025, 1, 1);
        if (today < earliest)
            throw new ArgumentOutOfRangeException(nameof(today), "当前日期早于 2025 年，无法生成备案日期。");

        var rng = random ?? Random.Shared;
        var day = DateOnly.FromDayNumber(earliest.DayNumber + rng.Next(today.DayNumber - earliest.DayNumber + 1));
        var hour12 = rng.Next(1, 13);
        var minute = rng.Next(0, 60);
        var second = rng.Next(0, 60);
        var isPm = rng.Next(2) == 1;
        var filed = new DateTime(day.Year, day.Month, day.Day, hour12 % 12 + (isPm ? 12 : 0), minute, second);
        var place = Places[rng.Next(Places.Length)];
        return new KentuckyArticlesFields(
            $"157{rng.Next(0, 10_000):0000}.{rng.Next(0, 100):00}",
            filed.ToString("M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture),
            $"{Pick(rng, CompanyAdjectives)} {Pick(rng, CompanyNouns)} INC",
            $"{rng.Next(100, 10000)} {Pick(rng, StreetNames)} {Pick(rng, StreetSuffixes)}",
            $"{place.City}, KY {place.Zip}",
            filed.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture) + ".");
    }

    private static string JoinAddress(string street, string cityLine)
    {
        var road = street.Trim();
        var city = cityLine.Trim();
        if (road.Length == 0)
            return city;
        if (city.Length == 0)
            return road;
        return $"{road}, {city}";
    }

    private static void Draw(XGraphics graphics, string value, XFont font, double x, double baseline)
    {
        var text = value.Trim();
        if (text.Length == 0)
            return;
        graphics.DrawString(text, font, XBrushes.Black, new XPoint(x, baseline));
    }

    private static void DrawFitted(
        XGraphics graphics,
        string value,
        string family,
        double size,
        XFontStyleEx style,
        double anchor,
        double baseline,
        double maxWidth,
        bool center)
    {
        var text = value.Trim();
        if (text.Length == 0)
            return;
        var font = new XFont(family, size, style);
        var width = graphics.MeasureString(text, font).Width;
        if (width > maxWidth && width > 0)
        {
            font = new XFont(family, Math.Max(6, size * maxWidth / width), style);
            width = graphics.MeasureString(text, font).Width;
        }

        var x = center ? anchor - width / 2 : anchor;
        graphics.DrawString(text, font, XBrushes.Black, new XPoint(x, baseline));
    }

    private static string Pick(Random random, string[] values) => values[random.Next(values.Length)];

    private static readonly string[] CompanyAdjectives = ["Tangible", "Harbor", "Silver", "North", "Maple", "Clear", "Summit", "River", "Golden", "Pacific"];
    private static readonly string[] CompanyNouns = ["Group", "Peak", "Oak", "Line", "Ridge", "Goods", "Retail", "Stone", "Field", "Trade"];
    private static readonly string[] StreetNames = ["Reins", "Oak", "Maple", "Pine", "Cedar", "Highland", "Main", "Church"];
    private static readonly string[] StreetSuffixes = ["Rd", "St", "Ave", "Dr", "Ln", "Ct", "Way"];
    private static readonly (string City, string Zip)[] Places =
    [
        ("Elizabethtown", "42701"),
        ("Louisville", "40202"),
        ("Lexington", "40507"),
        ("Frankfort", "40601"),
        ("Bowling Green", "42101"),
        ("Richmond", "40475"),
    ];
}

public sealed record KentuckyArticlesFields(
    string FilingNumber,
    string FiledAt,
    string CompanyName,
    string Street,
    string CityLine,
    string EffectiveOn)
{
    public bool HasAnyValue() =>
        new[] { FilingNumber, FiledAt, CompanyName, Street, CityLine, EffectiveOn }
            .Any(value => !string.IsNullOrWhiteSpace(value));
}
