using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokAiGenerationScreenshotServiceTests
{
    [Fact]
    public void Generate_creates_four_workbench_pngs()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-ai-shot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        try
        {
            using (var poster = new Image<Rgba32>(240, 320))
            {
                poster[20, 20] = new Rgba32(200, 80, 40);
                poster.SaveAsPng(Path.Combine(workflow, "海报图片.png"));
            }

            var outputs = TikTokAiGenerationScreenshotService.Generate(
                workflow,
                "测试短剧标题",
                settings: new ClientSettings());

            outputs.Should().HaveCount(4);
            outputs.Should().OnlyContain(path => File.Exists(path));
            TikTokAiGenerationScreenshotService.HasCurrentOutput(workflow).Should().BeTrue();

            foreach (var path in outputs)
            {
                Path.GetFileName(path).Should().StartWith("工程图_");
                using var image = Image.Load(path);
                image.Width.Should().Be(1600);
                image.Height.Should().BeGreaterThan(1000);
            }
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Publish_options_builder_binds_ai_generation_directory()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-ai-bind-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        try
        {
            File.WriteAllBytes(
                TikTokProofMaterialService.GetPdfPath(workflow),
                "%PDF-1.7\nproof"u8.ToArray());
            TikTokSourceFileInfoScreenshotService.Generate(workflow, "绑定测试剧", "公司A");
            TikTokAiGenerationScreenshotService.Generate(workflow, "绑定测试剧", new ClientSettings());

            var account = new TikTokAccountProfile
            {
                TiktokCopyrightMaterialTypes =
                [
                    TikTokPublishConstants.ProductionAgreementMaterialType,
                    TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
                ],
            };

            var options = TikTokPublishOptionsBuilder.FromAccount(account, workflow);
            options.CopyrightMaterialFilePaths.Keys.Should().Contain(
                TikTokPublishConstants.AiGenerationScreenshotsMaterialType);

            var images = options.ResolveCopyrightMaterialFilePaths(
                TikTokPublishConstants.AiGenerationScreenshotsMaterialType);
            images.Should().HaveCountGreaterThanOrEqualTo(4);
            images.Should().OnlyContain(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }
}
