using ChannelsPublisher.Clip;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public sealed class CommentarySubtitleMaskTests
{
    [Fact]
    public void RepeatedHighSubtitleBuildsATightMaskWithoutFollowingAnIsolatedTextLine()
    {
        var regions = CommentarySubtitleMask.SelectRegions([
            new(0, 0.44, 0.682, 0.12, 0.031),
            new(1, 0.13, 0.685, 0.74, 0.033),
            new(2, 0.31, 0.680, 0.38, 0.029),
            new(3, 0.29, 0.910, 0.42, 0.025),
        ]);

        var region = Assert.Single(regions);
        Assert.True(region.Top < 0.680 && region.Bottom > 0.718);
        Assert.True(region.Bottom < 0.77);
        Assert.True(region.Left < 0.13 && region.Right > 0.87);
    }

    [Fact]
    public void TwoRepeatedSubtitlePositionsGetSeparateMasks()
    {
        var regions = CommentarySubtitleMask.SelectRegions(
            Enumerable.Range(0, 6).Select(frame => new SubtitleObservation(frame, 0.30, 0.68, 0.4, 0.03))
                .Concat(Enumerable.Range(6, 4).Select(frame => new SubtitleObservation(frame, 0.26, 0.89, 0.48, 0.03))));

        Assert.Collection(regions,
            region => Assert.InRange(region.Top, 0.65, 0.68),
            region => Assert.InRange(region.Top, 0.86, 0.89));
    }

    [Fact]
    public void FallbackCoversBothRaisedAndBottomSubtitles()
    {
        var regions = CommentarySubtitleMask.Fallback(0.18);
        Assert.Contains(regions, region => region.Top < 0.7 && region.Bottom > 0.7);
        Assert.Contains(regions, region => region.Top < 0.9 && region.Bottom > 0.9);
        var filters = CommentarySubtitleMask.BuildFilter(regions, new ClipMediaInfo(4000, 720, 1280, true, false));
        Assert.Equal(2, filters.Split(',').Length);
        Assert.All(filters.Split(','), filter => Assert.StartsWith("delogo=x=", filter));
    }
}
