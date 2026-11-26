using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouAutoArchivePolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Old_account_settings_cannot_stop_the_upload_before_review(bool enterprise)
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<KuaishouPersonalConfig>(
            """{"FirstPageAction":"draft","FinalAction":"keep"}""")!;
        var overlay = KuaishouPersonalFormResolver.SettingsOverlay(config, enterprise);
        Assert.Equal("next", overlay["kuaishou_first_page_submit_action"]);
        Assert.Equal("submit_review", overlay["kuaishou_final_submit_action"]);
        Assert.DoesNotContain("FinalAction", System.Text.Json.JsonSerializer.Serialize(config));
    }

    [Theory]
    [InlineData("draft_saved", "draft_saved", false, true)]
    [InlineData("manual_hold", "manual_hold", false, true)]
    [InlineData("completed", "videos_uploaded", false, true)]
    [InlineData("completed", "review_submitted", true, false)]
    [InlineData("success", "completed", false, false)]
    public void Distinguishes_old_stopping_points_from_completed_uploads(
        string status, string stage, bool submitted, bool expected)
    {
        var state = new KuaishouPersonalUploadState { Status = status, CurrentStage = stage, ReviewSubmitted = submitted };
        Assert.Equal(expected, state.WasStoppedBeforeReview());
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Archives_only_when_the_option_is_on_and_upload_is_finished(bool option, bool uploaded, bool expected)
    {
        Assert.Equal(expected, KuaishouAutoArchivePolicy.ShouldArchive(option, uploaded));
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "未执行", true)]
    [InlineData(false, "已完成", true)]
    [InlineData(false, "已完成 77/77", true)]
    [InlineData(false, "失败", false)]
    [InlineData(false, "未执行", false)]
    [InlineData(false, null, false)]
    public void Upload_counts_as_finished_from_the_step_or_the_column(bool stepSucceeded, string? column, bool expected)
    {
        Assert.Equal(expected, KuaishouAutoArchivePolicy.UploadCompleted(stepSucceeded, column));
    }
}
