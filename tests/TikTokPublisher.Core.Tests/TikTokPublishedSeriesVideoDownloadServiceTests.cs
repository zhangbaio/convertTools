using FluentAssertions;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokPublishedSeriesVideoDownloadServiceTests
{
    [Fact]
    public void ResolveTargetEpisodes_preserves_only_distinct_positive_requested_episodes()
    {
        var episodes = TikTokPublishedSeriesVideoDownloadService.ResolveTargetEpisodes(
            [12, 10, 12, 0, -1, 11],
            requiredEpisodeCount: 3,
            platformEpisodeCount: 60);

        episodes.Should().Equal(10, 11, 12);
    }

    [Fact]
    public void ResolveTargetEpisodes_uses_required_prefix_when_no_explicit_selection()
    {
        var episodes = TikTokPublishedSeriesVideoDownloadService.ResolveTargetEpisodes(
            requestedEpisodes: null,
            requiredEpisodeCount: 5,
            platformEpisodeCount: 3);

        episodes.Should().Equal(1, 2, 3);
    }
}
