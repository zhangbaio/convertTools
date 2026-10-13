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

    [Fact]
    public void BuildDuplicateCheckPayload_all_series_uses_original_names_without_account_scope()
    {
        var account = new TikTokAccountProfile
        {
            TiktokLoginEmail = "15327086817@163.com",
            TiktokAccountNickname = "账号3",
        };

        var payload = TikTokManagementUploadRecordSyncService.BuildDuplicateCheckPayload(
            ["盛夏的浪起", "盛夏的浪起", "金钱照冷暖"],
            "all_series",
            account);

        payload["dedupe_scope"].Should().Be("all_series");
        payload["platform"].Should().Be("tt");
        ((IEnumerable<string>)payload["original_names"]!).Should().Equal("盛夏的浪起", "金钱照冷暖");
        payload.Should().NotContainKey("tiktok_username");
        payload.Should().NotContainKey("tiktok_account_username");
        payload.Should().NotContainKey("normalize_original_names");
    }

    [Fact]
    public void BuildDuplicateCheckPayload_current_account_normalizes_original_names()
    {
        var account = new TikTokAccountProfile
        {
            TiktokLoginEmail = "3996972403@qq.com",
            TiktokAccountNickname = "赵可",
        };

        var payload = TikTokManagementUploadRecordSyncService.BuildDuplicateCheckPayload(
            ["《烬九州：第二十九季》"],
            "tiktok_username",
            account,
            normalizeTitles: true);

        payload["dedupe_scope"].Should().Be("tiktok_username");
        payload["tiktok_username"].Should().Be("3996972403@qq.com");
        payload["normalize_original_names"].Should().Be(true);
        ((IEnumerable<string>)payload["original_names"]!).Should().Equal("《烬九州：第二十九季》");
    }
}

public sealed class DramaDownloadExistenceServiceTests
{
    [Theory]
    [InlineData(true, false, true, false, "不存在")]
    [InlineData(true, true, true, false, "已归档")]
    [InlineData(true, false, true, true, "管理系统")]
    [InlineData(true, true, true, true, "已归档、管理系统")]
    [InlineData(false, false, true, false, "归档未核对")]
    [InlineData(true, true, false, false, "已归档、管理系统未核对")]
    [InlineData(false, false, false, false, "归档未核对、管理系统未核对")]
    public void Format_names_each_source(
        bool archiveChecked,
        bool inArchive,
        bool managementChecked,
        bool inManagement,
        string expected)
    {
        DramaDownloadExistenceResult.Format(archiveChecked, inArchive, managementChecked, inManagement)
            .Should().Be(expected);
    }

    [Fact]
    public void LabelFor_matches_original_title_after_punctuation_strip()
    {
        var result = new DramaDownloadExistenceResult(
            true,
            "",
            true,
            "",
            new HashSet<string>(["烬九州第二十九季"], StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));

        result.LabelFor("《烬九州：第二十九季》").Should().Be("已归档");
        result.LabelFor("另一部剧").Should().Be("不存在");
    }

    [Theory]
    [InlineData("不存在", DramaExistenceTone.Absent)]
    [InlineData("已归档", DramaExistenceTone.Present)]
    [InlineData("管理系统", DramaExistenceTone.Present)]
    [InlineData("已归档、管理系统", DramaExistenceTone.Present)]
    [InlineData("已归档、管理系统未核对", DramaExistenceTone.Present)]
    [InlineData("归档未核对", DramaExistenceTone.Unknown)]
    [InlineData("核对中", DramaExistenceTone.Unknown)]
    public void Tone_separates_absent_from_present(string label, DramaExistenceTone tone)
    {
        DramaDownloadExistenceResult.ToneFor(label).Should().Be(tone);
    }

    [Fact]
    public void CollectArchive_matches_original_title_and_folder_not_new_title()
    {
        var root = Path.Combine(Path.GetTempPath(), "tt-existence-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var archive = Path.Combine(workspace, "archive");
        Directory.CreateDirectory(Path.Combine(archive, "meta"));
        File.WriteAllText(
            Path.Combine(archive, "meta", "jin.json"),
            """
            {"archiveSource":"tiktok","originalTitle":"《烬九州：第二十九季》","newTitle":"完全不同的新剧名","projectKey":"jin"}
            """);
        Directory.CreateDirectory(Path.Combine(archive, "烬九州第二十九季"));
        File.WriteAllText(Path.Combine(archive, "烬九州第二十九季", "archive-meta.json"), """{"archiveSource":"tiktok","newTitle":"只在文件夹里"}""");

        try
        {
            var account = new TikTokAccountProfile
            {
                TiktokUploadProfilePath = workspace,
                TiktokArchiveRootDir = archive,
            };

            var archiveResult = DramaDownloadExistenceService.CollectArchive(account);
            archiveResult.Checked.Should().BeTrue();
            archiveResult.Keys.Should().Contain(AiRewriteHistoryService.NormalizeTitleKey("《烬九州：第二十九季》"));
            archiveResult.Keys.Should().Contain(AiRewriteHistoryService.NormalizeTitleKey("烬九州第二十九季"));
            archiveResult.Keys.Should().NotContain(AiRewriteHistoryService.NormalizeTitleKey("完全不同的新剧名"));
            archiveResult.Keys.Should().NotContain(AiRewriteHistoryService.NormalizeTitleKey("只在文件夹里"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
