using PlatformPublisher.Common.Models;
using PlatformPublisher.Weixin.Publishing;
using Xunit;

namespace PlatformPublisher.Weixin.Tests;

public sealed class WeixinPublishOptionsTests
{
    [Fact]
    public void OptionsRoundTripAndResolveExplicitEpisodes()
    {
        var source = new WeixinPublishOptions
        {
            EpisodeSelectionMode = "explicit",
            EpisodeIndexes = "2,3,3,7,99",
            MergePublishEnabled = true,
            MergePublishGroupSize = 2,
            FillShortTitle = true,
            ShortTitleMaxLength = 12,
            ReplaceCoverWithLocalImage = true,
            CoverImagePath = @"D:\covers\poster.png",
            FinalAction = "draft",
        };

        var restored = WeixinPublishOptions.FromJob(new PublishJob { PlatformOptionsJson = source.ToJson() });
        var indexes = restored.ResolveEpisodeIndexes(8, requestedCount: 5);

        Assert.Equal([2, 3, 7], indexes);
        Assert.True(restored.MergePublishEnabled);
        Assert.Equal(2, restored.MergePublishGroupSize);
        Assert.True(restored.FillShortTitle);
        Assert.Equal(12, restored.ShortTitleMaxLength);
        Assert.True(restored.ReplaceCoverWithLocalImage);
        Assert.Equal("draft", restored.FinalAction);
    }

    [Fact]
    public void InvalidJsonFallsBackToLegacyJobFields()
    {
        var restored = WeixinPublishOptions.FromJob(new PublishJob
        {
            PlatformOptionsJson = "{invalid",
            PublishDescription = "旧描述",
            DeclareOriginal = false,
            HideLocation = false,
        });

        Assert.Equal("热门短剧，精彩内容持续更新。", restored.DescriptionTemplate);
        Assert.True(restored.DeclareOriginal);
    }
}
