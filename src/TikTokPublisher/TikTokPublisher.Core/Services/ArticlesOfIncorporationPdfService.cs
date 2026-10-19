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

        var templatePath = ResolveTemplatePath();
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

    private static string ResolveTemplatePath()
    {
        var direct = Path.Combine(AppContext.BaseDirectory, "articles-of-incorporation", "template.pdf");
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
                "template.pdf");
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException("未找到公司注册文件模板。", direct);
    }
}

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
