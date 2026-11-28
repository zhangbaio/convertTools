using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueProjectDeletionUseCaseTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory();
    private string Root => Path.Combine(_temp.FullName, "workspace");
    private string History => Path.Combine(_temp.FullName, "history.db");

    [Fact]
    public async Task Removes_source_and_workflow_without_losing_other_rows_or_resurrecting_a_stale_snapshot()
    {
        var deleted = CreateProject("remove");
        var kept = CreateProject("keep");
        WorkspaceQueueDatabase.Save(Root, [deleted.Item, kept.Item]);

        var result = await ExecuteAsync([deleted.Item, kept.Item], deleted.Item.ProjectDir);
        result.Errors.Should().BeEmpty();
        result.DeletedDirectoryCount.Should().Be(2);
        result.RemovedProjects.Should().Equal(deleted.Item.ProjectDir);
        Directory.Exists(deleted.Item.ProjectDir).Should().BeFalse();
        Directory.Exists(deleted.Workflow).Should().BeFalse();
        Directory.Exists(kept.Item.ProjectDir).Should().BeTrue();

        WorkspaceQueueDatabase.Save(Root, [deleted.Item, kept.Item]);
        WorkspaceQueueDatabase.Load(Root).Items.Select(item => item.ProjectDir).Should().Equal(kept.Item.ProjectDir);

        // A completed marker suppresses old queue writes; a scan cannot erase recreated contents.
        Directory.CreateDirectory(deleted.Item.ProjectDir);
        var newlyCreated = Path.Combine(deleted.Item.ProjectDir, "new.txt");
        File.WriteAllText(newlyCreated, "new content");
        WorkspaceQueueService.ScanProjects(Root).Select(item => item.ProjectDir).Should().Equal(kept.Item.ProjectDir);
        File.Exists(newlyCreated).Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_after_staging_the_first_directory_restores_it_and_keeps_the_queue()
    {
        var project = CreateProject("cancel");
        WorkspaceQueueDatabase.Save(Root, [project.Item]);
        using var ct = new CancellationTokenSource();
        var useCase = CreateUseCase(move: (source, dest) =>
        {
            Directory.Move(source, dest);
            ct.Cancel();
        });

        var result = await useCase.ExecuteAsync(Request([project.Item], project.Item.ProjectDir), ct.Token);

        result.Cancelled.Should().BeTrue();
        result.RemovedProjects.Should().BeEmpty();
        AssertIntact(project.Item, project.Workflow);
        File.Exists(History).Should().BeFalse();
    }

    [Fact]
    public async Task Failed_second_move_rolls_back_so_the_same_project_can_be_retried()
    {
        var project = CreateProject("retry");
        WorkspaceQueueDatabase.Save(Root, [project.Item]);
        var useCase = CreateUseCase(move: (source, dest) =>
        {
            if (source == project.Workflow)
                throw new IOException("目录被占用");
            Directory.Move(source, dest);
        });

        var failed = await useCase.ExecuteAsync(Request([project.Item], project.Item.ProjectDir));
        failed.Errors.Should().ContainSingle().Which.Should().Contain("目录被占用");
        failed.RemovedProjects.Should().BeEmpty();
        AssertIntact(project.Item, project.Workflow);

        var retried = await ExecuteAsync([project.Item], project.Item.ProjectDir);
        retried.Errors.Should().BeEmpty();
        retried.DeletedDirectoryCount.Should().Be(2);
    }

    [Fact]
    public async Task Queue_commit_failure_leaves_both_directories_and_the_queue_available()
    {
        var project = CreateProject("commit-failure");
        WorkspaceQueueDatabase.Save(Root, [project.Item]);
        var useCase = CreateUseCase(commit: (_, _) => throw new IOException("队列库不可写"));

        var failed = await useCase.ExecuteAsync(Request([project.Item], project.Item.ProjectDir));

        failed.Errors.Should().ContainSingle().Which.Should().Contain("队列库不可写");
        failed.RemovedProjects.Should().BeEmpty();
        AssertIntact(project.Item, project.Workflow);
        File.Exists(History).Should().BeFalse();
    }

    [Fact]
    public async Task Failed_purge_leaves_staged_directories_and_the_recovery_manifest()
    {
        var project = CreateProject("purge-failure");
        WorkspaceQueueDatabase.Save(Root, [project.Item]);
        var useCase = CreateUseCase(purge: _ => throw new IOException("文件仍然占用"));

        var result = await useCase.ExecuteAsync(Request([project.Item], project.Item.ProjectDir));

        result.RemovedProjects.Should().Equal(project.Item.ProjectDir);
        result.DeletedDirectoryCount.Should().Be(0);
        var stagingRoot = Path.Combine(Root, QueueProjectDeletionPaths.StagingFolderName);
        var stage = Directory.GetDirectories(stagingRoot).Should().ContainSingle().Which;
        result.Errors.Should().ContainSingle().Which.Should().Contain(stage);
        Directory.GetDirectories(stage).Should().HaveCount(2);
        File.ReadAllText(Path.Combine(stage, "restore.json")).Should().Contain("purge-failure");
        WorkspaceQueueDatabase.Load(Root).Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Loading_queue_restores_directories_moved_before_the_removal_commit(bool movedWorkflow)
    {
        var project = CreateProject("interrupted");
        WorkspaceQueueDatabase.Save(Root, [project.Item]);
        var stage = CreateAbandonedStage(project.Item.ProjectDir, project.Workflow, movedWorkflow);

        WorkspaceQueueService.LoadPersistedSnapshot(Root).Items
            .Select(item => item.ProjectDir).Should().Contain(project.Item.ProjectDir);

        AssertIntact(project.Item, project.Workflow);
        Directory.Exists(stage).Should().BeFalse();
        WorkspaceQueueService.ScanProjects(Root).Should().Contain(item => item.ProjectDir == project.Item.ProjectDir);
    }

    [Fact]
    public void Recovery_never_revives_a_committed_removal_or_replaces_a_recreated_directory()
    {
        var removed = CreateProject("committed");
        var conflicted = CreateProject("conflict");
        WorkspaceQueueDatabase.Save(Root, [removed.Item, conflicted.Item]);
        var committedStage = CreateAbandonedStage(removed.Item.ProjectDir, removed.Workflow, movedWorkflow: true);
        WorkspaceQueueService.CommitProjectDeletion(Root, removed.Item.ProjectDir);
        var conflictedStage = CreateAbandonedStage(conflicted.Item.ProjectDir, conflicted.Workflow, movedWorkflow: true);
        Directory.CreateDirectory(conflicted.Item.ProjectDir);
        var fresh = Path.Combine(conflicted.Item.ProjectDir, "new.txt");
        File.WriteAllText(fresh, "new content");

        _ = WorkspaceQueueService.LoadPersistedSnapshot(Root);

        Directory.Exists(removed.Item.ProjectDir).Should().BeFalse();
        Directory.Exists(removed.Workflow).Should().BeFalse();
        Directory.Exists(Path.Combine(committedStage, "0")).Should().BeTrue();
        File.ReadAllText(fresh).Should().Be("new content");
        Directory.Exists(Path.Combine(conflictedStage, "0")).Should().BeTrue();
        Directory.Exists(Path.Combine(conflictedStage, "1")).Should().BeTrue();
    }

    [Fact]
    public void Recovery_waits_for_an_active_delete_to_release_its_staging_lease()
    {
        var project = CreateProject("reserved");
        WorkspaceQueueDatabase.Save(Root, [project.Item]);
        using (var lease = QueueProjectDeletionRecovery.TryAcquire(Root, create: true))
        {
            lease.Should().NotBeNull();
            _ = CreateAbandonedStage(project.Item.ProjectDir, project.Workflow, movedWorkflow: true);
            _ = WorkspaceQueueService.LoadPersistedSnapshot(Root);
            Directory.Exists(project.Item.ProjectDir).Should().BeFalse();
        }

        _ = WorkspaceQueueService.LoadPersistedSnapshot(Root);
        AssertIntact(project.Item, project.Workflow);
    }

    private string CreateAbandonedStage(string project, string workflow, bool movedWorkflow)
    {
        var stage = Path.Combine(Root, QueueProjectDeletionPaths.StagingFolderName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        // Emulate the restore.json created by a client that exited before handling rollback.
        File.WriteAllText(Path.Combine(stage, "restore.json"), JsonSerializer.Serialize(new
        {
            Project = project,
            Directories = new Dictionary<string, string> { ["0"] = project, ["1"] = workflow },
        }));
        Directory.Move(project, Path.Combine(stage, "0"));
        if (movedWorkflow)
            Directory.Move(workflow, Path.Combine(stage, "1"));
        return stage;
    }

    [Fact]
    public async Task Rejected_ownership_does_not_hide_or_delete_either_project()
    {
        var project = CreateProject("wrong-owner");
        var other = CreateProject("other");
        WorkspaceQueueDatabase.Save(Root, [project.Item, other.Item]);
        WriteMetadata(project.Item.ProjectDir, project.Item.ProjectDir, other.Workflow);

        var failed = await ExecuteAsync([project.Item, other.Item], project.Item.ProjectDir);

        failed.Errors.Should().ContainSingle().Which.Should().Contain("另一项目");
        failed.RemovedProjects.Should().BeEmpty();
        Directory.Exists(project.Item.ProjectDir).Should().BeTrue();
        Directory.Exists(other.Workflow).Should().BeTrue();
        WorkspaceQueueDatabase.Load(Root).Items.Should().HaveCount(2);
        File.Exists(History).Should().BeFalse();
    }

    [Fact]
    public async Task Busy_workspace_and_wrong_account_leave_queue_and_directories_intact()
    {
        var project = CreateProject("busy");
        WorkspaceQueueDatabase.Save(Root, [project.Item]);
        var blocked = await CreateUseCase(reserve: _ => null).ExecuteAsync(Request([project.Item], project.Item.ProjectDir));
        blocked.Errors.Should().ContainSingle().Which.Should().Contain("正在执行队列");

        project.Item.AccountProfileId = "other-account";
        var wrongAccount = await ExecuteAsync([project.Item], project.Item.ProjectDir);
        wrongAccount.Errors.Should().ContainSingle().Which.Should().Contain("不属于当前账号");
        AssertIntact(project.Item, project.Workflow);
    }

    [Fact]
    public async Task Directory_outside_workspace_is_rejected_even_when_metadata_declares_it()
    {
        var project = CreateProject("external");
        var outside = _temp.CreateSubdirectory("outside").FullName;
        WriteMetadata(project.Item.ProjectDir, project.Item.ProjectDir, outside);
        WorkspaceQueueDatabase.Save(Root, [project.Item]);

        var result = await ExecuteAsync([project.Item], project.Item.ProjectDir);

        result.Errors.Should().ContainSingle().Which.Should().Contain("工作区外");
        Directory.Exists(outside).Should().BeTrue();
        AssertIntact(project.Item, project.Workflow);
    }

    [Fact]
    public async Task Orchestrator_blocks_a_queue_start_during_deletion_and_releases_the_reservation()
    {
        Directory.CreateDirectory(Root);
        var orchestrator = new WorkspaceQueueOrchestrator();
        using (var first = orchestrator.TryReserveWorkspaceForDeletion(Root))
        {
            first.Should().NotBeNull();
            orchestrator.TryReserveWorkspaceForDeletion(Root + Path.DirectorySeparatorChar).Should().BeNull();
            Func<Task> start = () => orchestrator.RunWorkspaceAsync(Root, [], new QueueRunOptions(), null!,
                new AccountStore(), FinalAction.None, "test", _ => { }, (_, _) => { }, CancellationToken.None,
                selectionValidated: true);
            await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*正在删除项目*");
            using var independent = orchestrator.TryReserveWorkspaceForDeletion(Path.Combine(_temp.FullName, "other"));
            independent.Should().NotBeNull();
        }
        using var next = orchestrator.TryReserveWorkspaceForDeletion(Root);
        next.Should().NotBeNull();
    }

    [Fact]
    public void Scanning_a_legacy_deletion_marker_cannot_remove_an_external_directory()
    {
        Directory.CreateDirectory(Root);
        var outside = _temp.CreateSubdirectory("legacy-external").FullName;
        WorkspaceQueueService.RememberDeletedProjects(Root, [outside]);

        _ = WorkspaceQueueService.ScanProjects(Root);

        Directory.Exists(outside).Should().BeTrue();
    }

    private Task<QueueProjectDeletionResult> ExecuteAsync(IReadOnlyList<QueueProjectItem> snapshot, string projectDir) =>
        CreateUseCase().ExecuteAsync(Request(snapshot, projectDir));

    private QueueProjectDeletionRequest Request(IReadOnlyList<QueueProjectItem> snapshot, string projectDir) =>
        new(Root, new TikTokAccountProfile { Id = "account", TiktokUploadProfilePath = Root }, [projectDir], snapshot);

    private QueueProjectDeletionUseCase CreateUseCase(Func<string, IDisposable?>? reserve = null,
        Action<string, string>? move = null, Action<string, string>? commit = null, Action<string>? purge = null) =>
        new(reserve ?? new WorkspaceQueueOrchestrator().TryReserveWorkspaceForDeletion,
            move ?? Directory.Move, commit ?? WorkspaceQueueService.CommitProjectDeletion,
            purge ?? (path => Directory.Delete(path, recursive: true)), History);

    private (QueueProjectItem Item, string Workflow) CreateProject(string name)
    {
        var source = Path.Combine(Root, name);
        var workflow = Path.Combine(Root, "workflow", "_" + name);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workflow);
        WriteMetadata(source, source, workflow);
        WriteMetadata(workflow, source, workflow);
        return (new QueueProjectItem { ProjectDir = source, DisplayName = name, AccountProfileId = "account" }, workflow);
    }

    private static void WriteMetadata(string target, string source, string workflow) =>
        File.WriteAllText(Path.Combine(target, "shortdrama-project.json"), JsonSerializer.Serialize(new
        {
            sourceProjectDir = source,
            workflowProjectDir = workflow,
        }));

    private void AssertIntact(QueueProjectItem item, string workflow)
    {
        Directory.Exists(item.ProjectDir).Should().BeTrue();
        Directory.Exists(workflow).Should().BeTrue();
        WorkspaceQueueDatabase.Load(Root).Items.Select(project => project.ProjectDir).Should().Contain(item.ProjectDir);
        WorkspaceQueueService.IsDeletedProjectDir(Root, item.ProjectDir).Should().BeFalse();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Delete(recursive: true);
    }
}
