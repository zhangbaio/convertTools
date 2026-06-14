namespace ShortDrama.Core.Interfaces;

public interface IDocumentRenderService
{
    Task<string> ConvertDocxToPdfAsync(string docxPath, string outputDir, CancellationToken cancellationToken);

    Task<string> ConvertPdfFirstPageToPngAsync(string pdfPath, string outputPngPath, CancellationToken cancellationToken);
}
