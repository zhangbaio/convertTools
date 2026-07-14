namespace TikTokPublisher.Core.Services;

public enum TikTokProofMaterialPdfRendererPreference
{
    Wps = 0,
    LibreOffice = 1,
}

public static class TikTokProofMaterialPdfRendererPreferenceExtensions
{
    public static TikTokProofMaterialPdfRendererPreference Parse(string? value) =>
        string.Equals(value?.Trim(), "libreoffice", StringComparison.OrdinalIgnoreCase)
            ? TikTokProofMaterialPdfRendererPreference.LibreOffice
            : TikTokProofMaterialPdfRendererPreference.Wps;
}

public sealed record TikTokProofMaterialRequest(
    string TemplateDocxPath,
    string OutputPdfPath,
    string CopyrightCompanyName,
    string DeclarantCompanyName,
    string DramaTitle,
    DateOnly StatementDate)
{
    public string? SealImagePath { get; init; }

    public TikTokProofMaterialPdfRendererPreference PreferredPdfRenderer { get; init; } =
        TikTokProofMaterialPdfRendererPreference.Wps;

    public string? WpsExecutablePath { get; init; }

    public string? LibreOfficeExecutablePath { get; init; }

    public bool KeepIntermediateDocx { get; init; }

    public string? TemporaryDirectory { get; init; }

    public TimeSpan RenderTimeout { get; init; } = TimeSpan.FromSeconds(180);
}

public sealed record TikTokProofMaterialReplacementCounts(
    int CopyrightCompany,
    int DeclarantCompany,
    int DramaTitle,
    int StatementDate,
    int SealImages);

public sealed record TikTokProofMaterialDocumentResult(
    string DocxPath,
    string WorkingDirectory,
    TikTokProofMaterialReplacementCounts Replacements);

public sealed record TikTokProofMaterialPdfRenderResult(
    string PdfPath,
    string RendererName);

public sealed record TikTokProofMaterialResult(
    string PdfPath,
    string? IntermediateDocxPath,
    string PdfRenderer,
    TikTokProofMaterialReplacementCounts Replacements);

public sealed record TikTokProofMaterialPdfRenderOptions
{
    public TikTokProofMaterialPdfRendererPreference PreferredRenderer { get; init; } =
        TikTokProofMaterialPdfRendererPreference.Wps;

    public string? WpsExecutablePath { get; init; }

    public string? LibreOfficeExecutablePath { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(180);
}
