using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class CopyrightProofLocalHistoryDiscoveryServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly TikTokAccountProfile _account;

    public CopyrightProofLocalHistoryDiscoveryServiceTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "copyright-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _account = new TikTokAccountProfile
        {
            Id = "account-1",
            Name = "测试账号",
            LastWorkspace = _tempRoot,
            TiktokUploadProfilePath = _tempRoot,
            TiktokExcelReportPath = Path.Combine(_tempRoot, "history.xlsx"),
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Ignore cleanup failures caused by delayed test file handles.
        }
    }

    [Fact]
    public void Discover_finds_exact_mapping_in_local_backup()
    {
        WriteBackupRecords(
            new
            {
                original_title = "怪谈玩家，但画风不对",
                new_title = "诡异游戏里我反成大反派",
                episode_count = 50,
            });

        var snapshot = CopyrightProofLocalHistoryDiscoveryService
            .Discover(
                _tempRoot,
                _account,
                mainDatabasePath: Path.Combine(_tempRoot, "missing.db"))
            .Should()
            .ContainSingle()
            .Subject;

        snapshot.Item.OriginalTitle.Should().Be("怪谈玩家，但画风不对");
        snapshot.Item.NewTitle.Should().Be("诡异游戏里我反成大反派");
        snapshot.Item.EpisodeCount.Should().Be(50);
        snapshot.Item.AccountProfileId.Should().Be("account-1");
    }

    [Fact]
    public void Discover_reads_original_title_from_exported_excel()
    {
        TikTokExcelExportService.Export(
            _tempRoot,
            [
                new QueueProjectItem
                {
                    ProjectDir = Path.Combine(_tempRoot, "原剧甲"),
                    DisplayName = "原剧甲",
                    OriginalTitle = "原剧甲",
                    NewTitle = "新剧甲",
                    EpisodeCount = 36,
                    AccountProfileId = _account.Id,
                    AccountProfileName = _account.DisplayName,
                },
            ],
            _account);

        var snapshot = CopyrightProofLocalHistoryDiscoveryService
            .Discover(
                _tempRoot,
                _account,
                mainDatabasePath: Path.Combine(_tempRoot, "missing.db"))
            .Should()
            .ContainSingle()
            .Subject;

        snapshot.Item.OriginalTitle.Should().Be("原剧甲");
        snapshot.Item.NewTitle.Should().Be("新剧甲");
        snapshot.Item.EpisodeCount.Should().Be(36);
    }

    [Fact]
    public void Conflicting_original_titles_are_not_automatically_executable()
    {
        WriteBackupRecords(
            new
            {
                original_title = "原剧甲",
                new_title = "相同新剧名",
                episode_count = 20,
            },
            new
            {
                original_title = "原剧乙",
                new_title = "相同新剧名",
                episode_count = 30,
            });

        var history = CopyrightProofLocalHistoryDiscoveryService.Discover(
            _tempRoot,
            _account,
            mainDatabasePath: Path.Combine(_tempRoot, "missing.db"));
        history.Should().HaveCount(2);

        var match = CopyrightProofProjectMatcher.MatchByNewTitleExact(
                ["相同新剧名"],
                [],
                [],
                history)
            .Should()
            .ContainSingle()
            .Subject;
        match.Location.Should().Be(CopyrightProofProjectLocation.Conflict);
        match.CanExecute.Should().BeFalse();
        match.ConflictCandidates.Should().BeEquivalentTo("原剧甲", "原剧乙");
    }

    private void WriteBackupRecords(params object[] records)
    {
        var dir = Path.Combine(_tempRoot, "_codex-backups", "sample", "database");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "archive-record.json"),
            JsonSerializer.Serialize(records));
    }
}
