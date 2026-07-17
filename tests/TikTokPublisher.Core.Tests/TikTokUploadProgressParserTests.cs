using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokUploadProgressParserTests
{
  [Fact]
  public void ExtractReadyUploadedVideoCount_IgnoresUnmatchedEpisodeLinesWhenTitleGiven()
  {
    const string text = "第1集 无关旧剧-第1集\n草稿\n第2集 无关旧剧-第2集\n草稿";

    TikTokUploadProgressParser
      .ExtractReadyUploadedVideoCount(text, ["当前新剧名"])
      .Should()
      .BeNull();
  }

  [Fact]
  public void ExtractReadyUploadedVideoCount_AllowsHeadingCountWithTitleGiven()
  {
    const string text = "内容上传\n正片内容 (2)\n第1集 无关旧剧-第1集\n草稿";

    TikTokUploadProgressParser
      .ExtractReadyUploadedVideoCount(text, ["当前新剧名"])
      .Should()
      .Be(2);
  }

  [Fact]
  public void ExtractReadyUploadedVideoCount_RequiresCompletedStatusForVisibleRows()
  {
    const string uploadingText =
      "内容上传\n正片内容 (2)\n视频\t状态\t操作\n" +
      "第1集 当前新剧名-第1集\n处理中\n" +
      "第2集 当前新剧名-第2集\n等待上传";
    const string completedText =
      "内容上传\n正片内容 (2)\n视频\t状态\t操作\n" +
      "第1集 当前新剧名-第1集\n草稿\n" +
      "第2集 当前新剧名-第2集\n草稿";

    TikTokUploadProgressParser
      .ExtractReadyUploadedVideoCount(uploadingText, ["当前新剧名"])
      .Should()
      .BeNull();
    TikTokUploadProgressParser
      .ExtractReadyUploadedVideoCount(completedText, ["当前新剧名"])
      .Should()
      .Be(2);
  }

  [Theory]
  [InlineData(null, 43, 28, 35)]
  [InlineData(null, 43, 0, 100)]
  [InlineData(15, 43, 28, 35)]
  [InlineData(43, 43, 0, 100)]
  public void EstimateDisplayPercent_UsesUploadedOrWaitingFallback(
    int? uploadedCount,
    int expectedCount,
    int waitingCount,
    int expectedPercent)
  {
    TikTokUploadProgressParser
      .EstimateDisplayPercent(uploadedCount, expectedCount, waitingCount)
      .Should()
      .Be(expectedPercent);
  }
}
