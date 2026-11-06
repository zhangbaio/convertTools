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
    public void DetailConfirmsSubmitted_accepts_review_text_even_with_draft_word()
    {
        Assert.True(TikTokEditFlowService.DetailConfirmsSubmitted("草稿 视频检测中"));
        Assert.True(TikTokEditFlowService.DetailConfirmsSubmitted("审核中"));
        Assert.False(TikTokEditFlowService.DetailConfirmsSubmitted("草稿 - DramaReel_vn 71 托管中"));
        Assert.False(TikTokEditFlowService.DetailConfirmsSubmitted(""));
    }

    [Theory]
    [InlineData(30, 30, false, false, false, true)]
    [InlineData(27, 30, true, false, false, true)]
    [InlineData(27, 30, false, false, true, true)]
    [InlineData(27, 30, false, false, false, false)]
    public void BatchLooksAccepted_uses_row_count_or_visible_activity(
        int rowCount,
        int targetReady,
        bool episodesOnPage,
        bool namesInBody,
        bool uploadActivity,
        bool expected)
    {
        Assert.Equal(
            expected,
            TikTokBatchUploadService.BatchLooksAccepted(
                rowCount, targetReady, episodesOnPage, namesInBody, uploadActivity));
    }
}
