using FluentAssertions;
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
    public void Explorer_capture_plan_uses_four_real_material_categories()
    {
        var outputs = new[]
        {
            @"C:\shots\01_真实项目文件目录.png",
            @"C:\shots\02_角色与场景素材.png",
            @"C:\shots\03_剧本与分镜文件.png",
            @"C:\shots\04_镜头生成源文件.png",
        };

        var workflow = Path.Combine(Path.GetTempPath(), $"capture-plan-{Guid.NewGuid():N}");
        var package = Path.Combine(workflow, TikTokSourceFileInfoScreenshotService.EvidenceDirectoryName, "EP01_源文件包");
        foreach (var name in new[] { "01_剧本与分镜", "02_角色素材", "04_镜头首帧", "06_视频源片段" })
        {
            var directory = Path.Combine(package, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "material.txt"), "real material");
        }

        var requests = TikTokSourceFileInfoScreenshotService.BuildExplorerCaptureRequests(workflow, outputs);

        requests.Select(request => request.OutputPath).Should().Equal(outputs);
        requests.Select(request => Path.GetFileName(request.Directory))
            .Should().Equal("01", "02", "03", "04");
        requests.Select(request => Directory.EnumerateFiles(request.Directory).Count())
            .Should().OnlyContain(count => count > 0);
        requests.Select(request => request.LargeIcons)
            .Should().Equal(false, true, true, false);
        Directory.Delete(workflow, recursive: true);
    }

    [Fact]
    public void Explorer_capture_plan_falls_back_to_real_package_files_when_video_is_absent()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"capture-plan-no-video-{Guid.NewGuid():N}");
        var package = Path.Combine(workflow, TikTokSourceFileInfoScreenshotService.EvidenceDirectoryName, "EP01_源文件包");
        foreach (var name in new[] { "01_剧本与分镜", "02_角色素材", "04_镜头首帧", "06_视频源片段" })
            Directory.CreateDirectory(Path.Combine(package, name));
        File.WriteAllText(Path.Combine(package, "EP01_素材清单.csv"), "name,type");
        File.WriteAllText(Path.Combine(package, "01_剧本与分镜", "script.docx"), "script");
        File.WriteAllText(Path.Combine(package, "02_角色素材", "character.jpg"), "image");
        File.WriteAllText(Path.Combine(package, "04_镜头首帧", "shot.png"), "image");
        File.WriteAllText(Path.Combine(package, "06_视频源片段", "视频源文件索引.txt"), "未发现视频");

        var outputs = Enumerable.Range(1, 4).Select(index => Path.Combine(workflow, $"{index}.png")).ToArray();
        var requests = TikTokSourceFileInfoScreenshotService.BuildExplorerCaptureRequests(workflow, outputs);

        requests[3].Directory.Should().Contain(".source-info-capture-staging");
        Directory.EnumerateFiles(requests[3].Directory)
            .Select(Path.GetFileName)
            .Should().Contain(name => name!.Contains("素材清单", StringComparison.Ordinal));
        Directory.Delete(workflow, recursive: true);
    }

    [Fact]
    public void Explorer_capture_plan_uses_other_real_project_files_when_categories_are_missing()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"capture-plan-partial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        try
        {
            File.WriteAllText(Path.Combine(workflow, "短剧第1集.mp4"), "test video 1");
            File.WriteAllText(Path.Combine(workflow, "短剧第2集.mp4"), "test video 2");
            var outputs = Enumerable.Range(1, 4)
                .Select(index => Path.Combine(workflow, $"{index}.png"))
                .ToArray();
            var logs = new List<string>();

            var requests = TikTokSourceFileInfoScreenshotService.BuildExplorerCaptureRequests(
                workflow, outputs, logs.Add);

            requests.Should().HaveCount(4);
            requests.Select(request => Directory.EnumerateFiles(request.Directory).Count())
                .Should().OnlyContain(count => count > 0);
            logs.Should().Contain(message =>
                message.Contains("缺少专属素材", StringComparison.Ordinal) &&
                message.Contains("其他真实项目文件补位", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Explorer_capture_plan_rejects_project_without_real_source_evidence()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"capture-plan-empty-{Guid.NewGuid():N}");
        var excluded = Path.Combine(
            workflow,
            TikTokAiGenerationScreenshotService.OutputDirectoryName);
        Directory.CreateDirectory(excluded);
        try
        {
            File.WriteAllText(Path.Combine(excluded, "AI过程图.png"), "not source evidence");
            var outputs = Enumerable.Range(1, 4)
                .Select(index => Path.Combine(workflow, $"{index}.png"))
                .ToArray();

            var action = () => TikTokSourceFileInfoScreenshotService.BuildExplorerCaptureRequests(
                workflow, outputs);

            action.Should().Throw<InvalidOperationException>()
                .WithMessage("*没有可用于“原始文件或素材文件信息”的真实图片、文档或视频文件*");
            Directory.Exists(Path.Combine(workflow, ".source-info-capture-staging"))
                .Should().BeFalse("失败时不应保留截图暂存目录");
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Resolve_source_project_directory_reads_workflow_metadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tiktok-source-resolution-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var workflow = Path.Combine(root, "workflow", "project");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workflow);
        try
        {
            File.WriteAllText(
                Path.Combine(workflow, "shortdrama-project.json"),
                $$"""{ "sourceProjectDir": {{System.Text.Json.JsonSerializer.Serialize(source)}} }""");

            TikTokSourceFileInfoScreenshotService.ResolveSourceProjectDirectory(workflow)
                .Should().Be(Path.GetFullPath(source));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
            outputs.Should().OnlyContain(path => File.Exists(path));
            logs.Should().Contain(message =>
                message.Contains("第 2 类已选取", StringComparison.Ordinal));
            logs.Should().Contain(message =>
                message.Contains("第 3 类已选取", StringComparison.Ordinal));
            Directory.Exists(TikTokSourceFileInfoScreenshotService.GetEvidenceDirectory(workflow))
                .Should().BeFalse("截图流程只能读取现有真实素材，不应合成额外证据文件");
            Directory.EnumerateFiles(workflow, "*.png", SearchOption.AllDirectories)
                .Where(path => !path.Contains(
                    TikTokSourceFileInfoScreenshotService.OutputDirectoryName,
                    StringComparison.OrdinalIgnoreCase))
                .Should().HaveCount(7, "原有的主体、场景和首帧素材不应被修改或扩增");
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
            Directory.Exists(evidenceDir)
                .Should().BeFalse("只有海报时应直接截图真实文件，不应生成剧本或清单冒充源文件");
            Path.GetFileName(outputDir).Should().Be(TikTokSourceFileInfoScreenshotService.OutputDirectoryName);
            File.Exists(Path.Combine(workflow, "海报图片.png")).Should().BeTrue();
            logs.Should().Contain(message => message.Contains("其他真实项目文件补位", StringComparison.Ordinal));
            logs.Should().Contain(message => message.Contains("原始文件信息截图已生成", StringComparison.Ordinal));

            foreach (var path in outputs)
            {
                Path.GetDirectoryName(path).Should().Be(outputDir);
                using var image = Image.Load(path);
                image.Width.Should().BeGreaterThanOrEqualTo(800);
                image.Height.Should().BeGreaterThanOrEqualTo(500);
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
