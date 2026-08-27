using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokProofMaterialVideoHydrationTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void ResolveTemporaryVideoEpisodeCount_UsesNoVideos_WhenOutputsDoNotNeedGeneration()
    {
        var settings = new ClientSettings();

        TikTokProofMaterialService.ResolveTemporaryVideoEpisodeCount(
                generateAiScreenshots: false,
                generateEditingProjectFiles: false,
                settings)
            .Should().Be(0);
    }

    [Fact]
    public void ResolveTemporaryVideoEpisodeCount_UsesOneEpisode_ForAiScreenshotsOnly()
    {
        var settings = new ClientSettings();

        TikTokProofMaterialService.ResolveTemporaryVideoEpisodeCount(
                generateAiScreenshots: true,
                generateEditingProjectFiles: false,
                settings)
            .Should().Be(1);
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(0, ClientSettingsDefaults.TiktokProjectImageRenderEpisodeLimit)]
    [InlineData(500, 200)]
    public void ResolveTemporaryVideoEpisodeCount_UsesConfiguredEditingLimit(
        int configuredLimit,
        int expected)
    {
        var settings = new ClientSettings
        {
            TiktokProjectImageRenderEpisodeLimit = configuredLimit,
        };

        TikTokProofMaterialService.ResolveTemporaryVideoEpisodeCount(
                generateAiScreenshots: true,
                generateEditingProjectFiles: true,
                settings)
            .Should().Be(expected);
    }

    [Fact]
    public void ProofMaterialVideoHydrationResult_DoesNotOwnOrDeleteCreatedFiles()
    {
        using var temp = new TemporaryDirectory();
        var existing = Path.Combine(temp.Path, "existing.mp4");
        var hydrated = Path.Combine(temp.Path, "hydrated.mp4");
        File.WriteAllBytes(existing, [1]);
        File.WriteAllBytes(hydrated, [2]);
        var result = new QueueMaterialStepService.ProofMaterialVideoHydrationResult([hydrated]);

        result.CreatedVideoPaths.Should().Equal(hydrated);
        File.Exists(existing).Should().BeTrue();
        File.Exists(hydrated).Should().BeTrue(
            "证明材料补下载的视频应由项目归档流程统一清理");
    }

    [Fact]
    public async Task Missing_book_id_uses_uploaded_series_fallback_for_material_videos_only()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source");
        var workflow = Path.Combine(temp.Path, "workflow", "source");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workflow);
        File.WriteAllText(
            Path.Combine(source, "shortdrama-project.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                sourceProjectDir = source,
                workflowProjectDir = workflow,
            }));
        var item = new QueueProjectItem
        {
            ProjectDir = source,
            NewTitle = "已上传项目",
            UploadCompletedAt = DateTimeOffset.Now.ToString("o"),
        };
        var calls = 0;
        var result = await QueueMaterialStepService.EnsureProofMaterialVideosAsync(
            item,
            new ClientSettings(),
            requiredEpisodeCount: 1,
            _ => { },
            CancellationToken.None,
            (_, episodes, _, _) =>
            {
                calls++;
                episodes.Should().Equal(1);
                var cache = ProjectVideoResolver.ResolvePublishedMaterialVideoDirectory(source);
                Directory.CreateDirectory(cache);
                var path = Path.Combine(cache, "第001集.mp4");
                File.WriteAllBytes(path, [1, 2, 3]);
                return Task.FromResult<IReadOnlyDictionary<int, string>>(
                    new Dictionary<int, string> { [1] = path });
            });

        calls.Should().Be(1);
        result.CreatedVideoPaths.Should().ContainSingle();
        ProjectVideoResolver.ResolveMaterialVideos(source).Should().ContainSingle();
        ProjectVideoResolver.ResolveUploadVideos(source, allowStagedFallback: true).Should().BeEmpty();
    }

    [Fact]
    public void FindProofMaterialFallbackFrame_PrefersRetainedAiFrame()
    {
        using var temp = new TemporaryDirectory();
        var retainedDirectory = TikTokAiGenerationScreenshotService.GetRetainedFramesDirectory(temp.Path);
        Directory.CreateDirectory(retainedDirectory);
        var retained = Path.Combine(retainedDirectory, "保留帧.jpg");
        File.WriteAllBytes(retained, [1, 2, 3]);
        var otherDirectory = Path.Combine(temp.Path, "其他材料", "抽帧原图");
        Directory.CreateDirectory(otherDirectory);
        File.WriteAllBytes(Path.Combine(otherDirectory, "其他帧.png"), [1, 2, 3, 4, 5]);

        QueueMaterialStepService.FindProofMaterialFallbackFrame(temp.Path)
            .Should().Be(retained);
    }

    [Fact]
    public void FindProofMaterialFallbackFrame_UsesOtherExtractedFrameDirectory()
    {
        using var temp = new TemporaryDirectory();
        var frameDirectory = Path.Combine(temp.Path, "参考格式原始素材包", "抽帧原图");
        Directory.CreateDirectory(frameDirectory);
        var empty = Path.Combine(frameDirectory, "空图片.jpg");
        var usable = Path.Combine(frameDirectory, "可用图片.png");
        File.WriteAllBytes(empty, []);
        File.WriteAllBytes(usable, [1, 2, 3]);

        QueueMaterialStepService.FindProofMaterialFallbackFrame(temp.Path)
            .Should().Be(usable);
    }

    [Fact]
    public async Task CreateProofMaterialFrameFallbackVideoAsync_CreatesPlayableInputFile()
    {
        using var temp = new TemporaryDirectory();
        var frame = Path.Combine(temp.Path, "抽帧.png");
        var output = Path.Combine(temp.Path, "证明材料抽帧兜底.mp4");
        File.WriteAllBytes(frame, OnePixelPng);

        await QueueMaterialStepService.CreateProofMaterialFrameFallbackVideoAsync(
            frame,
            output,
            CancellationToken.None);

        File.Exists(output).Should().BeTrue();
        new FileInfo(output).Length.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(true, 0, "Failed")]
    [InlineData(false, 0, "Failed")]
    [InlineData(false, 1, "Partial")]
    [InlineData(false, 13, "Partial")]
    [InlineData(true, 16, "Completed")]
    public void ResolveProofMaterialHydrationDisposition_AllowsPartialSuccessfulDownloads(
        bool downloadOk,
        int availableVideoCount,
        string expected)
    {
        QueueMaterialStepService.ResolveProofMaterialHydrationDisposition(
                downloadOk,
                availableVideoCount)
            .ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(new int[0], 0, 1, new[] { 1 })]
    [InlineData(new[] { 1 }, 1, 2, new[] { 2 })]
    [InlineData(new[] { 1, 2 }, 2, 3, new[] { 3 })]
    [InlineData(new[] { 1, 2, 3 }, 3, 3, new int[0])]
    [InlineData(new[] { 9 }, 1, 2, new[] { 1 })]
    public void ResolveMissingProofMaterialEpisodes_DownloadsOnlyTheIncrementalMinimum(
        int[] existingEpisodeNumbers,
        int existingVideoCount,
        int requiredEpisodeCount,
        int[] expected)
    {
        QueueMaterialStepService.ResolveMissingProofMaterialEpisodes(
                existingEpisodeNumbers,
                existingVideoCount,
                requiredEpisodeCount)
            .Should().Equal(expected);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"proof-video-hydration-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
