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
        Assert.Equal(Path.GetFullPath(@"E:\tiktok2\同一部"), conflicts[0].MatchedQueueProjects[0].ProjectDir);
    }

    [Fact]
    public void Resolve_DeletesOtherUnimportedQueueCopyAndKeepsImportFolder()
    {
        var importDir = @"E:\tiktok2\同一部";
        var otherDir = @"D:\other\同一部";
        var conflicts = new[]
        {
            new LocalManualDramaImportConflictFinder.Conflict(
                importDir,
                "同一部",
                [LocalManualDramaImportConflictFinder.LocalQueueReason],
                [new LocalManualDramaImportConflictFinder.MatchedQueueProject(otherDir, MetadataExists: false, ActiveUpload: false)]),
        };

        var plan = LocalManualDramaImportConflictFinder.ResolveUnimportedLocalDeletion(
            conflicts,
            [Preview(importDir, "同一部")],
            [importDir]);

        Assert.Equal([Path.GetFullPath(otherDir)], plan.Targets.Select(target => target.Directory));
        Assert.Contains(Path.GetFullPath(importDir), plan.ProtectedImportDirs);
        Assert.Empty(plan.BlockedImportDirs);
    }

    [Fact]
    public void Resolve_KeepsSamePathImportFolderAndImportedQueueCopy()
    {
        var importDir = @"E:\tiktok2\同一部";
        var importedCopy = @"D:\other\已导入副本";
        var conflicts = new[]
        {
            new LocalManualDramaImportConflictFinder.Conflict(
                importDir,
                "同一部",
                [LocalManualDramaImportConflictFinder.LocalQueueReason],
                [
                    new LocalManualDramaImportConflictFinder.MatchedQueueProject(importDir, MetadataExists: false, ActiveUpload: false),
                    new LocalManualDramaImportConflictFinder.MatchedQueueProject(importedCopy, MetadataExists: true, ActiveUpload: false),
                ]),
        };

        var plan = LocalManualDramaImportConflictFinder.ResolveUnimportedLocalDeletion(
            conflicts,
            [Preview(importDir, "同一部", metadataExists: true)],
            [importDir]);

        Assert.Empty(plan.Targets);
        Assert.Contains(Path.GetFullPath(importDir), plan.ProtectedImportDirs);
    }

    [Fact]
    public void Resolve_DeletesUnimportedLocalDuplicateAndLeavesArchiveAndManagement()
    {
        var importDir = @"E:\tiktok2\已归档剧";
        var duplicateDir = @"E:\tiktok2\已归档剧-副本";
        var conflicts = new[]
        {
            new LocalManualDramaImportConflictFinder.Conflict(
                importDir,
                "已归档剧",
                [
                    LocalManualDramaImportConflictFinder.ArchiveReason,
                    LocalManualDramaImportConflictFinder.ManagementReason,
                ],
                []),
        };

        var plan = LocalManualDramaImportConflictFinder.ResolveUnimportedLocalDeletion(
            conflicts,
            [
                Preview(importDir, "已归档剧"),
                Preview(duplicateDir, "已归档剧"),
                Preview(@"E:\tiktok2\已导入副本", "已归档剧", metadataExists: true),
            ],
            [importDir]);

        Assert.Equal([Path.GetFullPath(duplicateDir)], plan.Targets.Select(target => target.Directory));
        Assert.DoesNotContain(plan.Targets, target => target.Directory.Contains("archive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_BlocksImportWhenUnimportedQueueCopyIsUploading()
    {
        var importDir = @"E:\tiktok2\上传中";
        var otherDir = @"D:\other\上传中";
        var conflicts = new[]
        {
            new LocalManualDramaImportConflictFinder.Conflict(
                importDir,
                "上传中",
                [LocalManualDramaImportConflictFinder.LocalQueueReason],
                [new LocalManualDramaImportConflictFinder.MatchedQueueProject(otherDir, MetadataExists: false, ActiveUpload: true)]),
        };

        var plan = LocalManualDramaImportConflictFinder.ResolveUnimportedLocalDeletion(
            conflicts,
            [Preview(importDir, "上传中")],
            [importDir]);

        Assert.Empty(plan.Targets);
        Assert.Equal([Path.GetFullPath(importDir)], plan.BlockedImportDirs);
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
