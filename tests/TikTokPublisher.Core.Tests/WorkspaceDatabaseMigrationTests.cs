using System.Text.Json;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

[CollectionDefinition("Workspace database migration", DisableParallelization = true)]
public sealed class WorkspaceDatabaseMigrationCollection;

[Collection("Workspace database migration")]
public sealed class WorkspaceDatabaseMigrationTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("tiktok-db-test-").FullName;
    private readonly string? _previousDataRoot = AppPaths.DataRootOverride;

    public WorkspaceDatabaseMigrationTests() => AppPaths.DataRootOverride = Path.Combine(_temp, "app-data");

    [Fact]
    public async Task SharedDatabaseKeepsQueueOptionsDeletionsArchivesAndDraftsIsolated()
    {
        var a = CreateWorkspace("first", 2);
        var b = CreateWorkspace("second", 3);
        var legacy = CreateWorkspace("legacy", 5);
        const string editUrl = "https://drama.tiktok.com/series/draft/1234567890123456789";
        TikTokUploadStateStore.MarkUploadStepStarted(a.Workflow);
        TikTokUploadStateStore.RecordPlatformSeriesFound(a.Workflow, editUrl, "fixture", "test");

        var beforeA = Migrate(a.Root);
        var beforeB = Migrate(b.Root);
        Assert.NotEqual(beforeA.WorkspaceId, beforeB.WorkspaceId);
        Assert.False(WorkspaceDatabaseLocation.Resolve(legacy.Root).IsInMainDatabase);
        Assert.Equal(5, WorkspaceQueueService.LoadRunOptions(legacy.Root).ProjectConcurrency);
        Assert.Equal(2, WorkspaceQueueService.LoadRunOptions(a.Root + Path.DirectorySeparatorChar).ProjectConcurrency);
        Assert.Equal(editUrl, TikTokUploadStateStore.LoadCachedEditDetailUrl(a.Workflow));
        Assert.True(TikTokUploadStateStore.ShouldSearchPlatformForExistingDraft(a.Workflow));
        Assert.False(TikTokUploadStateStore.ShouldSearchPlatformForExistingDraft(b.Workflow));

        File.WriteAllText(TikTokUploadStateStore.StateFilePath(a.Workflow), "{}");
        TikTokUploadStateStore.MarkUploadStepFailed(a.Workflow, "retry");
        Assert.Equal(editUrl, TikTokUploadStateStore.LoadCachedEditDetailUrl(a.Workflow));
        Assert.Equal("{}", File.ReadAllText(TikTokUploadStateStore.StateFilePath(a.Workflow)));

        await TikTokArchivedProjectService.ArchiveQueueProjectAsync(a.Root, a.Project,
            deleteSourceVideos: false, deleteWorkflowVideos: false);
        await TikTokArchivedProjectService.ArchiveQueueProjectAsync(b.Root, b.Project,
            deleteSourceVideos: false, deleteWorkflowVideos: false);
        var archiveA = Assert.Single(TikTokArchivedProjectService.ListStored(a.Root));
        var archiveB = Assert.Single(TikTokArchivedProjectService.ListStored(b.Root));
        Assert.NotEqual(archiveA.MetadataPath, archiveB.MetadataPath);
        TikTokArchivedProjectService.Restore(b.Root, archiveB.MetadataPath);
        Assert.Empty(TikTokArchivedProjectService.ListStored(b.Root));
        Assert.Single(TikTokArchivedProjectService.ListStored(a.Root));

        var export = Path.Combine(_temp, "second-export.db");
        WorkspaceDatabaseMigration.BackupWorkspace(b.Root, export);
        WorkspaceQueueDatabase.SaveDeletedProjectDirs(b.Root, [b.Project]);
        WorkspaceQueueDatabase.DeleteProjects(b.Root, [b.Project]);
        WorkspaceQueueDatabase.Save(b.Root, [], new() { ["project_concurrency"] = 9 });
        Assert.Empty(WorkspaceQueueDatabase.Load(b.Root).Items);
        Assert.Contains(b.Project, WorkspaceQueueDatabase.LoadDeletedProjectDirs(b.Root));
        Assert.Empty(WorkspaceQueueDatabase.LoadDeletedProjectDirs(a.Root));
        Assert.Empty(WorkspaceQueueDatabase.LoadDeletedProjectDirs(legacy.Root));
        Assert.Equal(2, WorkspaceQueueService.LoadRunOptions(a.Root).ProjectConcurrency);
        Assert.Single(TikTokArchivedProjectService.ListStored(a.Root));

        WorkspaceDatabaseMigration.RestoreWorkspace(b.Root, WorkspaceDatabaseLocation.Resolve(b.Root), export);
        Assert.Equal(3, WorkspaceQueueService.LoadRunOptions(b.Root).ProjectConcurrency);
        Assert.Equal(b.Project, Assert.Single(WorkspaceQueueDatabase.Load(b.Root).Items).ProjectDir);
        Assert.Empty(WorkspaceQueueDatabase.LoadDeletedProjectDirs(b.Root));
        Assert.Single(TikTokArchivedProjectService.ListStored(a.Root));
        Assert.True(Migrate(a.Root).AlreadyMigrated);
        Assert.Single(TikTokArchivedProjectService.ListStored(a.Root));
        Assert.Equal(5, WorkspaceQueueService.LoadRunOptions(legacy.Root).ProjectConcurrency);
        TikTokArchivedProjectService.Restore(a.Root, archiveA.MetadataPath);
        Assert.Equal(editUrl, TikTokUploadStateStore.LoadCachedEditDetailUrl(a.Workflow));
    }

    [Fact]
    public void WrongWorkspaceAndCancelledImportDoNotChangeRuntimeRouting()
    {
        var a = CreateWorkspace("first", 2);
        var b = CreateWorkspace("second", 3);
        var snapshot = Path.Combine(_temp, "first-backup.db");
        WorkspaceDatabaseMigration.BackupDatabase(WorkspaceQueuePaths.QueueDatabasePath(a.Root), snapshot);
        Assert.Throws<InvalidDataException>(() =>
            WorkspaceDatabaseMigration.MigrateBackup(b.Root, snapshot, AppPaths.AppDatabaseFile));
        Assert.Throws<OperationCanceledException>(() =>
            WorkspaceDatabaseMigration.MigrateBackup(a.Root, snapshot, AppPaths.AppDatabaseFile, new CancellationToken(true)));
        Assert.False(WorkspaceDatabaseLocation.Resolve(a.Root).IsInMainDatabase);
        Assert.False(WorkspaceDatabaseLocation.Resolve(b.Root).IsInMainDatabase);
        Assert.Single(WorkspaceQueueDatabase.Load(a.Root).Items);
        Assert.Single(WorkspaceQueueDatabase.Load(b.Root).Items);
        Assert.Equal(1, Migrate(a.Root).QueueProjects);
    }

    [Fact]
    public void LegacyJsonQueueAndOptionsCanBeImportedFromABackup()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "json-workspace")).FullName;
        var item = new QueueProjectItem { ProjectDir = Path.Combine(root, "project"), OriginalTitle = "fixture" };
        var backup = Path.Combine(_temp, "queue-backup.json");
        File.WriteAllText(backup, JsonSerializer.Serialize(new
        {
            items = new[] { item.ToPayload() },
            options = new { test = "json" },
        }));

        var result = WorkspaceDatabaseMigration.MigrateBackup(root, backup, AppPaths.AppDatabaseFile);

        Assert.Equal(1, result.QueueProjects);
        Assert.Equal(item.ProjectDir, Assert.Single(WorkspaceQueueDatabase.Load(root).Items).ProjectDir);
        Assert.Equal("json", WorkspaceQueueDatabase.Load(root).Options["test"]);
        Assert.False(File.Exists(WorkspaceQueuePaths.QueueDatabasePath(root)));
    }

    private (string Root, string Project, string Workflow) CreateWorkspace(string name, int parallelism)
    {
        var root = Path.Combine(_temp, name);
        var project = Directory.CreateDirectory(Path.Combine(root, "same-project")).FullName;
        var workflow = Directory.CreateDirectory(Path.Combine(root, "workflow", "same-project")).FullName;
        WorkspaceQueueDatabase.Save(root,
            [new QueueProjectItem { ProjectDir = project, OriginalTitle = "fixture", AccountProfileId = "shared-account" }],
            new() { ["project_concurrency"] = parallelism });
        return (root, project, workflow);
    }

    private WorkspaceMigrationResult Migrate(string workspace)
    {
        var backup = Path.Combine(_temp, $"{Path.GetFileName(workspace)}-backup.db");
        WorkspaceDatabaseMigration.BackupDatabase(WorkspaceQueuePaths.QueueDatabasePath(workspace), backup);
        return WorkspaceDatabaseMigration.MigrateBackup(workspace, backup, AppPaths.AppDatabaseFile);
    }

    public void Dispose()
    {
        AppPaths.DataRootOverride = _previousDataRoot;
        SqliteConnection.ClearAllPools();
        Directory.Delete(_temp, recursive: true);
    }
}
