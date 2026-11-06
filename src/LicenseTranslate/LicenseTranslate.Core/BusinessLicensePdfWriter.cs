using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace LicenseTranslate.Core;

public static class BusinessLicensePdfWriter
{
    private static bool _windowsFontsEnabled;

    public static void Write(string path, BusinessLicenseDraft draft)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var sheet = BusinessLicenseSheet.From(draft);
        EnsureWindowsFonts();
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(841.9);
        page.Height = XUnit.FromPoint(595.3);
        using var graphics = XGraphics.FromPdfPage(page);
        using var emblem = Image("emblem.png");
        using var qr = Image("qr.png");
        using var signature = Image("signature.png");
        using var sealLeft = Image("seal_left.png");
        using var sealRight = Image("seal_right.png");

        DrawBorder(graphics);
        graphics.DrawImage(emblem, new XRect(392.3, 18.6, 59.5, 77.5));
        graphics.DrawImage(qr, new XRect(586.2, 103.2, 28.1, 29.9));

        var bold = new XFont("Times New Roman", 8.1, XFontStyleEx.Bold);
        var body = new XFont("Times New Roman", 8.1, XFontStyleEx.Regular);
        var title = new XFont("Times New Roman", 14.1, XFontStyleEx.Bold);
        var subtitle = new XFont("Times New Roman", 10.4, XFontStyleEx.Regular);
        var footer = new XFont("Times New Roman", 7.5, XFontStyleEx.Regular);

        Draw(graphics, "Unified Social Credit Code", bold, 54, 103.5);
        Draw(graphics, sheet.Credit, body, 54, 117.5);
        DrawCentered(graphics, "Business License", title, 114.5);
        DrawCentered(graphics, sheet.Duplicate, subtitle, 129.5);
        DrawWrapped(graphics, "Scan the QR code to log in the \"National Enterprise Credit Information Publicity System\" for more information on registration, filing, licensing and supervision.", body, new XRect(628, 90, 162, 55));

        Draw(graphics, "Name", bold, 54, 163.5);
        Draw(graphics, sheet.Name, body, 150.5, 163.5);
        Draw(graphics, sheet.ExtraLabel, bold, 484.2, 163.5);
        Draw(graphics, sheet.ExtraValue, body, 576, 163.5);
        Draw(graphics, "Type", bold, 54, 184.5);
        Draw(graphics, sheet.Type, body, 150.5, 184.5);
        Draw(graphics, sheet.DateLabel, bold, 484.2, 184.5);
        Draw(graphics, sheet.Established, body, 576, 184.5);
        Draw(graphics, sheet.PersonLabel, bold, 54, 217.5);
        Draw(graphics, sheet.Person, body, 150.5, 217.5);
        Draw(graphics, sheet.AddressLabel, bold, 484.2, 205.5);
        DrawFitted(graphics, sheet.Address, 576, 193, 212, 40, 8.1);

        var scopeTop = 228d;
        var scopeFont = Fit(graphics, sheet.Scope, 331.5, 174, [7.6, 7.2, 6.8, 6.4, 6.0]);
        var scopeLines = Wrap(graphics, sheet.Scope, scopeFont, 331.5);
        DrawLines(graphics, scopeLines, scopeFont, 150.5, scopeTop);
        var scopeHeight = Math.Max(scopeLines.Count, 1) * scopeFont.Size * 1.15;
        Draw(graphics, "Business scope", bold, 54, scopeTop + scopeHeight / 2);

        Draw(graphics, "Registration authority:", bold, 484.2, 408.5);
        DrawFitted(graphics, AuthorityName(sheet.Authority), 484.2, 412, 304, 28, 8.1);
        Draw(graphics, "(Sealed)", body, 484.2, 436.5);
        var issuedWidth = graphics.MeasureString(sheet.Issued, body).Width;
        Draw(graphics, sheet.Issued, body, 788 - issuedWidth, 448.5);

        Draw(graphics, "Website of the National Enterprise Credit Information Publicity", footer, 54, 478);
        Draw(graphics, "System: http://www.gsxt.gov.cn", footer, 54, 487.5);
        Draw(graphics, "The market entity shall submit its annual report through the National Enterprise Credit Information Publicity", footer, 269.4, 478);
        Draw(graphics, "System from January 1 to June 30 each year.", footer, 269.4, 487.5);
        Draw(graphics, "Formulated under the Supervision of the State", footer, 607.4, 478);
        Draw(graphics, "Administration for Market Regulation", footer, 607.4, 487.5);
        Draw(graphics, "I, Niu Mengyuan, translator of E-TRANSTAR (Beijing) International Translation Co., Ltd., confirm this is a true and accurate translation of the original document.", footer, 54, 502.5);
        Draw(graphics, "CATTI Certificate No.: 20201100937000000097", footer, 54, 512.5);
        Draw(graphics, "Organization: E-TRANSTAR (Beijing) International Translation Co., Ltd.", footer, 222, 512.5);
        Draw(graphics, "Tel: 010-60537024", footer, 54, 522.5);
        Draw(graphics, "Email: victor@57trans.com", footer, 117, 522.5);
        Draw(graphics, "Organization Address: Room 221-0281, 2nd Floor, Building 1, No. 3 Baiyang Road, Fangshan District, Beijing", footer, 54, 532.5);
        Draw(graphics, "Signature:", footer, 54, 542.5);
        Draw(graphics, "Date of Translation: " + sheet.TranslatedOn, footer, 186, 542.5);
        graphics.DrawImage(signature, new XRect(90.4, 531.6, 45.2, 15.6));
        graphics.DrawImage(sealLeft, new XRect(286.9, 449.6, 114.5, 118));
        graphics.DrawImage(sealRight, new XRect(408.5, 447.5, 114.3, 115));

        document.Save(path);
    }

    private static void EnsureWindowsFonts()
    {
        if (_windowsFontsEnabled)
            return;
        GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        _windowsFontsEnabled = true;
    }

    private static XImage Image(string fileName) => XImage.FromStream(new MemoryStream(BusinessLicenseAssets.Read(fileName)));

    private static string AuthorityName(string authority)
    {
        const string suffix = " (Sealed)";
        return authority.EndsWith(suffix, StringComparison.Ordinal) ? authority[..^suffix.Length] : authority;
    }

    private static void DrawBorder(XGraphics graphics)
    {
        DrawLine(graphics, 46.4, 15.6, 795.5, 15.6, 2.88);
        DrawLine(graphics, 46.4, 453.5, 795.5, 453.5, 2.88);
        DrawLine(graphics, 50.0, 18.1, 791.9, 18.1, 0.72);
        DrawLine(graphics, 50.0, 451.0, 791.9, 451.0, 0.72);
        DrawLine(graphics, 47.9, 14.1, 47.9, 455.0, 2.88);
        DrawLine(graphics, 794.0, 17.0, 794.0, 455.0, 2.88);
        DrawLine(graphics, 50.4, 17.8, 50.4, 450.6, 0.72);
        DrawLine(graphics, 791.5, 18.5, 791.5, 451.4, 0.72);
    }

    private static void DrawLine(XGraphics graphics, double x1, double y1, double x2, double y2, double width) =>
        graphics.DrawLine(new XPen(XColors.Black, width), x1, y1, x2, y2);

    private static void Draw(XGraphics graphics, string text, XFont font, double x, double baseline)
    {
        if (text.Length == 0)
            return;
        graphics.DrawString(text, font, XBrushes.Black, new XPoint(x, baseline));
    }

    private static void DrawCentered(XGraphics graphics, string text, XFont font, double baseline)
    {
        var width = graphics.MeasureString(text, font).Width;
        Draw(graphics, text, font, (841.9 - width) / 2, baseline);
    }

    private static void DrawFitted(XGraphics graphics, string text, double x, double y, double width, double height, double size)
    {
        var font = Fit(graphics, text, width, height, [size, size - 0.6, size - 1.2]);
        DrawLines(graphics, Wrap(graphics, text, font, width), font, x, y);
    }

    private static XFont Fit(XGraphics graphics, string text, double width, double height, double[] sizes)
    {
        XFont? chosen = null;
        foreach (var size in sizes)
        {
            chosen = new XFont("Times New Roman", size, XFontStyleEx.Regular);
            var lines = Wrap(graphics, text, chosen, width);
            if (lines.Count * chosen.Size * 1.15 <= height + 0.5)
                return chosen;
        }
        return chosen!;
    }

    private static void DrawWrapped(XGraphics graphics, string text, XFont font, XRect rect) =>
        DrawLines(graphics, Wrap(graphics, text, font, rect.Width), font, rect.X, rect.Y);

    private static void DrawLines(XGraphics graphics, IReadOnlyList<string> lines, XFont font, double x, double top)
    {
        var baseline = top + font.Size;
        foreach (var line in lines)
        {
            Draw(graphics, line, font, x, baseline);
            baseline += font.Size * 1.15;
        }
    }

    private static List<string> Wrap(XGraphics graphics, string text, XFont font, double width)
    {
        var lines = new List<string>();
        if (text.Length == 0 || width <= 0)
            return lines;
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var trial = current.Length == 0 ? word : current + " " + word;
            if (graphics.MeasureString(trial, font).Width <= width || current.Length == 0)
                current = trial;
            else
            {
                lines.Add(current);
                current = word;
            }
        }
        if (current.Length > 0)
            lines.Add(current);
        return lines;
    }
}
