using TikTokPublisher.Ui.Services;
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

    [Fact]
    public void DraftDetailUrlFromRow_reads_series_id_from_list_row()
    {
        Assert.Equal(
            "https://www.tiktokdramacenter.com/series/draft/7706505987917911092",
            TikTokEditFlowService.DraftDetailUrlFromRow(
                "The Lycan King Let Me Go, Now I'm Back With His Triplets ID: 7706505987917911092 " +
                "草稿 - RAMSA0664 +9 54 0 托管中 详情 素材 剧集表现"));
        Assert.Equal("", TikTokEditFlowService.DraftDetailUrlFromRow("尘封荣光归故里 草稿 54 托管中"));
        Assert.Equal("", TikTokEditFlowService.DraftDetailUrlFromRow(null));
    }

    [Fact]
    public void Submit_recovery_reopens_the_draft_from_the_current_edit_page()
    {
        Assert.Equal(
            "https://www.tiktokdramacenter.com/series/draft/7706723252315378709",
            EmbeddedBrowserPublishAutomation.ResolveSubmitRecoveryDraftUrl(
                "https://www.tiktokdramacenter.com/series/draft/7706723252315378709?tab=content",
                ""));
        Assert.Equal(
            "",
            EmbeddedBrowserPublishAutomation.ResolveSubmitRecoveryDraftUrl(
                "https://www.tiktokdramacenter.com/series/draft",
                ""));
    }

    [Theory]
    [InlineData("""{"code":0,"message":"success","data":{"token":"x"}}""", "code=0 msg=success")]
    [InlineData("""{"status_code":10002,"status_msg":"剧集信息不完整"}""", "code=10002 msg=剧集信息不完整")]
    [InlineData("""{"data":{},"BaseResp":{"StatusCode":0,"code":7,"msg":"busy"}}""", "code=7 msg=busy")]
    [InlineData("""{"data":{"id":"1"}}""", "")]
    [InlineData("not json", "")]
    [InlineData("", "")]
    public void DescribeSubmitResponseBody_keeps_only_code_and_message(string body, string expected)
    {
        Assert.Equal(expected, TikTokBrowserActions.DescribeSubmitResponseBody(body));
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
