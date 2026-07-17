using FluentAssertions;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Tests;

public sealed class VideoFramePosterSourceServiceTests
{
    [Fact]
    public void BuildCandidateTimes_preserves_priority_clamps_and_deduplicates_to_tenths()
    {
        var candidates = VideoFramePosterSourceService.BuildCandidateTimes(
            preferredTime: 1.0,
            duration: 10.0,
            neighborOffsets: "2，4",
            fallbackPercents: "10,25,50,75");

        candidates.Should().Equal(1.0, 0.1, 3.0, 5.0, 2.5, 7.5);
    }

    [Fact]
    public void BuildCandidateTimes_uses_default_lists_when_configured_values_are_invalid()
    {
        var candidates = VideoFramePosterSourceService.BuildCandidateTimes(
            preferredTime: 5.0,
            duration: 100.0,
            neighborOffsets: "not-a-number",
            fallbackPercents: "invalid");

        candidates.Should().Equal(5.0, 3.0, 7.0, 1.0, 9.0, 10.0, 25.0, 50.0, 75.0);
    }

    [Theory]
    [InlineData("十年归来-第002集.mp4", 2)]
    [InlineData("episode-003-final.mp4", 3)]
    [InlineData("04_story.mp4", 4)]
    public void SelectVideoForEpisode_selects_supported_episode_file_names(string expectedName, int desiredEpisode)
    {
        var videos = new[]
        {
            Path.Combine("videos", "01_story.mp4"),
            Path.Combine("videos", "十年归来-第002集.mp4"),
            Path.Combine("videos", "episode-003-final.mp4"),
            Path.Combine("videos", "04_story.mp4"),
        };

        var selected = VideoFramePosterSourceService.SelectVideoForEpisode(videos, desiredEpisode);

        Path.GetFileName(selected).Should().Be(expectedName);
    }

    [Fact]
    public void SelectVideoForEpisode_returns_first_video_when_requested_episode_is_absent()
    {
        var videos = new[]
        {
            Path.Combine("videos", "episode-001.mp4"),
            Path.Combine("videos", "episode-002.mp4"),
        };

        var selected = VideoFramePosterSourceService.SelectVideoForEpisode(videos, desiredEpisode: 9);

        selected.Should().Be(videos[0]);
    }

    [Fact]
    public void SelectVideoForEpisode_rejects_an_empty_video_list()
    {
        var action = () => VideoFramePosterSourceService.SelectVideoForEpisode([], desiredEpisode: 1);

        action.Should().Throw<ArgumentException>()
            .WithParameterName("videos");
    }
}
