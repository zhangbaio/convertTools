using FluentAssertions;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Tests;

public sealed class UploadTitleImportServiceTests
{
    [Fact]
    public void ResolveEpisodeLimitError_blocks_over_limit_when_global_switch_is_off()
    {
        var item = new DramaSearchItem
        {
            Title = "超长短剧",
            EpisodeTotal = 150,
        };
        var settings = new ClientSettings
        {
            TiktokAllowOverLimitUploadImport = false,
            TiktokOverLimitDownloadEpisodeCount = 120,
        };

        var error = UploadTitleImportService.ResolveEpisodeLimitError(item, settings);

        error.Should().Contain("大于最大限制 120");
    }

    [Fact]
    public void ResolveDownloadPlan_truncates_over_limit_items_when_global_switch_is_on()
    {
        var item = new DramaSearchItem
        {
            Title = "超长短剧",
            EpisodeTotal = 150,
        };
        var settings = new ClientSettings
        {
            TiktokAllowOverLimitUploadImport = true,
            TiktokOverLimitDownloadEpisodeCount = 120,
        };

        var error = UploadTitleImportService.ResolveEpisodeLimitError(item, settings);
        var plan = UploadTitleImportService.ResolveDownloadPlan(item, settings);

        error.Should().BeEmpty();
        plan.Truncated.Should().BeTrue();
        plan.Episodes.Should().Be("1-120");
        plan.EffectiveEpisodeCount.Should().Be(120);
    }
}
