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
    public void CreateRandomFilingStamp_StaysInside2025ThroughToday()
    {
        var today = new DateOnly(2026, 10, 19);
        var random = new Random(17);
        for (var i = 0; i < 200; i++)
        {
            var stamp = ArticlesOfIncorporationFields.CreateRandomFilingStamp(today, random);
            var signedOn = DateOnly.ParseExact(stamp.SignedOn, "MM/dd/yyyy");
            var filedAt = DateTime.ParseExact(stamp.FiledAt, "MM/dd/yyyy hh:mm tt", System.Globalization.CultureInfo.InvariantCulture);

            signedOn.Should().BeOnOrAfter(new DateOnly(2025, 1, 1)).And.BeOnOrBefore(today);
            DateOnly.FromDateTime(filedAt).Should().Be(signedOn);
            stamp.FilingNumber.Should().HaveLength(10).And.StartWith(signedOn.Year.ToString());
            stamp.BusinessId.Should().HaveLength(7);
            int.Parse(stamp.BusinessId).Should().BeInRange(1_000_000, 9_999_999);
            stamp.RegistrantShortName.Should().Be(ArticlesOfIncorporationFields.ToShortName(stamp.RegistrantName));
            stamp.RegistrantName.Split(' ').Should().HaveCount(3);
            stamp.Street.Should().MatchRegex(@"^\d{3,4} [A-Z][a-z]+ (Rd|St|Ave|Dr|Ln|Blvd|Ct|Way)$");
            stamp.City.Should().MatchRegex(@"^.+, [A-Z]{2} \d{5}$");
        }
    }

    [Fact]
    public void CreateRandomFilingStamp_RejectsATodayBefore2025()
    {
        var act = () => ArticlesOfIncorporationFields.CreateRandomFilingStamp(new DateOnly(2024, 12, 31), new Random(1));
        act.Should().Throw<ArgumentOutOfRangeException>();
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
