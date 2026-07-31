using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokProofMaterialVideoHydrationTests
{
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
