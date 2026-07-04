using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokArchivedProjectServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workspaceRoot;
    private readonly string _archiveRoot;

    public TikTokArchivedProjectServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "tiktok-archive-" + Guid.NewGuid().ToString("N"));
        _workspaceRoot = Path.Combine(_tempRoot, "workspace");
        _archiveRoot = Path.Combine(_tempRoot, "archive-root");
        Directory.CreateDirectory(_workspaceRoot);
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
            // Best effort cleanup for Windows file locks.
        }
    }

    [Fact]
    public async Task ArchiveQueueProjectAsync_writes_python_compatible_metadata_and_keeps_clip_subtitles()
    {
        var (sourceDir, workflowDir) = CreateProjectDirs("demo");
        WriteSmallFile(Path.Combine(sourceDir, "source.mp4"));
        WriteSmallFile(Path.Combine(workflowDir, "videos", "\u7b2c01\u96c6.mp4"));
        WriteSmallFile(Path.Combine(workflowDir, "material-videos", "raw.mp4"));
        WriteSmallFile(Path.Combine(workflowDir, "material-clip-output", "clip", "cut.mp4"));
        var subtitleVideo = Path.Combine(workflowDir, "material-clip-output", "clip", "subtitles", "caption.mp4");
        WriteSmallFile(subtitleVideo);
        var account = new TikTokAccountProfile
        {
            Id = "acct-1dfecd83",
            Name = "账号3",
            TiktokAccountNickname = "账号3",
            TiktokLoginEmail = "15327086817@163.com",
        };

        await TikTokArchivedProjectService.ArchiveQueueProjectAsync(
            _workspaceRoot,
            sourceDir,
            _archiveRoot,
            preserveWorkflowEpisodes: new[] { 1 },
            account: account);

        var metadataPath = Directory.EnumerateFiles(Path.Combine(_archiveRoot, "meta"), "*.json").Single();
        using var doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
        var root = doc.RootElement;
        var archivedWorkflowDir = root.GetProperty("archivedWorkflowDir").GetString()!;

        File.Exists(Path.Combine(archivedWorkflowDir, "videos", "\u7b2c01\u96c6.mp4")).Should().BeTrue();
        File.Exists(Path.Combine(archivedWorkflowDir, "material-videos", "raw.mp4")).Should().BeFalse();
        File.Exists(Path.Combine(archivedWorkflowDir, "material-clip-output", "clip", "cut.mp4")).Should().BeFalse();
        File.Exists(Path.Combine(archivedWorkflowDir, "material-clip-output", "clip", "subtitles", "caption.mp4")).Should().BeTrue();
        root.GetProperty("archiveSource").GetString().Should().Be("tiktok");
        root.GetProperty("deletedVideoFileCount").GetInt32().Should().Be(3);
        root.GetProperty("preservedVideoFileCount").GetInt32().Should().Be(1);
        root.GetProperty("deletedSourceVideoFileCount").GetInt32().Should().Be(1);
        root.GetProperty("deletedWorkflowVideoFileCount").GetInt32().Should().Be(0);
        root.GetProperty("deletedMaterialVideoFileCount").GetInt32().Should().Be(1);
        root.GetProperty("deletedMaterialClipVideoFileCount").GetInt32().Should().Be(1);
        root.GetProperty("accountProfileId").GetString().Should().Be("acct-1dfecd83");
        root.GetProperty("accountProfileName").GetString().Should().Be("账号3");

        var archivedItem = TikTokArchivedProjectService.List(_workspaceRoot, _archiveRoot).Single();
        var syncItem = TikTokArchivedProjectService.ToQueueItemForSync(archivedItem);
        syncItem.AccountProfileId.Should().Be("acct-1dfecd83");
        syncItem.AccountProfileName.Should().Be("账号3");
    }

    [Fact]
    public void Restore_moves_project_to_queue_tail_and_refreshes_queued_time()
    {
        var active = CreateProjectDirs("active").SourceDir;
        var restoredSource = Path.Combine(_workspaceRoot, "restore-me");
        var restoredWorkflow = Path.Combine(_workspaceRoot, "workflow", "_restore-me");
        var archivedSource = Path.Combine(_archiveRoot, "source", "restore-me");
        var archivedWorkflow = Path.Combine(_archiveRoot, "workflow", "_restore-me");
        Directory.CreateDirectory(archivedSource);
        Directory.CreateDirectory(archivedWorkflow);
        WriteProjectMetadata(archivedSource, restoredSource, restoredWorkflow, "restore-me");
        WriteProjectMetadata(archivedWorkflow, restoredSource, restoredWorkflow, "restore-me");
        var metadataPath = Path.Combine(_archiveRoot, "meta", "restore-me.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["projectKey"] = "restore-me",
                ["displayName"] = "restore-me",
                ["originalTitle"] = "restore-me",
                ["newTitle"] = "restore-me",
                ["archiveSource"] = "tiktok",
                ["archivedAt"] = "2026-01-01T00:00:00",
                ["sourceProjectDir"] = restoredSource,
                ["workflowProjectDir"] = restoredWorkflow,
                ["archivedSourceDir"] = archivedSource,
                ["archivedWorkflowDir"] = archivedWorkflow,
            }));

        WorkspaceQueueDatabase.Save(
            _workspaceRoot,
            new[]
            {
                new QueueProjectItem
                {
                    ProjectDir = restoredSource,
                    DisplayName = "restore-me",
                    QueuedAt = "2026-01-01 00:00:00",
                    Enabled = true,
                    Archived = true,
                },
                new QueueProjectItem
                {
                    ProjectDir = active,
                    DisplayName = "active",
                    QueuedAt = "2026-01-02 00:00:00",
                    Enabled = true,
                },
            });

        TikTokArchivedProjectService.Restore(_workspaceRoot, metadataPath, _archiveRoot);

        Directory.Exists(restoredSource).Should().BeTrue();
        Directory.Exists(restoredWorkflow).Should().BeTrue();
        File.Exists(metadataPath).Should().BeFalse();
        var state = WorkspaceQueueDatabase.Load(_workspaceRoot);
        state.Items.Select(item => Path.GetFileName(item.ProjectDir))
            .Should().Equal("active", "restore-me");
        var restored = state.Items.Last();
        restored.Archived.Should().BeFalse();
        restored.Enabled.Should().BeTrue();
        restored.QueuedAt.Should().NotBe("2026-01-01 00:00:00");
    }

    [Fact]
    public void ScanProjects_clears_archived_flag_when_project_directory_exists()
    {
        var sourceDir = CreateProjectDirs("visible").SourceDir;
        WorkspaceQueueDatabase.Save(
            _workspaceRoot,
            new[]
            {
                new QueueProjectItem
                {
                    ProjectDir = sourceDir,
                    DisplayName = "visible",
                    QueuedAt = "2026-01-01 00:00:00",
                    Archived = true,
                },
            });

        var item = WorkspaceQueueService.ScanProjects(_workspaceRoot).Single();

        item.Archived.Should().BeFalse();
    }

    private (string SourceDir, string WorkflowDir) CreateProjectDirs(string name)
    {
        var sourceDir = Path.Combine(_workspaceRoot, name);
        var workflowDir = Path.Combine(_workspaceRoot, "workflow", "_" + name);
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(workflowDir);
        WriteProjectMetadata(sourceDir, sourceDir, workflowDir, name);
        WriteProjectMetadata(workflowDir, sourceDir, workflowDir, name);
        return (sourceDir, workflowDir);
    }

    private static void WriteProjectMetadata(string dir, string sourceDir, string workflowDir, string title)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "shortdrama-project.json"),
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["projectKey"] = title,
                ["title"] = title,
                ["sourceProjectDir"] = sourceDir,
                ["workflowProjectDir"] = workflowDir,
                ["workflowDirName"] = Path.GetFileName(workflowDir),
            }));
    }

    private static void WriteSmallFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
    }
}
