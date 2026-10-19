using FluentAssertions;
using PdfSharp.Pdf.IO;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class ArticlesOfIncorporationPdfServiceTests
{
    [Theory]
    [InlineData("ANTHONY CLYDE CLUNIS", "ANTHONY CLUNIS")]
    [InlineData("ANTHONY CLUNIS", "ANTHONY CLUNIS")]
    [InlineData("ANTHONY", "ANTHONY")]
    [InlineData("  ANTHONY   CLYDE   CLUNIS ", "ANTHONY CLUNIS")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ToShortName_KeepsFirstAndLastOfTheSameName(string? fullName, string expected)
    {
        ArticlesOfIncorporationFields.ToShortName(fullName).Should().Be(expected);
    }

    [Fact]
    public void Render_FillsClearedTemplateWithSampleFields()
    {
        var keepSample = string.Equals(Environment.GetEnvironmentVariable("KEEP_ARTICLES_SAMPLE"), "1", StringComparison.Ordinal);
        var output = keepSample
            ? Path.Combine(FindRepositoryRoot(), "tmp", "articles-of-incorporation-sample.pdf")
            : Path.Combine(Path.GetTempPath(), $"articles-{Guid.NewGuid():N}.pdf");
        try
        {
            ArticlesOfIncorporationPdfService.Render(output, ArticlesOfIncorporationFields.Sample);

            new FileInfo(output).Length.Should().BeGreaterThan(100);
            using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            pdf.PageCount.Should().Be(1);
        }
        finally
        {
            if (!keepSample && File.Exists(output))
                File.Delete(output);
        }
    }

    [Fact]
    public void Render_AllowsEveryFieldToStayBlank()
    {
        var output = Path.Combine(Path.GetTempPath(), $"articles-blank-{Guid.NewGuid():N}.pdf");
        try
        {
            ArticlesOfIncorporationPdfService.Render(output, new ArticlesOfIncorporationFields(
                "", "", "", "", "", "", "", "", "", ""));

            using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            pdf.PageCount.Should().Be(1);
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "global.json")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
