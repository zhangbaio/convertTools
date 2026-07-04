using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class DramaSourceRouterDownloadTests
{
    [Fact]
    public void TryBuildSuccessfulResultWhenVideosExist_Should_Return_Null_When_OutputDir_Has_No_Videos()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        try
        {
            var request = new DramaDownloadRequest(
                ProjectDir: outputDir,
                OutputDir: outputDir,
                DisplayName: "测试剧",
                BookId: "book-1",
                Episodes: "all",
                Quality: "1080P",
                Concurrent: 3,
                EpisodeNumberMode: "source");

            DramaSourceRouter.TryBuildSuccessfulResultWhenVideosExist(request).Should().BeNull();
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public void TryBuildSuccessfulResultWhenVideosExist_Should_Return_Success_When_Videos_Already_Present()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"drama-router-videos-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        File.WriteAllBytes(Path.Combine(outputDir, "第01集.mp4"), [1, 2, 3]);

        try
        {
            var request = new DramaDownloadRequest(
                ProjectDir: outputDir,
                OutputDir: outputDir,
                DisplayName: "测试剧",
                BookId: "book-1",
                Episodes: "all",
                Quality: "1080P",
                Concurrent: 3,
                EpisodeNumberMode: "source");

            var result = DramaSourceRouter.TryBuildSuccessfulResultWhenVideosExist(request);

            result.Should().NotBeNull();
            result!.Ok.Should().BeTrue();
            result.VideoCount.Should().Be(1);
            result.Message.Should().Contain("跳过 legacy 红果下载重试");
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }
}
