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
    public void Explorer_capture_script_is_embedded()
    {
        typeof(TikTokSourceFileInfoScreenshotService).Assembly
            .GetManifestResourceNames()
            .Should().Contain("TikTokPublisher.Core.Resources.CaptureExplorerWindow.ps1");
    }

    [Fact]
    public void Generate_handles_multiple_videos_when_only_one_fallback_frame_is_requested()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-source-many-videos-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        try
        {
            File.WriteAllText(Path.Combine(workflow, "短剧第1集.mp4"), "test video 1");
            File.WriteAllText(Path.Combine(workflow, "短剧第2集.mp4"), "test video 2");

            var action = () => TikTokSourceFileInfoScreenshotService.Generate(
                workflow, "多集短剧", "测试公司");

            action.Should().NotThrow();
            TikTokSourceFileInfoScreenshotService.ListGeneratedImages(workflow).Should().HaveCount(4);
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Generate_prefers_existing_ai_drama_assets_without_creating_extra_source_images()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-source-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        try
        {
            var characterDir = Path.Combine(workflow, "第一集制作包", "可灵主体", "赵强");
            var sceneDir = Path.Combine(workflow, "第一集制作包", "assets", "场景参考");
            var keyframeDir = Path.Combine(workflow, "第一集制作包", "可灵视频", "片段01");
            Directory.CreateDirectory(characterDir);
            Directory.CreateDirectory(sceneDir);
            Directory.CreateDirectory(keyframeDir);

            for (var i = 0; i < 4; i++)
            {
                File.WriteAllText(
                    Path.Combine(characterDir, $"{i:D2}_主体主图_正面全身.png"),
                    "invalid image");
            }
            SaveSolidImage(Path.Combine(characterDir, "99_主体主图_正面全身.png"), new Rgba32(80, 110, 140));
            SaveSolidImage(Path.Combine(sceneDir, "福生便利店场景参考板.png"), new Rgba32(150, 100, 60));
            SaveSolidImage(Path.Combine(keyframeDir, "EP01_片段01_首帧.png"), new Rgba32(110, 80, 60));
            File.WriteAllText(
                Path.Combine(keyframeDir, "可灵生成提示词.md"),
                "赵强把辞职信拍在收银台上，福生抬头。保持人物和场景一致。",
                System.Text.Encoding.UTF8);

            var logs = new List<string>();
            var outputs = TikTokSourceFileInfoScreenshotService.Generate(
                workflow,
                "福生小店",
                "测试公司",
                logs.Add);

            outputs.Should().HaveCount(4);
            logs.Should().Contain(message => message.Contains("复用真实主体/定妆文件 1 张", StringComparison.Ordinal));
            logs.Should().Contain(message => message.Contains("复用真实场景参考文件 1 张", StringComparison.Ordinal));

            var evidenceDir = TikTokSourceFileInfoScreenshotService.GetEvidenceDirectory(workflow);
            Directory.EnumerateFiles(Path.Combine(evidenceDir, "03_角色参考"))
                .Should().BeEmpty("直接角色素材应只读复用，不应产生额外抽帧图片");
            Directory.EnumerateFiles(Path.Combine(evidenceDir, "04_场景参考"))
                .Should().BeEmpty("直接场景素材应只读复用，不应产生额外抽帧图片");
            var episodePackage = Path.Combine(evidenceDir, "EP01_源文件包");
            Directory.Exists(episodePackage).Should().BeTrue();
            File.Exists(Path.Combine(episodePackage, "EP01_素材清单.csv")).Should().BeTrue();
            File.Exists(Path.Combine(episodePackage, "EP01_镜头清单.json")).Should().BeTrue();
            File.Exists(Path.Combine(
                    episodePackage, "01_剧本与分镜", "EP01_30秒片段正式制作剧本.docx"))
                .Should().BeTrue();
            File.Exists(Path.Combine(episodePackage, "06_视频源片段", "视频源文件索引.txt"))
                .Should().BeTrue();
            Directory.EnumerateFiles(episodePackage, "*.mp4", SearchOption.AllDirectories)
                .Should().BeEmpty("视频只登记真实路径和元数据，不应复制或转码");
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

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

            var logs = new List<string>();
            var outputs = TikTokSourceFileInfoScreenshotService.Generate(
                workflow,
                "测试短剧标题",
                "测试公司",
                logs.Add);

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
            var scriptDocx = Directory.EnumerateFiles(
                    evidenceDir, "*_30秒片段正式制作剧本.docx", SearchOption.AllDirectories)
                .Should().ContainSingle().Subject;
            using (var document = WordprocessingDocument.Open(scriptDocx, false))
            {
                var documentText = document.MainDocumentPart!.Document.Body!.InnerText;
                documentText.Should().Contain("片段时间码");
                documentText.Should().NotContain("character_main.ai");
                if (File.Exists(@"D:\code\短剧制作\《福生小店》20集AI真人漫剧完整剧本.docx"))
                {
                    documentText.Should().Contain("集体辞职");
                    documentText.Should().Contain("赵强");
                    documentText.Should().Contain("福生");
                }
            }

            var manifest = File.ReadAllText(Path.Combine(evidenceDir, "视频文件清单.csv"));
            manifest.Should().Contain("SHA-256");
            manifest.Should().NotContain("character_main.ai");
            manifest.Should().NotContain("scene_palace.psd");
            manifest.Should().NotContain("raw/A001_C001.mov");
            logs.Should().Contain(message => message.Contains("原始文件信息/初始化", StringComparison.Ordinal));
            logs.Should().Contain(message => message.Contains("原始文件信息/扫描", StringComparison.Ordinal));
            logs.Should().Contain(message => message.Contains("原始文件信息/清单", StringComparison.Ordinal));
            logs.Should().Contain(message => message.Contains("原始文件信息/文档", StringComparison.Ordinal));
            logs.Should().Contain(message => message.Contains("原始文件信息截图已生成", StringComparison.Ordinal));

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

    private static void SaveSolidImage(string path, Rgba32 color)
    {
        using var image = new Image<Rgba32>(360, 640, color);
        image.SaveAsPng(path);
    }
}
