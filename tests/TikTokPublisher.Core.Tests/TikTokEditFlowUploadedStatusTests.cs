using TikTokPublisher.Ui.Services.TikTok;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokEditFlowUploadedStatusTests
{
    [Theory]
    [InlineData("贪财俏妻与隐退战神王爷 ID 7686667993543431189 67 集 视频检测中", true)]
    [InlineData("视频检测中", true)]
    [InlineData("审核中", true)]
    [InlineData("待审核", true)]
    [InlineData("发布中", true)]
    [InlineData("已发布", true)]
    [InlineData("已上线", true)]
    [InlineData("Submitted", true)]
    [InlineData("In review", true)]
    [InlineData("Published", true)]
    [InlineData("草稿", false)]
    [InlineData("贪财俏妻与隐退战神王爷 草稿", false)]
    [InlineData("草稿 视频检测中", false)]
    [InlineData("审核不通过", false)]
    [InlineData("未通过", false)]
    [InlineData("", false)]
    public void IsNonDraftUploadedStatus_AcceptsSubmittedSeriesAndRejectsDrafts(string rowText, bool expected)
    {
        Assert.Equal(expected, TikTokEditFlowService.IsNonDraftUploadedStatus(rowText));
    }

    [Fact]
    public void IsNonDraftUploadedStatus_RejectsNull()
    {
        Assert.False(TikTokEditFlowService.IsNonDraftUploadedStatus(null));
    }
}
