using FluentAssertions;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class ManualDeletedCopyrightProofServiceTests
{
    private readonly TikTokAccountProfile _account = new()
    {
        Id = "account-1",
        Name = "账号一",
    };

    [Fact]
    public void BuildMatches_creates_recoverable_deleted_history_snapshot()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "manual-proof-workspace");

        var match = ManualDeletedCopyrightProofService.BuildMatches(
                [new ManualDeletedCopyrightProofEntry("已发布新剧", "原始短剧")],
                workspace,
                _account)
            .Should()
            .ContainSingle()
            .Subject;

        match.Location.Should().Be(CopyrightProofProjectLocation.DeletedHistory);
        match.CanExecute.Should().BeTrue();
        match.HistorySnapshot.Should().NotBeNull();
        match.HistorySnapshot!.Workspace.Should().Be(Path.GetFullPath(workspace));
        match.HistorySnapshot.Item.NewTitle.Should().Be("已发布新剧");
        match.HistorySnapshot.Item.OriginalTitle.Should().Be("原始短剧");
        match.HistorySnapshot.Item.AccountProfileId.Should().Be("account-1");
        match.HistorySnapshot.Item.UploadSeriesStatus.Should().Be(QueueStepStatus.Completed);
        match.HistorySnapshot.Item.UploadCompletedAt.Should().BeEmpty(
            "a manually recovered existing series must not count as uploaded today");
    }

    [Fact]
    public void BuildMatches_uses_existing_queue_project_instead_of_rebuilding()
    {
        var queueItem = new QueueProjectItem
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "existing-project"),
            NewTitle = "已发布新剧",
            OriginalTitle = "队列原剧",
        };

        var match = ManualDeletedCopyrightProofService.BuildMatches(
                [new ManualDeletedCopyrightProofEntry("已发布新剧", "手动原剧")],
                Path.GetTempPath(),
                _account,
                [queueItem])
            .Should()
            .ContainSingle()
            .Subject;

        match.Location.Should().Be(CopyrightProofProjectLocation.CurrentQueue);
        match.QueueProject.Should().BeSameAs(queueItem);
        match.HistorySnapshot.Should().BeNull();
    }

    [Fact]
    public void BuildMatches_uses_existing_archive_project_instead_of_rebuilding()
    {
        var archive = new ArchivedProjectItem(
            "key",
            "显示名",
            "归档原剧",
            "已发布新剧",
            "2026-07-30T00:00:00+08:00",
            "",
            Path.Combine(Path.GetTempPath(), "meta.json"),
            Path.Combine(Path.GetTempPath(), "archive-project"),
            "TikTok",
            "",
            "");

        var match = ManualDeletedCopyrightProofService.BuildMatches(
                [new ManualDeletedCopyrightProofEntry("已发布新剧", "手动原剧")],
                Path.GetTempPath(),
                _account,
                archivedProjects: [archive])
            .Should()
            .ContainSingle()
            .Subject;

        match.Location.Should().Be(CopyrightProofProjectLocation.Archived);
        match.ArchivedProject.Should().BeSameAs(archive);
        match.HistorySnapshot.Should().BeNull();
    }

    [Fact]
    public void BuildMatches_marks_conflicting_original_titles_as_not_executable()
    {
        var match = ManualDeletedCopyrightProofService.BuildMatches(
                [
                    new ManualDeletedCopyrightProofEntry("相同新剧", "原剧甲"),
                    new ManualDeletedCopyrightProofEntry("相同新剧", "原剧乙"),
                ],
                Path.GetTempPath(),
                _account)
            .Should()
            .ContainSingle()
            .Subject;

        match.Location.Should().Be(CopyrightProofProjectLocation.Conflict);
        match.CanExecute.Should().BeFalse();
        match.ConflictCandidates.Should().BeEquivalentTo("原剧甲", "原剧乙");
    }

    [Fact]
    public void BuildMatches_deduplicates_rows_allows_missing_original_and_skips_missing_new_title()
    {
        var matches = ManualDeletedCopyrightProofService.BuildMatches(
            [
                new ManualDeletedCopyrightProofEntry("新剧", "原剧"),
                new ManualDeletedCopyrightProofEntry(" 新剧 ", " 原剧 "),
                new ManualDeletedCopyrightProofEntry("缺少原剧", ""),
                new ManualDeletedCopyrightProofEntry("", "缺少新剧"),
            ],
            Path.GetTempPath(),
            _account);

        matches.Should().HaveCount(2);
        matches.Select(match => match.NewTitle)
            .Should()
            .Equal("新剧", "缺少原剧");

        var platformRecovery = matches.Single(match => match.NewTitle == "缺少原剧");
        platformRecovery.CanExecute.Should().BeTrue();
        platformRecovery.HistorySnapshot.Should().NotBeNull();
        platformRecovery.HistorySnapshot!.Item.OriginalTitle.Should().BeEmpty();
        platformRecovery.HistorySnapshot.Item.ProjectDir.Should().EndWith("缺少原剧_版权恢复");
        platformRecovery.HistorySnapshot.Item.Remark.Should().Contain("TikTok");
    }

    [Fact]
    public void ParseUnknownOriginalTitles_parses_multiline_input_and_removes_duplicates()
    {
        var entries = ManualDeletedCopyrightProofService.ParseUnknownOriginalTitles(
            " 新剧甲 \r\n新剧乙\n\n新剧甲\r新剧丙 ");

        entries.Select(entry => entry.NewTitle)
            .Should()
            .Equal("新剧甲", "新剧乙", "新剧丙");
        entries.Should().OnlyContain(entry => entry.OriginalTitle == string.Empty);
    }

    [Fact]
    public void PromoteMissingToPublishedRecovery_promotes_only_missing_titles()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "promote-missing-workspace");
        var queueItem = new QueueProjectItem
        {
            ProjectDir = Path.Combine(workspace, "queue-project"),
            NewTitle = "队列剧",
        };
        var history = new TikTokExecutionProjectSnapshot(
            workspace,
            "2026-07-02T08:00:00+08:00",
            new QueueProjectItem
            {
                NewTitle = "历史剧",
                OriginalTitle = "历史原剧",
                ProjectDir = Path.Combine(workspace, "历史原剧"),
            });
        var matches = new[]
        {
            new CopyrightProofProjectMatch(
                "队列剧",
                CopyrightProofProjectLocation.CurrentQueue,
                QueueProject: queueItem),
            new CopyrightProofProjectMatch(
                "历史剧",
                CopyrightProofProjectLocation.DeletedHistory,
                HistorySnapshot: history),
            new CopyrightProofProjectMatch("缺失剧", CopyrightProofProjectLocation.Missing),
            new CopyrightProofProjectMatch(
                "冲突剧",
                CopyrightProofProjectLocation.Conflict,
                ConflictCandidates: ["a", "b"]),
        };

        var promoted = ManualDeletedCopyrightProofService.PromoteMissingToPublishedRecovery(
            matches,
            workspace,
            _account);

        promoted.Should().HaveCount(4);
        promoted[0].Should().BeSameAs(matches[0]);
        promoted[1].Should().BeSameAs(matches[1]);
        promoted[1].HistorySnapshot.Should().BeSameAs(history);

        var fallback = promoted[2];
        fallback.Location.Should().Be(CopyrightProofProjectLocation.DeletedHistory);
        fallback.CanExecute.Should().BeTrue();
        ManualDeletedCopyrightProofService.IsPublishedRecoveryFallback(fallback).Should().BeTrue();
        fallback.HistorySnapshot!.Item.OriginalTitle.Should().BeEmpty();
        fallback.HistorySnapshot.Item.ProjectDir.Should().EndWith("缺失剧_版权恢复");
        fallback.HistorySnapshot.Item.Remark.Should().Contain("TikTok");

        promoted[3].Location.Should().Be(CopyrightProofProjectLocation.Conflict);
        promoted[3].CanExecute.Should().BeFalse();
    }

    [Fact]
    public void MatchByNewTitleExactOrRecover_prefers_real_history_over_synthetic_fallback()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "or-recover-workspace");
        var history = new TikTokExecutionProjectSnapshot(
            workspace,
            "2026-07-02T08:00:00+08:00",
            new QueueProjectItem
            {
                NewTitle = "已删除的新剧名",
                OriginalTitle = "同一原剧名",
                ProjectDir = Path.Combine(workspace, "同一原剧名"),
            });

        var matches = ManualDeletedCopyrightProofService.MatchByNewTitleExactOrRecover(
            ["已删除的新剧名", "完全找不到的剧"],
            workspace,
            _account,
            [],
            [],
            [history]);

        matches.Should().HaveCount(2);
        matches[0].Location.Should().Be(CopyrightProofProjectLocation.DeletedHistory);
        matches[0].HistorySnapshot.Should().BeSameAs(history);
        ManualDeletedCopyrightProofService.IsPublishedRecoveryFallback(matches[0]).Should().BeFalse();

        matches[1].Location.Should().Be(CopyrightProofProjectLocation.DeletedHistory);
        ManualDeletedCopyrightProofService.IsPublishedRecoveryFallback(matches[1]).Should().BeTrue();
        matches[1].HistorySnapshot!.Item.ProjectDir.Should().EndWith("完全找不到的剧_版权恢复");
    }

    [Fact]
    public void MatchByNewTitleExactOrRecover_keeps_queue_and_archive_precedence()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "or-recover-precedence");
        var queueItem = new QueueProjectItem
        {
            ProjectDir = Path.Combine(workspace, "queue"),
            NewTitle = "相同新剧名",
        };
        var archive = new ArchivedProjectItem(
            "key",
            "显示名",
            "归档原剧",
            "归档新剧名",
            "2026-07-30T00:00:00+08:00",
            "",
            Path.Combine(workspace, "meta.json"),
            Path.Combine(workspace, "archive-project"),
            "TikTok",
            "",
            "");

        var matches = ManualDeletedCopyrightProofService.MatchByNewTitleExactOrRecover(
            ["相同新剧名", "归档新剧名"],
            workspace,
            _account,
            [queueItem],
            [archive]);

        matches.Should().HaveCount(2);
        matches[0].Location.Should().Be(CopyrightProofProjectLocation.CurrentQueue);
        matches[0].QueueProject.Should().BeSameAs(queueItem);
        matches[1].Location.Should().Be(CopyrightProofProjectLocation.Archived);
        matches[1].ArchivedProject.Should().BeSameAs(archive);
    }
}
