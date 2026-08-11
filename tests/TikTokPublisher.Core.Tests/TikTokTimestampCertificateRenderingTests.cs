using FluentAssertions;
using PdfSharp.Pdf.IO;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokTimestampCertificateRenderingTests
{
    [Fact]
    public void Render_UsesBundledTemplateAndCreatesChineseEnglishPages()
    {
        var root = FindRepositoryRoot();
        var assets = Path.Combine(root, "src", "TikTokPublisher", "TikTokPublisher.Core", "Resources", "TimestampCertificate");
        var keepSample = string.Equals(Environment.GetEnvironmentVariable("KEEP_TIMESTAMP_SAMPLE"), "1", StringComparison.Ordinal);
        var output = keepSample
            ? Path.Combine(root, "tmp", "pdfs", "timestamp-certificate-sample.pdf")
            : Path.Combine(Path.GetTempPath(), $"timestamp-certificate-{Guid.NewGuid():N}.pdf");
        try
        {
            TikTokTimestampCertificateService.Render(
                Path.Combine(assets, "tsa_certificate_template.pdf"),
                Path.Combine(assets, "tsa_certificate_layout.json"),
                output,
                new TikTokTimestampCertificateService.CertificateFields(
                    "武汉测试科技有限公司",
                    "2026-08-11 13:20:37（UTC+8）",
                    new string('A', 64),
                    "测试短剧名称",
                    "TSA-01-2026081112345678901"),
                CancellationToken.None);

            File.Exists(output).Should().BeTrue();
            using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            pdf.PageCount.Should().Be(2);
        }
        finally
        {
            if (!keepSample && File.Exists(output)) File.Delete(output);
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
