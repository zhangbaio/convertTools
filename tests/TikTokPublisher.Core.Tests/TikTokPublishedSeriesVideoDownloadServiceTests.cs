using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokPublishedSeriesVideoDownloadServiceTests
{
    [Fact]
    public void Web_video_download_uses_headless_browser_by_default()
    {
        TikTokPublishedSeriesVideoDownloadService.DefaultHeadless.Should().BeTrue();
    }

    [Fact]
    public void Web_video_download_reuses_current_account_storage_state()
    {
        var authPath = Path.Combine(
            Path.GetTempPath(),
            $"web-video-auth-{Guid.NewGuid():N}.json");
        var account = new TikTokAccountProfile
        {
            Id = "acct-web-video",
            TiktokStorageStatePath = authPath,
        };

        var plan = TikTokPublishedSeriesVideoDownloadService.ResolveBrowserLaunchPlan(account);

        plan.Headless.Should().BeTrue();
        plan.AuthPath.Should().Be(Path.GetFullPath(authPath));
    }

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
