using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokPublishedSeriesMatchTextTests
{
    [Fact]
    public void ParseNewTitles_TrimsDeduplicatesAndPreservesOrder()
    {
        var titles = TikTokPublishedSeriesMatchText.ParseNewTitles(
            " 剧集甲 \r\n剧集乙\n剧集甲\n\n剧集丙");

        Assert.Equal(["剧集甲", "剧集乙", "剧集丙"], titles);
    }

    [Theory]
    [InlineData("已发布", true)]
    [InlineData("Published", true)]
    [InlineData("published", true)]
    [InlineData("审核中", false)]
    [InlineData("草稿", false)]
    [InlineData("未发布", false)]
    [InlineData("发布中", false)]
    public void IsPublishedStatus_OnlyAcceptsExplicitPublishedStatus(
        string status,
        bool expected)
    {
        Assert.Equal(expected, TikTokPublishedSeriesMatchText.IsPublishedStatus(status));
    }

    [Theory]
    [InlineData("已发布", true)]
    [InlineData("Published", true)]
    [InlineData("分发受限", true)]
    [InlineData("Distribution restricted", true)]
    [InlineData("审核中", false)]
    [InlineData("草稿", false)]
    public void IsCopyrightProofEligibleStatus_AcceptsPublishedAndDistributionRestricted(
        string status,
        bool expected)
    {
        Assert.Equal(expected, TikTokPublishedSeriesMatchText.IsCopyrightProofEligibleStatus(status));
    }

    [Theory]
    [InlineData("未发布", false, true)]
    [InlineData("审核中", false, true)]
    [InlineData("草稿", false, true)]
    [InlineData("未发布", true, false)]
    [InlineData("分发受限", true, true)]
    [InlineData("已发布", true, true)]
    public void CanUseForCopyrightProofVideoRecovery_AllowsAnyStatusForMaterialOnly(
        string status,
        bool willEditTikTok,
        bool expected)
    {
        Assert.Equal(
            expected,
            TikTokPublishedSeriesMatchText.CanUseForCopyrightProofVideoRecovery(status, willEditTikTok));
    }

    [Fact]
    public void BuildPublishedTitlesCopyText_OnlyCopiesPublishedTitles()
    {
        var matches = new[]
        {
            Match("已发布甲", TikTokPublishedSeriesMatchKind.Published),
            Match("审核中乙", TikTokPublishedSeriesMatchKind.NotPublished, "审核中"),
            Match("已发布丙", TikTokPublishedSeriesMatchKind.Published),
        };

        var text = TikTokPublishedSeriesMatchText.BuildPublishedTitlesCopyText(matches);

        Assert.Equal($"已发布甲{Environment.NewLine}已发布丙", text);
    }

    [Fact]
    public void BuildDisplayText_GroupsResultsByOutcome()
    {
        var matches = new[]
        {
            Match("未找到甲", TikTokPublishedSeriesMatchKind.Missing),
            new TikTokPublishedSeriesMatch(
                "已发布乙",
                TikTokPublishedSeriesMatchKind.Published,
                "platform-status-detail",
                "7654321098765432100",
                Message: "query-message-detail"),
        };

        var text = TikTokPublishedSeriesMatchText.BuildDisplayText(matches);

        Assert.True(text.IndexOf("【已发布（1）】", StringComparison.Ordinal) <
                    text.IndexOf("【未找到（1）】", StringComparison.Ordinal));
        Assert.Contains("已发布乙", text);
        Assert.Contains("未找到甲", text);
        Assert.DoesNotContain("platform-status-detail", text);
        Assert.DoesNotContain("7654321098765432100", text);
        Assert.DoesNotContain("query-message-detail", text);
    }

    private static TikTokPublishedSeriesMatch Match(
        string title,
        TikTokPublishedSeriesMatchKind kind,
        string status = "") =>
        new(title, kind, status);
}
