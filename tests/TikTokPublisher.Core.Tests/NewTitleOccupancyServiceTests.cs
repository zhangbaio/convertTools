using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class NewTitleOccupancyServiceTests
{
    [Fact]
    public void Missing_archive_directory_skips_check()
    {
        var workspace = Directory.CreateTempSubdirectory().FullName;
        var archive = Path.Combine(workspace, "archive-missing");

        var snapshot = NewTitleOccupancyService.LoadArchiveTitles(workspace, archive, "", "");

        snapshot.Checked.Should().BeFalse();
        snapshot.Titles.Should().BeEmpty();
        snapshot.SkipReason.Should().Be("归档目录不存在");
    }

    [Fact]
    public void Archive_exact_title_is_occupied_and_current_project_is_excluded()
    {
        var workspace = Directory.CreateTempSubdirectory().FullName;
        var archive = Path.Combine(workspace, "archive");
        var currentProject = Path.Combine(workspace, "当前项目");
        Directory.CreateDirectory(Path.Combine(archive, "meta"));
        WriteArchiveMeta(archive, "other", "《山野种田养出满仓粮》", Path.Combine(workspace, "其他项目"));
        WriteArchiveMeta(archive, "self", "山野耕耘换来满仓粮", currentProject);

        var snapshot = NewTitleOccupancyService.LoadArchiveTitles(
            workspace,
            archive,
            currentProject,
            "");

        snapshot.Checked.Should().BeTrue();
        snapshot.Titles.Should().Equal("《山野种田养出满仓粮》");
        NewTitleOccupancyService.IsExactMatch("山野种田养出满仓粮", snapshot.Titles).Should().BeTrue();
        NewTitleOccupancyService.IsExactMatch(" 山野种田养出满仓粮 ", snapshot.Titles).Should().BeTrue();
        NewTitleOccupancyService.IsExactMatch("山野种田养出满仓禾", snapshot.Titles).Should().BeFalse();
        NewTitleOccupancyService.IsExactMatch("山野耕耘换来满仓粮", snapshot.Titles).Should().BeFalse();
    }

    [Fact]
    public void Failed_checks_do_not_force_regeneration()
    {
        var skipped = NewTitleOccupancySnapshot.Skipped("归档目录不存在");

        var decision = NewTitleOccupancyService.Decide(
            "山野种田养出满仓粮",
            skipped,
            managementChecked: false,
            managementDuplicates: ["山野种田养出满仓粮"]);

        decision.Regenerate.Should().BeFalse();
    }

    [Fact]
    public void Successful_management_exact_match_forces_regeneration()
    {
        var decision = NewTitleOccupancyService.Decide(
            "《山野种田养出满仓粮》",
            NewTitleOccupancySnapshot.Ready([]),
            managementChecked: true,
            managementDuplicates: ["山野种田养出满仓粮"]);

        decision.Regenerate.Should().BeTrue();
        decision.Reason.Should().Be("管理系统");
    }

    [Fact]
    public void Concurrent_claim_keeps_one_title_per_account()
    {
        var accountKey = "id:occupancy-" + Guid.NewGuid().ToString("N");
        var title = "山野种田养出满仓粮";
        var first = Path.Combine(Path.GetTempPath(), "project-a");
        var second = Path.Combine(Path.GetTempPath(), "project-b");
        try
        {
            NewTitleOccupancyService.TryClaim(accountKey, title, first).Should().BeTrue();
            NewTitleOccupancyService.TryClaim(accountKey, "《山野种田养出满仓粮》", first).Should().BeTrue();
            NewTitleOccupancyService.TryClaim(accountKey, title, second).Should().BeFalse();
            NewTitleOccupancyService.TryClaim("id:other-account", title, second).Should().BeTrue();
        }
        finally
        {
            NewTitleOccupancyService.Release(accountKey, title, first);
            NewTitleOccupancyService.Release("id:other-account", title, second);
        }
    }

    [Fact]
    public void New_title_payload_is_scoped_to_the_tiktok_account()
    {
        var account = new TikTokAccountProfile
        {
            TiktokLoginEmail = "15327086817@163.com",
            TiktokAccountNickname = "账号3",
        };

        var payload = TikTokManagementUploadRecordSyncService.BuildNewTitleDuplicateCheckPayload(
            ["《山野种田养出满仓粮》", "山野种田养出满仓粮", "山野耕耘换来满仓粮"],
            account);

        payload["dedupe_scope"].Should().Be("tiktok_username");
        payload["platform"].Should().Be("tt");
        payload["tiktok_username"].Should().Be("15327086817@163.com");
        ((IEnumerable<string>)payload["new_names"]!).Should().Equal(
            "《山野种田养出满仓粮》",
            "山野种田养出满仓粮",
            "山野耕耘换来满仓粮");
        payload.Should().NotContainKey("original_names");
    }

    [Fact]
    public async Task Missing_tiktok_username_skips_management_check()
    {
        var result = await TikTokManagementUploadRecordSyncService.CheckDuplicateNewNamesAsync(
            ["山野种田养出满仓粮"],
            new TikTokAccountProfile { Name = "账号1" },
            CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("TikTok 用户名");
        result.Duplicates.Should().BeEmpty();
    }

    private static void WriteArchiveMeta(string archiveRoot, string name, string newTitle, string sourceProjectDir)
    {
        var path = Path.Combine(archiveRoot, "meta", name + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            newTitle,
            originalTitle = "原剧名",
            archiveSource = "tiktok",
            sourceProjectDir,
        }));
    }
}
