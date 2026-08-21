using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokAiGenerationScreenshotServiceTests
{
    [Theory]
    [InlineData(12, 60, 0.2)]
    [InlineData(12, 120, 0.1)]
    [InlineData(6, 30, 0.2)]
    public void Supplemental_single_pass_frame_rate_targets_requested_count(
        int frameCount,
        double duration,
        double expected)
    {
        TikTokAiGenerationScreenshotService.ResolveSupplementalFrameRate(frameCount, duration)
            .Should().BeApproximately(expected, 0.000001);
    }

    [Fact]
    public void Workbench_uses_portrait_layout_when_most_frames_are_vertical()
    {
        var frames = new List<Image<Rgba32>>
        {
            new(540, 960),
            new(540, 960),
            new(540, 960),
            new(1280, 720),
        };
        try
        {
            TikTokAiGenerationScreenshotService.UsesPortraitLayout(frames).Should().BeTrue();
        }
        finally
        {
            foreach (var frame in frames) frame.Dispose();
        }
    }

    [Fact]
    public void Workbench_keeps_landscape_layout_when_vertical_frames_are_not_the_majority()
    {
        var frames = new List<Image<Rgba32>>
        {
            new(1280, 720),
            new(1280, 720),
            new(540, 960),
            new(540, 960),
        };
        try
        {
            TikTokAiGenerationScreenshotService.UsesPortraitLayout(frames).Should().BeFalse();
        }
        finally
        {
            foreach (var frame in frames) frame.Dispose();
        }
    }

    [Fact]
    public void Cover_crop_top_aligns_portrait_frames()
    {
        var crop = TikTokAiGenerationScreenshotService.CalculateCoverCrop(
            resizedWidth: 560,
            resizedHeight: 996,
            targetWidth: 560,
            targetHeight: 300);

        crop.Top.Should().Be(0);
        crop.Top.Should().BeLessThan((996 - 300) / 2);
    }

    [Fact]
    public void Cover_crop_does_not_move_portrait_down_for_a_detected_face()
    {
        var crop = TikTokAiGenerationScreenshotService.CalculateCoverCrop(
            resizedWidth: 560,
            resizedHeight: 996,
            targetWidth: 560,
            targetHeight: 300,
            normalizedFaceCenterY: 0.15);

        crop.Top.Should().Be(0);
    }

    [Fact]
    public void Cover_crop_rejects_a_low_false_face_focus_for_portrait_frames()
    {
        var crop = TikTokAiGenerationScreenshotService.CalculateCoverCrop(
            resizedWidth: 560,
            resizedHeight: 996,
            targetWidth: 560,
            targetHeight: 300,
            normalizedFaceCenterY: 0.65);

        crop.Top.Should().Be(0);
    }

    [Fact]
    public void Cover_crop_keeps_landscape_frames_centered()
    {
        var crop = TikTokAiGenerationScreenshotService.CalculateCoverCrop(
            resizedWidth: 600,
            resizedHeight: 400,
            targetWidth: 560,
            targetHeight: 300);

        crop.Should().Be(new Rectangle(20, 50, 560, 300));
    }

    [Fact]
    public void Face_visibility_score_prefers_centered_skin_region()
    {
        using var emptyFrame = new Image<Rgba32>(160, 160, new Rgba32(35, 45, 60));
        using var faceFrame = new Image<Rgba32>(160, 160, new Rgba32(35, 45, 60));
        for (var y = 28; y < 88; y++)
        {
            for (var x = 50; x < 110; x++)
            {
                var dx = (x - 80) / 30d;
                var dy = (y - 58) / 30d;
                if ((dx * dx) + (dy * dy) <= 1)
                {
                    faceFrame[x, y] = new Rgba32(210, 155, 125);
                }
            }
        }

        TikTokAiGenerationScreenshotService.ScoreFaceVisibility(faceFrame)
            .Should().BeGreaterThan(TikTokAiGenerationScreenshotService.ScoreFaceVisibility(emptyFrame));
    }

    [Fact]
    public void Face_visibility_score_rejects_a_low_neck_like_skin_region()
    {
        using var upperFace = new Image<Rgba32>(160, 160, new Rgba32(35, 45, 60));
        using var lowNeck = new Image<Rgba32>(160, 160, new Rgba32(35, 45, 60));
        for (var y = 24; y < 64; y++)
        {
            for (var x = 60; x < 100; x++)
            {
                upperFace[x, y] = new Rgba32(210, 155, 125);
            }
        }
        for (var y = 104; y < 150; y++)
        {
            for (var x = 52; x < 108; x++)
            {
                lowNeck[x, y] = new Rgba32(210, 155, 125);
            }
        }

        TikTokAiGenerationScreenshotService.ScoreFaceVisibility(upperFace)
            .Should().BeGreaterThan(TikTokAiGenerationScreenshotService.ScoreFaceVisibility(lowNeck));
    }

    [Fact]
    public void Likely_face_count_distinguishes_single_and_multi_person_frames()
    {
        using var single = new Image<Rgba32>(160, 160, new Rgba32(35, 45, 60));
        using var multiple = new Image<Rgba32>(160, 160, new Rgba32(35, 45, 60));
        FillSkinRegion(single, 60, 22, 100, 62);
        FillSkinRegion(multiple, 25, 24, 55, 58);
        FillSkinRegion(multiple, 105, 24, 135, 58);

        TikTokAiGenerationScreenshotService.CountLikelyFaces(single).Should().Be(1);
        TikTokAiGenerationScreenshotService.CountLikelyFaces(multiple).Should().Be(2);

        static void FillSkinRegion(Image<Rgba32> image, int left, int top, int right, int bottom)
        {
            for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
                image[x, y] = new Rgba32(210, 155, 125);
        }
    }

    [Fact]
    public void Asset_fallback_excludes_project_images()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-ai-assets-{Guid.NewGuid():N}");
        var projectImages = Path.Combine(workflow, TikTokProjectImageService.OutputDirectoryName);
        Directory.CreateDirectory(projectImages);
        try
        {
            using (var poster = new Image<Rgba32>(32, 32, Color.Red))
            {
                poster.SaveAsPng(Path.Combine(workflow, "海报图片.png"));
            }
            using (var cover = new Image<Rgba32>(32, 32, Color.Blue))
            {
                cover.SaveAsPng(Path.Combine(workflow, "tiktok-cover-3x4.png"));
            }
            using (var projectImage = new Image<Rgba32>(32, 32, Color.Green))
            {
                projectImage.SaveAsPng(Path.Combine(projectImages, "工程图_1.png"));
            }

            var paths = TikTokAiGenerationScreenshotService.CollectAssetImagePaths(workflow);

            paths.Should().Contain(path => Path.GetFileName(path) == "海报图片.png");
            paths.Should().Contain(path => Path.GetFileName(path) == "tiktok-cover-3x4.png");
            paths.Should().NotContain(path =>
                path.Contains(TikTokProjectImageService.OutputDirectoryName, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).StartsWith("工程图_", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Frame_pool_repeats_all_original_frames_in_round_robin_order()
    {
        var frames = new List<Image<Rgba32>>
        {
            new(1, 1, Color.Red),
            new(1, 1, Color.Green),
            new(1, 1, Color.Blue),
        };
        try
        {
            TikTokAiGenerationScreenshotService.FillFramePool(frames, 9);

            frames.Select(frame => frame[0, 0])
                .Should()
                .Equal(
                    Color.Red.ToPixel<Rgba32>(),
                    Color.Green.ToPixel<Rgba32>(),
                    Color.Blue.ToPixel<Rgba32>(),
                    Color.Red.ToPixel<Rgba32>(),
                    Color.Green.ToPixel<Rgba32>(),
                    Color.Blue.ToPixel<Rgba32>(),
                    Color.Red.ToPixel<Rgba32>(),
                    Color.Green.ToPixel<Rgba32>(),
                    Color.Blue.ToPixel<Rgba32>());
        }
        finally
        {
            foreach (var frame in frames)
            {
                frame.Dispose();
            }
        }
    }

    [Fact]
    public void EnumerateVideos_includes_tiktok_upload_videos()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-ai-video-source-{Guid.NewGuid():N}");
        var uploadVideos = Path.Combine(workflow, "tiktok_upload_videos");
        Directory.CreateDirectory(uploadVideos);
        try
        {
            var first = Path.Combine(uploadVideos, "第1集.mp4");
            var second = Path.Combine(uploadVideos, "第2集.mov");
            File.WriteAllBytes(first, [1]);
            File.WriteAllBytes(second, [2]);
            File.WriteAllBytes(Path.Combine(uploadVideos, "忽略.txt"), [3]);

            TikTokAiGenerationScreenshotService.EnumerateVideos(workflow)
                .Take(2)
                .Should()
                .BeEquivalentTo([first, second]);
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Generate_uses_distinct_tiktok_upload_videos_for_workbench_pages()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-ai-real-video-{Guid.NewGuid():N}");
        var uploadVideos = Path.Combine(workflow, "tiktok_upload_videos");
        Directory.CreateDirectory(uploadVideos);
        try
        {
            var colors = new[] { "red", "green", "blue", "yellow", "magenta", "cyan", "orange", "purple" };
            for (var index = 0; index < colors.Length; index++)
            {
                CreateSolidColorVideo(
                    Path.Combine(uploadVideos, $"第{index + 1}集.mp4"),
                    colors[index]);
            }

            var outputs = TikTokAiGenerationScreenshotService.Generate(
                workflow,
                "真实视频抽帧测试",
                settings: new ClientSettings());

            var retainedFrames =
                TikTokAiGenerationScreenshotService.ListRetainedFrameImages(workflow);
            retainedFrames.Should().NotBeEmpty();
            retainedFrames.Should().OnlyContain(path => File.Exists(path));
            retainedFrames.Should().OnlyContain(path =>
                Path.GetDirectoryName(path) ==
                TikTokAiGenerationScreenshotService.GetRetainedFramesDirectory(workflow));
            var manifestPath =
                TikTokAiGenerationScreenshotService.GetRetainedFramesManifestPath(workflow);
            File.Exists(manifestPath).Should().BeTrue();
            using (var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)))
            {
                manifest.RootElement.GetProperty("version").GetString()
                    .Should().Be(TikTokAiGenerationScreenshotService.RetainedFramesVersion);
                manifest.RootElement.GetProperty("frame_count").GetInt32()
                    .Should().Be(retainedFrames.Count);
                manifest.RootElement.GetProperty("frames").GetArrayLength()
                    .Should().Be(retainedFrames.Count);
                var firstFrame = manifest.RootElement.GetProperty("frames")[0];
                firstFrame.GetProperty("source_video").GetString().Should().EndWith(".mp4");
                firstFrame.GetProperty("seconds").GetDouble().Should().BeGreaterThan(0);
                firstFrame.GetProperty("sha256").GetString().Should().HaveLength(64);
            }

            var heroColors = outputs.Select(path =>
            {
                using var image = Image.Load<Rgba32>(path);
                var pixel = image[300, 350];
                return (pixel.R / 16, pixel.G / 16, pixel.B / 16);
            }).ToArray();

            heroColors.Should().OnlyHaveUniqueItems(
                "四页应分别使用不同分集视频的主体帧，而不是循环同一张海报");
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

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
            var retainedFrames =
                TikTokAiGenerationScreenshotService.ListRetainedFrameImages(workflow);
            retainedFrames.Should().NotBeEmpty();
            retainedFrames.Should().OnlyContain(path => File.Exists(path));
            var manifestPath =
                TikTokAiGenerationScreenshotService.GetRetainedFramesManifestPath(workflow);
            File.Exists(manifestPath).Should().BeTrue();
            using (var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)))
            {
                manifest.RootElement.GetProperty("frame_count").GetInt32()
                    .Should().Be(retainedFrames.Count);
                manifest.RootElement.GetProperty("frames").GetArrayLength()
                    .Should().Be(retainedFrames.Count);
            }

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

            File.Delete(manifestPath);
            TikTokAiGenerationScreenshotService.HasCurrentOutput(workflow)
                .Should().BeFalse("新版本产物必须包含抽帧原图清单");
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public async Task Generate_serializes_concurrent_runs_for_the_same_workflow()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-ai-concurrent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        try
        {
            using (var poster = new Image<Rgba32>(240, 320, new Rgba32(80, 120, 180)))
            {
                poster.SaveAsPng(Path.Combine(workflow, "海报图片.png"));
            }

            var first = Task.Run(() => TikTokAiGenerationScreenshotService.Generate(
                workflow, "并发测试一", new ClientSettings()));
            var second = Task.Run(() => TikTokAiGenerationScreenshotService.Generate(
                workflow, "并发测试二", new ClientSettings()));

            var results = await Task.WhenAll(first, second);

            results.Should().OnlyContain(paths => paths.Count == 4);
            TikTokAiGenerationScreenshotService.ListGeneratedImages(workflow).Should().HaveCount(4);
            Directory.EnumerateDirectories(workflow, ".ai-generation-screenshots-*")
                .Should()
                .BeEmpty("成功或失败后都不应遗留 staging 目录");
            Directory.EnumerateDirectories(workflow, ".ai-generation-screenshots-backup-*")
                .Should()
                .BeEmpty("成功替换后不应遗留备份目录");
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    private static void CreateSolidColorVideo(string outputPath, string color)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegLocator.ResolveFfmpeg(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error",
                     "-f", "lavfi",
                     "-i", $"color=c={color}:s=320x240:d=2",
                     "-pix_fmt", "yuv420p",
                     "-y", outputPath,
                 })
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("无法启动 ffmpeg 测试进程。");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(15000).Should().BeTrue("ffmpeg 应在测试超时前完成");
        process.ExitCode.Should().Be(0, stderr);
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
