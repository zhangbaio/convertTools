using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokUploadProgressParserTests
{
  [Fact]
  public void DetectUploadActivity_IgnoresStaleBodyMarker_WhenVideoTableIsComplete()
  {
    var activity = TikTokUploadProgressParser.DetectUploadActivity(
      "页面其他区域残留：处理中",
      ["第 85 集\n三萌宝帮母认亲寻父-第85集.mp4\n已上传"]);

    activity.Uploading.Should().BeFalse();
    activity.WaitingCount.Should().Be(0);
    activity.IsTableScoped.Should().BeTrue();
    TikTokUploadProgressParser.IsUploadComplete(85, 85, activity).Should().BeTrue();
  }

  [Fact]
  public void DetectUploadActivity_RejectsActiveVideoTable()
  {
    var activity = TikTokUploadProgressParser.DetectUploadActivity(
      "正片内容（85）",
      ["第 85 集\n三萌宝帮母认亲寻父-第85集.mp4\n处理中"]);

    activity.Uploading.Should().BeTrue();
    TikTokUploadProgressParser.IsUploadComplete(85, 85, activity).Should().BeFalse();
  }

  [Theory]
  [InlineData("等待中")]
  [InlineData("等待上传")]
  public void DetectUploadActivity_RejectsWaitingVideoTable(string waitingStatus)
  {
    var activity = TikTokUploadProgressParser.DetectUploadActivity(
      "正片内容（85）",
      [$"第 85 集\n三萌宝帮母认亲寻父-第85集.mp4\n{waitingStatus}"]);

    activity.WaitingCount.Should().Be(1);
    TikTokUploadProgressParser.IsUploadComplete(85, 85, activity).Should().BeFalse();
  }

  [Fact]
  public void DetectUploadActivity_FallsBackToBody_WhenNoVideoTableWasFound()
  {
    var activity = TikTokUploadProgressParser.DetectUploadActivity(
      "正在上传 50%",
      ["合同名称\n默认合同"]);

    activity.Uploading.Should().BeTrue();
    activity.IsTableScoped.Should().BeFalse();
  }

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
