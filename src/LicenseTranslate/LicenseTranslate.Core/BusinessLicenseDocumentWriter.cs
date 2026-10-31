using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace LicenseTranslate.Core;

public static class BusinessLicenseDocumentWriter
{
    public static void Write(string path, BusinessLicenseDraft draft)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;
        body.Append(Paragraph("Business License", true, "32", JustificationValues.Center));
        body.Append(Paragraph("(Duplicate)", false, "24", JustificationValues.Center));
        if (!string.IsNullOrWhiteSpace(draft.EnglishDuplicateNo))
            body.Append(Paragraph("Duplicate No.: " + draft.EnglishDuplicateNo, false, "21", JustificationValues.Right));
        body.Append(Paragraph("Unified Social Credit Code: " + draft.CreditCode, true, "21", JustificationValues.Left));
        body.Append(Paragraph("Scan the QR code to learn more about registration, filing, licensing and supervision information.", false, "18", JustificationValues.Left));
        body.Append(Blank());
        body.Append(Field(draft.Kind == "个体工商户" ? "Name" : "Name", draft.EnglishName));
        body.Append(Field("Type", draft.EnglishType));
        if (!string.IsNullOrWhiteSpace(draft.EnglishExtraValue))
            body.Append(Field(draft.EnglishExtraLabel, draft.EnglishExtraValue));
        body.Append(Field(draft.EnglishPersonRole, draft.EnglishPersonName));
        body.Append(Field(draft.EnglishDateLabel, draft.EnglishDateValue));
        body.Append(Field(draft.EnglishAddressLabel, draft.EnglishAddress));
        body.Append(Blank());
        body.Append(Paragraph("Business Scope", true, "21", JustificationValues.Left));
        body.Append(Paragraph(draft.EnglishScope, false, "20", JustificationValues.Both));
        body.Append(Blank());
        body.Append(Paragraph("Registration Authority: " + draft.EnglishAuthority, false, "21", JustificationValues.Right));
        body.Append(Paragraph(draft.EnglishIssueDate, false, "21", JustificationValues.Right));
        body.Append(Blank());
        body.Append(Paragraph("http://www.gsxt.gov.cn", false, "18", JustificationValues.Left));
        body.Append(Paragraph("Market entities shall submit and publicize their annual reports through the National Enterprise Credit Information Publicity System from January 1 to June 30 each year.", false, "18", JustificationValues.Left));
        body.Append(Paragraph("Formulated under the Supervision of the State Administration for Market Regulation", false, "18", JustificationValues.Center));
        main.Document.Save();
    }

    public static string OutputPath(BusinessLicenseDraft draft, string extension)
    {
        var folder = Path.GetDirectoryName(draft.SourcePath) ?? "";
        var person = string.IsNullOrWhiteSpace(draft.PersonName) ? Path.GetFileNameWithoutExtension(draft.SourcePath) : draft.PersonName.Trim();
        var stem = "营业执照 " + person;
        var path = Path.Combine(folder, stem + extension);
        var index = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(folder, $"{stem}{index}{extension}");
            index++;
        }
        return path;
    }

    private static Paragraph Field(string label, string value) =>
        Paragraph(label + ": " + value, false, "21", JustificationValues.Left);

    private static Paragraph Blank() => new(new Run(new Text(" ")));

    private static Paragraph Paragraph(string text, bool bold, string size, JustificationValues align)
    {
        var run = new Run(new RunProperties(new FontSize { Val = size }, new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }));
        if (bold) run.RunProperties!.Bold = new Bold();
        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return new Paragraph(new ParagraphProperties(new Justification { Val = align }), run);
    }
}
