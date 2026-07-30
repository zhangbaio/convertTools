using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

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
        const string queuedAt = "2026-06-28T14:42:13.4600000+08:00";
        const string uploadCompletedAt = "2026-07-08T12:34:56.0000000+08:00";
        WorkspaceQueueDatabase.Save(
            _workspaceRoot,
            new[]
            {
                new QueueProjectItem
                {
                    ProjectDir = sourceDir,
                    DisplayName = "demo",
                    QueuedAt = queuedAt,
                    UploadCompletedAt = uploadCompletedAt,
                    StepStates = new Dictionary<string, string>
                    {
                        [QueueStepKeys.UploadSeries] = QueueStepStatus.Completed,
                    },
                },
            });
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
        root.GetProperty("queuedAt").GetString().Should().Be(queuedAt);
        root.GetProperty("queued_at").GetString().Should().Be(queuedAt);
        root.GetProperty("accountProfileId").GetString().Should().Be("acct-1dfecd83");
        root.GetProperty("accountProfileName").GetString().Should().Be("账号3");
        root.GetProperty("uploadCompletedAt").GetString().Should().Be(uploadCompletedAt);
        root.GetProperty("upload_completed_at").GetString().Should().Be(uploadCompletedAt);
        root.GetProperty("queueProjectState")
            .GetProperty("step_states")
            .GetProperty(QueueStepKeys.UploadSeries)
            .GetString()
            .Should().Be(QueueStepStatus.Completed);

        var archivedItem = TikTokArchivedProjectService.List(_workspaceRoot, _archiveRoot).Single();
        archivedItem.QueuedAt.Should().Be(queuedAt);
        archivedItem.UploadCompletedAt.Should().Be(uploadCompletedAt);
        var syncItem = TikTokArchivedProjectService.ToQueueItemForSync(archivedItem);
        syncItem.QueuedAt.Should().Be(queuedAt);
        syncItem.UploadCompletedAt.Should().Be(uploadCompletedAt);
        syncItem.AccountProfileId.Should().Be("acct-1dfecd83");
        syncItem.AccountProfileName.Should().Be("账号3");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ArchiveQueueProjectAsync_cleans_retained_proof_hydration_video_only_when_configured(
        bool deleteSourceVideos,
        bool expectedVideoExists)
    {
        var (sourceDir, _) = CreateProjectDirs($"proof-hydration-{deleteSourceVideos}");
        var videoName = "证明材料补源-第01集.mp4";
        WriteSmallFile(Path.Combine(sourceDir, videoName));

        await TikTokArchivedProjectService.ArchiveQueueProjectAsync(
            _workspaceRoot,
            sourceDir,
            _archiveRoot,
            deleteSourceVideos: deleteSourceVideos,
            deleteWorkflowVideos: deleteSourceVideos,
            deleteMaterialVideos: deleteSourceVideos);

        var metadataPath = Directory
            .EnumerateFiles(Path.Combine(_archiveRoot, "meta"), "*.json")
            .Single();
        using var doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
        var archivedSourceDir = doc.RootElement.GetProperty("archivedSourceDir").GetString()!;

        File.Exists(Path.Combine(archivedSourceDir, videoName))
            .Should().Be(expectedVideoExists);
    }

    [Fact]
    public async Task TodayUploadCount_includes_completed_archived_projects()
    {
        var (sourceDir, _) = CreateProjectDirs("today-completed");
        const string uploadCompletedAt = "2026-07-08T09:15:00.0000000+08:00";
        WorkspaceQueueDatabase.Save(
            _workspaceRoot,
            new[]
            {
                new QueueProjectItem
                {
                    ProjectDir = sourceDir,
                    DisplayName = "today-completed",
                    AccountProfileId = "acct-today",
                    AccountProfileName = "账号今日",
                    UploadCompletedAt = uploadCompletedAt,
                    StepStates = new Dictionary<string, string>
                    {
                        [QueueStepKeys.UploadSeries] = QueueStepStatus.Completed,
                    },
                },
            });
        var account = new TikTokAccountProfile
        {
            Id = "acct-today",
            Name = "账号今日",
        };

        await TikTokArchivedProjectService.ArchiveQueueProjectAsync(
            _workspaceRoot,
            sourceDir,
            _archiveRoot,
            account: account);

        var count = TikTokTodayUploadCountService.CountTodayUploads(
            Array.Empty<QueueProjectItem>(),
            "acct-today",
            _workspaceRoot,
            new DateTimeOffset(2026, 7, 8, 18, 0, 0, TimeSpan.FromHours(8)),
            includeExecutionHistory: false);

        count.Should().Be(1);
    }

    [Fact]
    public void List_backfills_missing_queued_time_from_queue_state()
    {
        var (sourceDir, workflowDir) = CreateProjectDirs("legacy-queued");
        const string queuedAt = "2026-07-04T14:08:53.7400000+08:00";
        WorkspaceQueueDatabase.Save(
            _workspaceRoot,
            new[]
            {
                new QueueProjectItem
                {
                    ProjectDir = sourceDir,
                    DisplayName = "legacy-queued",
                    QueuedAt = queuedAt,
                    Archived = true,
                },
            });

        var archivedSource = Path.Combine(_archiveRoot, "source", "legacy-queued");
        var archivedWorkflow = Path.Combine(_archiveRoot, "workflow", "_legacy-queued");
        Directory.CreateDirectory(archivedSource);
        Directory.CreateDirectory(archivedWorkflow);
        var metadataPath = Path.Combine(_archiveRoot, "meta", "legacy-queued.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["projectKey"] = "legacy-queued",
                ["displayName"] = "legacy-queued",
                ["originalTitle"] = "legacy-queued",
                ["newTitle"] = "legacy-queued",
                ["archiveSource"] = "tiktok",
                ["archivedAt"] = "2026-07-04T17:25:44",
                ["sourceProjectDir"] = sourceDir,
                ["workflowProjectDir"] = workflowDir,
                ["archivedSourceDir"] = archivedSource,
                ["archivedWorkflowDir"] = archivedWorkflow,
            }));

        var archivedItem = TikTokArchivedProjectService.List(_workspaceRoot, _archiveRoot).Single();

        archivedItem.QueuedAt.Should().Be(queuedAt);
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
        restored.ProofMaterialStatementDate.Should().Be("2026-01-01");
    }

    [Fact]
    public async Task Restore_recovers_completed_step_states_when_queue_row_was_removed()
    {
        var (sourceDir, _) = CreateProjectDirs("restore-state");
        var original = new QueueProjectItem
        {
            ProjectDir = sourceDir,
            DisplayName = "restore-state",
            OriginalTitle = "原剧名",
            NewTitle = "新剧名",
            Enabled = true,
            StepStates = new Dictionary<string, string>
            {
                [QueueStepKeys.Download] = QueueStepStatus.Completed,
                [QueueStepKeys.RewriteInfo] = QueueStepStatus.Completed,
                [QueueStepKeys.GeneratePoster] = QueueStepStatus.Completed,
            },
        };
        WorkspaceQueueDatabase.Save(_workspaceRoot, new[] { original });

        await TikTokArchivedProjectService.ArchiveQueueProjectAsync(
            _workspaceRoot,
            sourceDir,
            _archiveRoot,
            deleteSourceVideos: false,
            deleteWorkflowVideos: false,
            deleteMaterialVideos: false);
        var metadataPath = Directory.EnumerateFiles(Path.Combine(_archiveRoot, "meta"), "*.json").Single();
        WorkspaceQueueDatabase.Save(_workspaceRoot, Array.Empty<QueueProjectItem>());

        TikTokArchivedProjectService.Restore(_workspaceRoot, metadataPath, _archiveRoot);

        var restored = WorkspaceQueueDatabase.Load(_workspaceRoot).Items.Single();
        restored.Enabled.Should().BeFalse();
        restored.Archived.Should().BeFalse();
        restored.OriginalTitle.Should().Be("原剧名");
        restored.NewTitle.Should().Be("新剧名");
        restored.StepStates[QueueStepKeys.Download].Should().Be(QueueStepStatus.Completed);
        restored.StepStates[QueueStepKeys.RewriteInfo].Should().Be(QueueStepStatus.Completed);
        restored.StepStates[QueueStepKeys.GeneratePoster].Should().Be(QueueStepStatus.Completed);
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

    [Fact]
    public async Task Archive_after_restore_preserves_queue_titles_when_project_info_is_missing()
    {
        var (sourceDir, _) = CreateProjectDirs("project-folder-name");
        WorkspaceQueueDatabase.Save(
            _workspaceRoot,
            new[]
            {
                new QueueProjectItem
                {
                    ProjectDir = sourceDir,
                    DisplayName = "project-folder-name",
                    OriginalTitle = "哑妃传",
                    NewTitle = "深宫哑女步步为后",
                },
            });

        await TikTokArchivedProjectService.ArchiveQueueProjectAsync(
            _workspaceRoot,
            sourceDir,
            _archiveRoot);
        var firstArchive = TikTokArchivedProjectService.List(_workspaceRoot, _archiveRoot).Single();
        TikTokArchivedProjectService.Restore(_workspaceRoot, firstArchive.MetadataPath, _archiveRoot);

        // Proof-material regeneration does not guarantee that 短剧信息.txt still exists.
        await TikTokArchivedProjectService.ArchiveQueueProjectAsync(
            _workspaceRoot,
            sourceDir,
            _archiveRoot);

        var secondArchive = TikTokArchivedProjectService.List(_workspaceRoot, _archiveRoot).Single();
        secondArchive.OriginalTitle.Should().Be("哑妃传");
        secondArchive.NewTitle.Should().Be("深宫哑女步步为后");
    }

    [Fact]
    public void Restore_resumes_when_source_was_already_restored()
    {
        var restoredSource = Path.Combine(_workspaceRoot, "partial-restore");
        var restoredWorkflow = Path.Combine(_workspaceRoot, "workflow", "_partial-restore");
        var archivedSource = Path.Combine(_archiveRoot, "source", "partial-restore");
        var archivedWorkflow = Path.Combine(_archiveRoot, "workflow", "_partial-restore");
        Directory.CreateDirectory(restoredSource);
        Directory.CreateDirectory(archivedWorkflow);
        WriteProjectMetadata(restoredSource, restoredSource, restoredWorkflow, "partial-restore");
        WriteProjectMetadata(archivedWorkflow, restoredSource, restoredWorkflow, "partial-restore");
        var metadataPath = WriteArchiveMetadata(
            "partial-restore",
            restoredSource,
            restoredWorkflow,
            archivedSource,
            archivedWorkflow);

        TikTokArchivedProjectService.Restore(_workspaceRoot, metadataPath, _archiveRoot);

        Directory.Exists(restoredSource).Should().BeTrue();
        Directory.Exists(restoredWorkflow).Should().BeTrue();
        Directory.Exists(archivedWorkflow).Should().BeFalse();
        File.Exists(metadataPath).Should().BeFalse();
    }

    [Fact]
    public void Restore_preflights_all_targets_before_moving_any_directory()
    {
        var restoredSource = Path.Combine(_workspaceRoot, "conflicting-restore");
        var restoredWorkflow = Path.Combine(_workspaceRoot, "workflow", "_conflicting-restore");
        var archivedSource = Path.Combine(_archiveRoot, "source", "conflicting-restore");
        var archivedWorkflow = Path.Combine(_archiveRoot, "workflow", "_conflicting-restore");
        Directory.CreateDirectory(archivedSource);
        Directory.CreateDirectory(archivedWorkflow);
        Directory.CreateDirectory(restoredWorkflow);
        var metadataPath = WriteArchiveMetadata(
            "conflicting-restore",
            restoredSource,
            restoredWorkflow,
            archivedSource,
            archivedWorkflow);

        var action = () => TikTokArchivedProjectService.Restore(_workspaceRoot, metadataPath, _archiveRoot);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*未移动任何目录*");
        Directory.Exists(archivedSource).Should().BeTrue();
        Directory.Exists(archivedWorkflow).Should().BeTrue();
        Directory.Exists(restoredSource).Should().BeFalse();
        File.Exists(metadataPath).Should().BeTrue();
    }

    [Fact]
    public void Restore_keeps_metadata_when_an_expected_archive_directory_is_missing()
    {
        var restoredSource = Path.Combine(_workspaceRoot, "missing-source");
        var restoredWorkflow = Path.Combine(_workspaceRoot, "workflow", "_missing-source");
        var archivedSource = Path.Combine(_archiveRoot, "source", "missing-source");
        var archivedWorkflow = Path.Combine(_archiveRoot, "workflow", "_missing-source");
        Directory.CreateDirectory(archivedWorkflow);
        var metadataPath = WriteArchiveMetadata(
            "missing-source",
            restoredSource,
            restoredWorkflow,
            archivedSource,
            archivedWorkflow);

        var action = () => TikTokArchivedProjectService.Restore(_workspaceRoot, metadataPath, _archiveRoot);

        action.Should().Throw<DirectoryNotFoundException>()
            .WithMessage("*已保留归档记录*");
        Directory.Exists(archivedWorkflow).Should().BeTrue();
        Directory.Exists(restoredWorkflow).Should().BeFalse();
        File.Exists(metadataPath).Should().BeTrue();
        TikTokArchivedProjectService.List(_workspaceRoot, _archiveRoot)
            .Should().ContainSingle(item => item.ProjectKey == "missing-source");
    }

    [Fact]
    public void Restore_rebases_stale_absolute_archive_paths_to_the_current_archive_root()
    {
        var restoredSource = Path.Combine(_workspaceRoot, "rebased-source");
        var restoredWorkflow = Path.Combine(_workspaceRoot, "workflow", "_rebased-workflow");
        var archivedSource = Path.Combine(_archiveRoot, "source", "rebased-source");
        var archivedWorkflow = Path.Combine(_archiveRoot, "workflow", "_rebased-workflow");
        Directory.CreateDirectory(archivedSource);
        Directory.CreateDirectory(archivedWorkflow);
        var metadataPath = WriteArchiveMetadata(
            "rebased-source",
            @"D:\old-workspace\rebased-source",
            @"D:\old-workspace\workflow\_rebased-workflow",
            @"D:\old-workspace\archive\source\rebased-source",
            @"D:\old-workspace\archive\workflow\_rebased-workflow",
            newTitle: "rebased-workflow");

        TikTokArchivedProjectService.Restore(_workspaceRoot, metadataPath, _archiveRoot);

        Directory.Exists(restoredSource).Should().BeTrue();
        Directory.Exists(restoredWorkflow).Should().BeTrue();
        File.Exists(metadataPath).Should().BeFalse();
    }

    [Fact]
    public async Task List_keeps_database_only_failed_records_alongside_file_records()
    {
        var first = CreateProjectDirs("database-only").SourceDir;
        var second = CreateProjectDirs("file-backed").SourceDir;
        await TikTokArchivedProjectService.ArchiveQueueProjectAsync(
            _workspaceRoot,
            first,
            _archiveRoot,
            deleteSourceVideos: false,
            deleteWorkflowVideos: false,
            deleteMaterialVideos: false);
        await TikTokArchivedProjectService.ArchiveQueueProjectAsync(
            _workspaceRoot,
            second,
            _archiveRoot,
            deleteSourceVideos: false,
            deleteWorkflowVideos: false,
            deleteMaterialVideos: false);
        var firstMetadata = Path.Combine(_archiveRoot, "meta", "database-only.json");
        File.Exists(firstMetadata).Should().BeTrue();
        File.Delete(firstMetadata);

        var items = TikTokArchivedProjectService.List(_workspaceRoot, _archiveRoot);

        items.Select(item => item.ProjectKey)
            .Should().BeEquivalentTo("database-only", "file-backed");
        var databaseOnly = items.Single(item => item.ProjectKey == "database-only");
        databaseOnly.MetadataPath.Should().Be(firstMetadata);

        TikTokArchivedProjectService.Restore(_workspaceRoot, databaseOnly.ArchiveProjectDir, _archiveRoot);

        Directory.Exists(Path.Combine(_workspaceRoot, "database-only")).Should().BeTrue();
        File.Exists(firstMetadata).Should().BeFalse();
        TikTokArchivedProjectService.List(_workspaceRoot, _archiveRoot)
            .Should().ContainSingle(item => item.ProjectKey == "file-backed");
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

    private string WriteArchiveMetadata(
        string projectKey,
        string sourceProjectDir,
        string workflowProjectDir,
        string archivedSourceDir,
        string archivedWorkflowDir,
        string? newTitle = null)
    {
        var metadataPath = Path.Combine(_archiveRoot, "meta", projectKey + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["projectKey"] = projectKey,
                ["displayName"] = projectKey,
                ["originalTitle"] = projectKey,
                ["newTitle"] = newTitle ?? projectKey,
                ["archiveSource"] = "tiktok",
                ["archivedAt"] = "2026-07-03T19:26:58",
                ["sourceProjectDir"] = sourceProjectDir,
                ["workflowProjectDir"] = workflowProjectDir,
                ["archivedSourceDir"] = archivedSourceDir,
                ["archivedWorkflowDir"] = archivedWorkflowDir,
            }));
        return metadataPath;
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
