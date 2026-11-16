using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouDailyUploadLimitTests
{
    [Theory]
    [InlineData("您今日的短剧上传已达上限")]
    [InlineData("目前您账号每天最多上传10部短剧，您今日上传额度已用完。")]
    [InlineData("每天最多上传 10 部")]
    [InlineData("为保证内容审核质量，每位创作者每日限传10部")]
    [InlineData("快手分账今日短剧上传已达上限，每天最多 10 部。队列已停止，今天不再分发。")]
    public void Matches_platform_daily_cap(string text)
    {
        Assert.True(KuaishouDailyUploadLimit.Matches(text));
        Assert.True(KuaishouDailyUploadLimit.Is(new InvalidOperationException(text)));
    }

    [Fact]
    public void Banner_about_a_general_limit_is_not_the_account_cap()
    {
        const string banner = "平台现已对每日短剧上传数量进行优化调整，对每位用户每日上传数量进行限制。";
        Assert.False(KuaishouDailyUploadLimit.Matches(banner));
    }

    [Fact]
    public void Failed_draft_can_continue_after_the_daily_cap()
    {
        Assert.True(KuaishouDailyUploadLimit.AllowsResume(new KuaishouPersonalUploadState
        {
            Status = "failed",
            MiniSeriesId = "56334999",
            FirstPageCompleted = true,
            VideosUploaded = false,
            LastError = "视频上传完成处理错误",
        }));
    }

    [Fact]
    public void New_series_and_finished_uploads_stay_blocked_after_the_daily_cap()
    {
        Assert.False(KuaishouDailyUploadLimit.AllowsResume(new KuaishouPersonalUploadState
        {
            Status = "failed",
            CurrentStage = "api_upload",
        }));
        Assert.False(KuaishouDailyUploadLimit.AllowsResume(new KuaishouPersonalUploadState
        {
            Status = "completed",
            MiniSeriesId = "51661633",
            FirstPageCompleted = true,
            VideosUploaded = true,
            ReviewSubmitted = true,
        }));
        Assert.False(KuaishouDailyUploadLimit.AllowsResume(null));
    }
}
