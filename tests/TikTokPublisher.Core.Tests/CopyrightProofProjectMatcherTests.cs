using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class CopyrightProofProjectMatcherTests
{
    [Fact]
    public void ParseNewTitles_TrimsAndDeduplicates()
    {
        var result = CopyrightProofProjectMatcher.ParseNewTitles(" 新剧甲 \r\n新剧乙\n新剧甲\n");

        Assert.Equal(["新剧甲", "新剧乙"], result);
    }

    [Fact]
    public void MatchByNewTitleExact_PrefersCurrentQueue()
    {
        var queue = new[]
        {
            new QueueProjectItem { NewTitle = "相同新剧名", OriginalTitle = "原名甲", ProjectDir = @"D:\queue\a" },
        };
        var archive = new[]
        {
            Archived("相同新剧名", @"D:\archive\a"),
        };

        var match = Assert.Single(CopyrightProofProjectMatcher.MatchByNewTitleExact(
            ["相同新剧名"], queue, archive));

        Assert.Equal(CopyrightProofProjectLocation.CurrentQueue, match.Location);
        Assert.Same(queue[0], match.QueueProject);
    }

    [Fact]
    public void MatchByNewTitleExact_DoesNotMatchOriginalTitle()
    {
        var queue = new[]
        {
            new QueueProjectItem { NewTitle = "真正的新剧名", OriginalTitle = "输入的原剧名", ProjectDir = @"D:\queue\a" },
        };

        var match = Assert.Single(CopyrightProofProjectMatcher.MatchByNewTitleExact(
            ["输入的原剧名"], queue, []));

        Assert.Equal(CopyrightProofProjectLocation.Missing, match.Location);
    }

    [Fact]
    public void MatchByNewTitleExact_UsesArchiveWhenQueueDoesNotContainTitle()
    {
        var archived = Archived("归档新剧名", @"D:\archive\a");

        var match = Assert.Single(CopyrightProofProjectMatcher.MatchByNewTitleExact(
            ["归档新剧名"], [], [archived]));

        Assert.Equal(CopyrightProofProjectLocation.Archived, match.Location);
        Assert.Same(archived, match.ArchivedProject);
    }

    [Fact]
    public void MatchByNewTitleExact_ReportsConflictForDuplicateNewTitles()
    {
        var queue = new[]
        {
            new QueueProjectItem { NewTitle = "重复新剧名", ProjectDir = @"D:\queue\a" },
            new QueueProjectItem { NewTitle = "重复新剧名", ProjectDir = @"D:\queue\b" },
        };

        var match = Assert.Single(CopyrightProofProjectMatcher.MatchByNewTitleExact(
            ["重复新剧名"], queue, []));

        Assert.Equal(CopyrightProofProjectLocation.Conflict, match.Location);
        Assert.Equal(2, match.ConflictCandidates?.Count);
    }

    private static ArchivedProjectItem Archived(string newTitle, string archiveDir) =>
        new(
            ProjectKey: Path.GetFileName(archiveDir),
            DisplayName: newTitle,
            OriginalTitle: "原剧名",
            NewTitle: newTitle,
            ArchivedAt: "",
            QueuedAt: "",
            MetadataPath: Path.Combine(archiveDir, "archive.json"),
            ArchiveProjectDir: archiveDir,
            ArchiveSource: "tiktok",
            ArchivedSourceDir: Path.Combine(archiveDir, "source"),
            ArchivedWorkflowDir: Path.Combine(archiveDir, "workflow"));
}
