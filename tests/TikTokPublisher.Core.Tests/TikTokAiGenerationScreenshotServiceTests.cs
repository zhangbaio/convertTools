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

            var logs = new List<string>();
            var outputs = TikTokAiGenerationScreenshotService.Generate(
                workflow,
                "测试短剧标题",
                settings: new ClientSettings(),
                log: logs.Add);

            outputs.Should().HaveCount(4);
            outputs.Should().OnlyContain(path => File.Exists(path));
            TikTokAiGenerationScreenshotService.HasCurrentOutput(workflow).Should().BeTrue();

            var outputDir = TikTokAiGenerationScreenshotService.GetOutputDirectory(workflow);
            Directory.Exists(outputDir).Should().BeTrue();
            Path.GetFileName(outputDir).Should().Be(TikTokAiGenerationScreenshotService.OutputDirectoryName);
            logs.Should().Contain(message => message.Contains("AI 截图/初始化", StringComparison.Ordinal));
            logs.Should().Contain(message => message.Contains("AI 截图/素材池", StringComparison.Ordinal));
            logs.Should().Contain(message => message.Contains("AI 截图/分析", StringComparison.Ordinal));
            logs.Should().Contain(message => message.Contains("AI 生成过程截图已生成", StringComparison.Ordinal));

            foreach (var path in outputs)
            {
                Path.GetDirectoryName(path).Should().Be(outputDir);
                Path.GetFileName(path).Should().MatchRegex(@"^\d{2}_分镜工作台\.png$");
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
            options.CopyrightMaterialFilePaths[TikTokPublishConstants.AiGenerationScreenshotsMaterialType]
                .Should().Be(TikTokAiGenerationScreenshotService.GetOutputDirectory(workflow));

            var images = options.ResolveCopyrightMaterialFilePaths(
                TikTokPublishConstants.AiGenerationScreenshotsMaterialType);
            images.Should().HaveCountGreaterThanOrEqualTo(4);
            images.Should().OnlyContain(path =>
                path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                && path.Contains(
                    Path.DirectorySeparatorChar + TikTokAiGenerationScreenshotService.OutputDirectoryName
                    + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
            images.Should().NotContain(path =>
                Path.GetFileName(path).StartsWith("工程图_", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }
}
