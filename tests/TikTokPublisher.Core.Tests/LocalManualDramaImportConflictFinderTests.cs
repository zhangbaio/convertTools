using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class LocalManualDramaImportConflictFinderTests
{
    [Fact]
    public void Find_MatchesQueueArchiveAndManagementByTitle()
    {
        var selected = new[]
        {
            Preview(@"E:\tiktok2\贪财俏妻与隐退战神王爷", "贪财俏妻与隐退战神王爷"),
            Preview(@"E:\tiktok2\已归档剧", "已归档剧"),
            Preview(@"E:\tiktok2\管理系统已有", "管理系统已有"),
            Preview(@"E:\tiktok2\全新剧", "全新剧"),
        };
        var queue = new[]
        {
            new QueueProjectItem
            {
                ProjectDir = @"D:\other\贪财俏妻与隐退战神王爷",
                OriginalTitle = "贪财俏妻与隐退战神王爷",
            },
        };
        var archived = new[]
        {
            Archive("已归档剧"),
        };
        var management = new HashSet<string>(StringComparer.Ordinal) { "管理系统已有" };

        var conflicts = LocalManualDramaImportConflictFinder.Find(selected, queue, archived, management);

        Assert.Equal(
            [
                LocalManualDramaImportConflictFinder.LocalQueueReason,
                LocalManualDramaImportConflictFinder.ArchiveReason,
                LocalManualDramaImportConflictFinder.ManagementReason,
            ],
            conflicts.Select(conflict => conflict.Reasons[0]));
        Assert.Equal("全新剧", selected[3].DisplayName);
        Assert.DoesNotContain(conflicts, conflict => conflict.DisplayName == "全新剧");
    }

    [Fact]
    public void Find_TreatsExistingMetadataAsLocalImportWhenQueueMisses()
    {
        var selected = new[]
        {
            Preview(@"E:\tiktok2\本地项目", "本地项目", metadataExists: true),
        };

        var conflicts = LocalManualDramaImportConflictFinder.Find(
            selected,
            Array.Empty<QueueProjectItem>(),
            Array.Empty<ArchivedProjectItem>());

        Assert.Equal([LocalManualDramaImportConflictFinder.LocalImportedReason], conflicts[0].Reasons);
    }

    [Fact]
    public void Find_CombinesQueueAndArchiveReasons()
    {
        var selected = new[] { Preview(@"E:\tiktok2\同一部", "同一部") };
        var queue = new[]
        {
            new QueueProjectItem { ProjectDir = @"E:\tiktok2\同一部", DisplayName = "同一部" },
        };

        var conflicts = LocalManualDramaImportConflictFinder.Find(selected, queue, [Archive("同一部")]);

        Assert.Equal(
            [
                LocalManualDramaImportConflictFinder.LocalQueueReason,
                LocalManualDramaImportConflictFinder.ArchiveReason,
            ],
            conflicts[0].Reasons);
    }

    private static LocalManualDramaImportPreview Preview(string dir, string name, bool metadataExists = false) =>
        new(dir, name, 10, null, null, metadataExists);

    private static ArchivedProjectItem Archive(string title) =>
        new(
            ProjectKey: title,
            DisplayName: title,
            OriginalTitle: title,
            NewTitle: title,
            ArchivedAt: "",
            QueuedAt: "",
            MetadataPath: "",
            ArchiveProjectDir: "",
            ArchiveSource: "tiktok",
            ArchivedSourceDir: "",
            ArchivedWorkflowDir: "");
}
