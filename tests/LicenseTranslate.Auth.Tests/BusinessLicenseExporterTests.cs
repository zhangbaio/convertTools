using LicenseTranslate.Core;
using Xunit;

namespace LicenseTranslate.Auth.Tests;

public class BusinessLicenseExporterTests : IDisposable
{
    private readonly DirectoryInfo _output = Directory.CreateTempSubdirectory("license-export-");

    [Fact]
    public void Pdf_only_exports_preserve_each_other_and_a_later_batch_uses_a_free_pair()
    {
        var draft = CreateDraft();
        var firstPdf = BusinessLicenseExporter.ExportPdf(draft);
        var originalPdf = File.ReadAllBytes(firstPdf);
        Assert.Equal("营业执照 经营者.pdf", Path.GetFileName(firstPdf));
        Assert.True(originalPdf.AsSpan().StartsWith("%PDF-"u8));
        Assert.Single(_output.GetFiles());

        draft.EnglishName = "Revised Test Business";
        var secondPdf = BusinessLicenseExporter.ExportPdf(draft);
        var revisedPdf = File.ReadAllBytes(secondPdf);
        Assert.Equal("营业执照 经营者2.pdf", Path.GetFileName(secondPdf));
        Assert.Equal(2, _output.GetFiles().Length);

        var (word, pdf) = BusinessLicenseExporter.ExportWordAndPdf(draft);

        Assert.Equal("营业执照 经营者3.docx", Path.GetFileName(word));
        Assert.Equal(Path.ChangeExtension(word, ".pdf"), pdf);
        Assert.Equal(originalPdf, File.ReadAllBytes(firstPdf));
        Assert.Equal(revisedPdf, File.ReadAllBytes(secondPdf));
        Assert.True(File.ReadAllBytes(pdf).AsSpan().StartsWith("%PDF-"u8));
        Assert.Equal(4, _output.GetFiles().Length);
    }

    [Fact]
    public void Batch_exports_skip_word_and_pdf_collisions_and_later_pdf_keeps_the_pair()
    {
        var draft = CreateDraft();
        var existingWord = Path.Combine(_output.FullName, "营业执照 经营者.docx");
        BusinessLicenseDocumentWriter.Write(existingWord, draft);
        var originalWord = File.ReadAllBytes(existingWord);
        var existingPdf = Path.Combine(_output.FullName, "营业执照 经营者2.pdf");
        BusinessLicensePdfWriter.Write(existingPdf, draft);
        var originalPdf = File.ReadAllBytes(existingPdf);

        var (word, pdf) = BusinessLicenseExporter.ExportWordAndPdf(draft);
        var batchPdf = File.ReadAllBytes(pdf);
        var laterPdf = BusinessLicenseExporter.ExportPdf(draft);

        Assert.Equal("营业执照 经营者3.docx", Path.GetFileName(word));
        Assert.Equal(Path.ChangeExtension(word, ".pdf"), pdf);
        Assert.Equal("营业执照 经营者.pdf", Path.GetFileName(laterPdf));
        Assert.Equal(originalWord, File.ReadAllBytes(existingWord));
        Assert.Equal(originalPdf, File.ReadAllBytes(existingPdf));
        Assert.Equal(batchPdf, File.ReadAllBytes(pdf));
        Assert.Equal(5, _output.GetFiles().Length);
    }

    private BusinessLicenseDraft CreateDraft() => new()
    {
        SourcePath = Path.Combine(_output.FullName, "license.png"),
        PersonName = "经营者",
        EnglishName = "Test Business",
    };

    public void Dispose() => _output.Delete(recursive: true);
}
