using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokManagementUploadRecordSyncServiceTests
{
    [Fact]
    public void BuildRecordForSync_includes_tiktok_username_from_account_login()
    {
        var item = new QueueProjectItem
        {
            OriginalTitle = "沧海升盾：我与首领共筑海岛",
            NewTitle = "荒岛之上我与首领共建新邦",
            ProjectDir = @"E:\tiktok\荒岛之上我与首领共建新邦",
            EpisodeCount = 92,
            AccountProfileId = "acct-1dfecd83",
            AccountProfileName = "账号3",
            Remark = "需要人工复核封面",
            QueuedAt = "2026-07-04 17:26:47",
            StepStates = new Dictionary<string, string>
            {
                [QueueStepKeys.UploadSeries] = QueueStepStatus.Completed,
            },
        };
        var account = new TikTokAccountProfile
        {
            Id = "acct-1dfecd83",
            Name = "账号3",
            TiktokAccountNickname = "账号3",
            TiktokLoginEmail = "15327086817@163.com",
        };

        var record = TikTokManagementUploadRecordSyncService.BuildRecordForSync(item, account);

        record["tiktok_username"].Should().Be("15327086817@163.com");
        record["tiktok_account_username"].Should().Be("15327086817@163.com");
        record["tiktok_login_email"].Should().Be("15327086817@163.com");
        record["tiktok_account"].Should().Be("15327086817@163.com");
        record["account_profile_name"].Should().Be("账号3");
        record["remark"].Should().Be("需要人工复核封面");
    }
}
