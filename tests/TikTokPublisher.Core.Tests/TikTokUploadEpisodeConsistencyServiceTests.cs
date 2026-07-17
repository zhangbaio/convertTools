using FluentAssertions;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokUploadEpisodeConsistencyServiceTests
{
    [Fact]
    public void ValidateBeforeUpload_Fails_When_Source_Is_Missing_Declared_Episode()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"episode-consistency-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspace, "source");
        var stagingDir = Path.Combine(workspace, "workflow", "source", TikTokUploadStagingService.StagingDirName);
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(stagingDir);

        try
        {
            for (var episode = 1; episode <= 99; episode++)
                File.WriteAllBytes(Path.Combine(sourceDir, $"show-第{episode}集.mp4"), [1]);
            File.WriteAllBytes(Path.Combine(sourceDir, "show-第12集.mp4.silencefix.mp4"), [2]);

            for (var episode = 1; episode <= 100; episode++)
                File.WriteAllBytes(Path.Combine(stagingDir, $"renamed-第{episode}集.mp4"), [3]);

            var result = TikTokUploadEpisodeConsistencyService.ValidateBeforeUpload(new PublishItem
            {
                ProjectDir = sourceDir,
                EpisodeCount = 100,
            });

            result.Ok.Should().BeFalse();
            result.ExpectedCount.Should().Be(100);
            result.SourceVideoCount.Should().Be(99);
            result.MissingEpisodes.Should().Equal(100);
            result.Message.Should().Contain("第 100 集");
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    [Fact]
    public void ValidateBeforeUpload_Passes_When_Source_Episodes_Match_Declared_Count()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), $"episode-consistency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectDir);

        try
        {
            for (var episode = 1; episode <= 3; episode++)
                File.WriteAllBytes(Path.Combine(projectDir, $"show-第{episode}集.mp4"), [1]);

            var result = TikTokUploadEpisodeConsistencyService.ValidateBeforeUpload(new PublishItem
            {
                ProjectDir = projectDir,
                EpisodeCount = 3,
            });

            result.Ok.Should().BeTrue(result.Message);
        }
        finally
        {
            TryDelete(projectDir);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
