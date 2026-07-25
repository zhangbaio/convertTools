using FluentAssertions;
using DocumentFormat.OpenXml.Packaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokSourceFileInfoScreenshotServiceTests
{
    [Fact]
    public void Generate_creates_four_png_screenshots_with_drama_title()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-source-info-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        try
        {
            using (var poster = new Image<Rgba32>(240, 320))
            {
                poster[20, 20] = new Rgba32(200, 80, 40);
                poster.SaveAsPng(Path.Combine(workflow, "海报图片.png"));
            }

            var outputs = TikTokSourceFileInfoScreenshotService.Generate(
                workflow,
                "测试短剧标题",
                "测试公司");

            outputs.Should().HaveCount(4);
            outputs.Should().OnlyContain(path => File.Exists(path));
            TikTokSourceFileInfoScreenshotService.HasCurrentOutput(workflow).Should().BeTrue();

            var outputDir = TikTokSourceFileInfoScreenshotService.GetOutputDirectory(workflow);
            var evidenceDir = TikTokSourceFileInfoScreenshotService.GetEvidenceDirectory(workflow);
            Directory.Exists(outputDir).Should().BeTrue();
            Directory.Exists(evidenceDir).Should().BeTrue();
            Path.GetFileName(outputDir).Should().Be(TikTokSourceFileInfoScreenshotService.OutputDirectoryName);
            File.Exists(Path.Combine(evidenceDir, "视频文件清单.csv")).Should().BeTrue();
            File.Exists(Path.Combine(evidenceDir, "项目说明.txt")).Should().BeTrue();
            var scriptDocx = Directory.EnumerateFiles(evidenceDir, "*_成片整理稿.docx").Should().ContainSingle().Subject;
            using (var document = WordprocessingDocument.Open(scriptDocx, false))
            {
                var documentText = document.MainDocumentPart!.Document.Body!.InnerText;
                documentText.Should().Contain("成片整理稿");
                documentText.Should().Contain("不代表拍摄前原始剧本");
                documentText.Should().NotContain("character_main.ai");
                document.MainDocumentPart.Document.Body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>()
                    .Should().ContainSingle();
            }

            var manifest = File.ReadAllText(Path.Combine(evidenceDir, "视频文件清单.csv"));
            manifest.Should().Contain("SHA-256");
            manifest.Should().NotContain("character_main.ai");
            manifest.Should().NotContain("scene_palace.psd");
            manifest.Should().NotContain("raw/A001_C001.mov");

            foreach (var path in outputs)
            {
                Path.GetDirectoryName(path).Should().Be(outputDir);
                using var image = Image.Load(path);
                image.Width.Should().BeGreaterThan(1000);
                image.Height.Should().BeGreaterThan(700);
            }
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Publish_options_builder_binds_source_file_information_directory()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-source-bind-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        try
        {
            File.WriteAllBytes(
                TikTokProofMaterialService.GetPdfPath(workflow),
                "%PDF-1.7\nproof"u8.ToArray());
            TikTokSourceFileInfoScreenshotService.Generate(workflow, "绑定测试剧", "公司A");

            var account = new Models.TikTokAccountProfile
            {
                TiktokCopyrightMaterialTypes =
                [
                    TikTokPublishConstants.ProductionAgreementMaterialType,
                    TikTokPublishConstants.SourceFileInformationMaterialType,
                ],
            };

            var options = TikTokPublishOptionsBuilder.FromAccount(account, workflow);
            options.CopyrightMaterialFilePaths.Keys.Should().BeEquivalentTo(
                TikTokPublishConstants.ProductionAgreementMaterialType,
                TikTokPublishConstants.SourceFileInformationMaterialType);
            options.CopyrightMaterialFilePaths[TikTokPublishConstants.SourceFileInformationMaterialType]
                .Should().Be(TikTokSourceFileInfoScreenshotService.GetOutputDirectory(workflow));

            var images = options.ResolveCopyrightMaterialFilePaths(
                TikTokPublishConstants.SourceFileInformationMaterialType);
            images.Should().HaveCount(4);
            images.Should().OnlyContain(path =>
                path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                && path.Contains(
                    Path.DirectorySeparatorChar + TikTokSourceFileInfoScreenshotService.OutputDirectoryName
                    + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }
}
