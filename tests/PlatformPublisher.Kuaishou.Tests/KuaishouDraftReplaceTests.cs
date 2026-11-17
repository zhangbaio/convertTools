using System.Text.Json;
using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouDraftReplaceTests
{
    [Theory]
    [InlineData("56506737", "56506737")]
    [InlineData("  短剧ID：56506629  ", "56506629")]
    public void Parses_one_series_id(string text, string expected)
    {
        Assert.True(KuaishouDraftReplace.TryParseSeriesId(text, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("津门旧案")]
    [InlineData("12")]
    [InlineData("56506737 56506629")]
    public void Rejects_text_that_is_not_one_series_id(string text)
    {
        Assert.False(KuaishouDraftReplace.TryParseSeriesId(text, out _));
    }

    [Fact]
    public void Rejected_offline_series_can_be_replaced()
    {
        using var document = JsonDocument.Parse("""
            {"result":1,"data":{"totalCount":1,"data":[{"miniSeriesId":56506737,"courseName":"津门旧案","auditStatus":4,"sellingStatus":2}]}}
            """);
        Assert.True(KuaishouDraftReplace.TryRead(document.RootElement, "56506737", out var target));
        Assert.Equal("津门旧案", target.Title);
        Assert.Equal("", KuaishouDraftReplace.Refusal(target));
        Assert.Equal("", KuaishouDraftReplace.Refusal(target with { AuditStatus = KuaishouDraftReplace.PendingAuditStatus, SellingStatus = 0 }));
    }

    [Fact]
    public void Listed_reviewing_and_published_series_stay_in_place()
    {
        var rejected = new KuaishouDraftReplace.Target("56506737", "津门旧案", KuaishouDraftReplace.RejectedAuditStatus, KuaishouShelfApi.OfflineSellingStatus);
        Assert.Equal("《津门旧案》（56506737）已上架，不能替换。", KuaishouDraftReplace.Refusal(rejected with { SellingStatus = KuaishouShelfApi.OnlineSellingStatus }));
        Assert.Equal("《津门旧案》（56506737）被平台风控下架，不能替换。", KuaishouDraftReplace.Refusal(rejected with { SellingStatus = KuaishouShelfApi.RiskSellingStatus }));
        Assert.Equal("《津门旧案》（56506737）正在审核，不能替换。", KuaishouDraftReplace.Refusal(rejected with { AuditStatus = KuaishouDraftReplace.ReviewingAuditStatus }));
        Assert.Equal("《津门旧案》（56506737）已发布成功，不能替换。", KuaishouDraftReplace.Refusal(rejected with { AuditStatus = KuaishouDraftReplace.PublishedAuditStatus }));
        Assert.Equal("《津门旧案》（56506737）当前不能替换。", KuaishouDraftReplace.Refusal(rejected with { AuditStatus = 0 }));
    }

    [Fact]
    public void A_local_project_keeps_its_own_series_id()
    {
        Assert.Equal("", KuaishouDraftReplace.BindingMessage("", "56506737"));
        Assert.Equal("", KuaishouDraftReplace.BindingMessage("56506737", "56506737"));
        Assert.Equal("这部本地剧已经绑定短剧 56506629，不能再换到 56506737。", KuaishouDraftReplace.BindingMessage("56506629", "56506737"));
    }

    [Fact]
    public void Episode_counts_must_match_before_videos_are_replaced()
    {
        Assert.Equal("", KuaishouDraftReplace.EpisodeCountMessage(80, 80, "56506737"));
        Assert.Equal("短剧 56506737 没有单集，已停止替换。", KuaishouDraftReplace.EpisodeCountMessage(0, 80, "56506737"));
        Assert.Equal("平台短剧有 90 集，本地有 80 集，已停止替换。", KuaishouDraftReplace.EpisodeCountMessage(90, 80, "56506737"));
    }

    [Fact]
    public void Replace_reuploads_episodes_that_already_have_video()
    {
        using var uploaded = JsonDocument.Parse("""{"videoUploadStatus":"SUCCESS"}""");
        using var failed = JsonDocument.Parse("""{"videoUploadStatus":"FAIL"}""");
        Assert.True(KuaishouApiVideoUploader.EpisodeUploaded(uploaded.RootElement, replaceExisting: false));
        Assert.True(KuaishouApiVideoUploader.SkipUploadedEpisode(uploaded: true, replaceExisting: false));
        Assert.False(KuaishouApiVideoUploader.SkipUploadedEpisode(uploaded: true, replaceExisting: true));
        Assert.False(KuaishouApiVideoUploader.EpisodeUploaded(failed.RootElement, replaceExisting: true));
        Assert.Throws<InvalidOperationException>(() => KuaishouApiVideoUploader.EpisodeUploaded(failed.RootElement, replaceExisting: false));
    }

    [Fact]
    public void Daily_cap_does_not_stop_a_replace_that_is_still_running()
    {
        Assert.True(KuaishouDraftReplace.ContinueDespiteDailyCap(new KuaishouPersonalUploadState
        {
            ReplaceExistingVideos = true,
            MiniSeriesId = "56506737",
            Status = "running",
        }));
        Assert.True(KuaishouDraftReplace.ContinueDespiteDailyCap(new KuaishouPersonalUploadState
        {
            ReplaceExistingVideos = true,
            MiniSeriesId = "56506737",
            Status = "failed",
        }));
        Assert.False(KuaishouDraftReplace.ContinueDespiteDailyCap(new KuaishouPersonalUploadState
        {
            ReplaceExistingVideos = true,
            MiniSeriesId = "56506737",
            Status = "completed",
        }));
        Assert.False(KuaishouDraftReplace.ContinueDespiteDailyCap(new KuaishouPersonalUploadState
        {
            MiniSeriesId = "56506737",
            Status = "running",
        }));
    }
}
