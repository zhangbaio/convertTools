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
    public void BuildMatches_deduplicates_identical_rows_and_skips_incomplete_rows()
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

        matches.Should().ContainSingle();
        matches[0].NewTitle.Should().Be("新剧");
    }
}
