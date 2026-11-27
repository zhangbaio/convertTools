namespace LicenseTranslate.Core;

public static class BusinessLicenseExporter
{
    public static string ExportPdf(BusinessLicenseDraft draft)
    {
        var pdf = BusinessLicenseDocumentWriter.OutputPath(draft, ".pdf");
        var temporaryPdf = TemporaryPath(pdf);
        try
        {
            BusinessLicensePdfWriter.Write(temporaryPdf, draft);
            File.Move(temporaryPdf, pdf);
            return pdf;
        }
        finally
        {
            File.Delete(temporaryPdf);
        }
    }

    public static (string Word, string Pdf) ExportWordAndPdf(BusinessLicenseDraft draft)
    {
        var word = BusinessLicenseDocumentWriter.OutputPath(draft, ".docx", ".pdf");
        var pdf = Path.ChangeExtension(word, ".pdf");
        var temporaryWord = TemporaryPath(word);
        var temporaryPdf = TemporaryPath(pdf);
        try
        {
            BusinessLicenseDocumentWriter.Write(temporaryWord, draft);
            BusinessLicensePdfWriter.Write(temporaryPdf, draft);
            File.Move(temporaryWord, word);
            try
            {
                File.Move(temporaryPdf, pdf);
            }
            catch
            {
                File.Delete(word);
                throw;
            }
            return (word, pdf);
        }
        finally
        {
            File.Delete(temporaryWord);
            File.Delete(temporaryPdf);
        }
    }

    private static string TemporaryPath(string outputPath) =>
        Path.Combine(Path.GetDirectoryName(outputPath) ?? "", $".license-{Guid.NewGuid():N}.tmp");
}
