using TikTokPublisher.Core.Remote;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class XingeTikTokProcessedSeriesTests
{
    [Fact]
    public void StableSourceIdWinsOverTitleAndEpisodeCount()
    {
        XingeTikTokProcessedFact[] facts =
        [
            new("account-a", "archived", "dramabox:123", "Changed title", 34),
            new("account-b", "uploaded", "dramabox:124", "Original title", 22),
        ];

        var match = Assert.Single(XingeTikTokProcessedSeries.FindMatches(new("Original title", 22, "123"), "dramabox", facts));
        Assert.Equal("archived", match.Kind);
        Assert.Empty(XingeTikTokProcessedSeries.FindMatches(new("Original title", 22, "125"), "dramabox", facts));
    }

    [Fact]
    public void LegacyRecordRequiresBothOriginalTitleAndEpisodeCount()
    {
        XingeTikTokProcessedFact[] facts = [new("account-a", "uploaded", "", "Anna’s   Day", 52)];

        Assert.Single(XingeTikTokProcessedSeries.FindMatches(new TikTokUploadSeriesSpec("anna's day", 52, "dramabox:100"), "dramabox", facts));
        Assert.Empty(XingeTikTokProcessedSeries.FindMatches(new("anna's day", 51, "dramabox:100"), "dramabox", facts));
        Assert.Empty(XingeTikTokProcessedSeries.FindMatches(new("anna's day", 0, "dramabox:100"), "dramabox", facts));
    }
}
