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
    public void ProofMaterialVideoLease_DeletesOnlyFilesCreatedForHydration()
    {
        using var temp = new TemporaryDirectory();
        var existing = Path.Combine(temp.Path, "existing.mp4");
        var hydrated = Path.Combine(temp.Path, "hydrated.mp4");
        File.WriteAllBytes(existing, [1]);
        File.WriteAllBytes(hydrated, [2]);
        var logs = new List<string>();

        using (new QueueMaterialStepService.ProofMaterialVideoLease([hydrated], logs.Add))
        {
            File.Exists(existing).Should().BeTrue();
            File.Exists(hydrated).Should().BeTrue();
        }

        File.Exists(existing).Should().BeTrue();
        File.Exists(hydrated).Should().BeFalse();
        logs.Should().ContainSingle(message => message.Contains("1/1", StringComparison.Ordinal));
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
