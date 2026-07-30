using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class WorkspaceMergeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"workspace-merge-{Guid.NewGuid():N}");

    public WorkspaceMergeServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // SQLite can briefly retain a Windows file handle after a test.
        }
    }

    [Fact]
    public void Merge_copies_active_project_preserves_state_and_is_idempotent()
    {
        var sourceWorkspace = Path.Combine(_root, "source-workspace");
        var targetWorkspace = Path.Combine(_root, "target-workspace");
        Directory.CreateDirectory(sourceWorkspace);
        Directory.CreateDirectory(targetWorkspace);
        var (sourceProject, sourceWorkflow) = CreateProject(sourceWorkspace, "示例短剧");
        var targetAccount = CreateTargetAccount();
        WorkspaceBindingService.Bind(targetWorkspace, targetAccount.Id, targetAccount.DisplayName);

        var sourceItem = new QueueProjectItem
        {
            ProjectDir = sourceProject,
            DisplayName = "示例短剧",
            OriginalTitle = "原剧名",
            NewTitle = "新剧名",
            AccountProfileId = "old-account",
            AccountProfileName = "旧账号",
            StatusText = QueueStepStatus.Completed,
            UploadCompletedAt = "2026-07-29T10:20:30+08:00",
            StepStates = new Dictionary<string, string>
            {
                [QueueStepKeys.Download] = QueueStepStatus.Completed,
                [QueueStepKeys.GenerateProofMaterial] = QueueStepStatus.Completed,
                [QueueStepKeys.UploadSeries] = QueueStepStatus.Completed,
            },
        };
        WorkspaceQueueDatabase.Save(sourceWorkspace, [sourceItem]);
        ProjectStateDocumentStore.SaveDocument(
            sourceWorkspace,
            sourceProject,
            "merge-test",
            new Dictionary<string, object?>
            {
                ["source_path"] = sourceProject,
                ["workflow_path"] = sourceWorkflow,
            },
            sourceWorkflow);

        var analysis = WorkspaceMergeService.Analyze(targetWorkspace, [sourceWorkspace]);
        var first = WorkspaceMergeService.Merge(
            analysis,
            targetAccount,
            targetArchiveRootDir: Path.Combine(targetWorkspace, "archive"));

        first.ImportedProjectCount.Should().Be(1);
        first.ReusedProjectCount.Should().Be(0);
        var imported = WorkspaceQueueService.ScanProjects(targetWorkspace).Should().ContainSingle().Subject;
        imported.NewTitle.Should().Be("新剧名");
        imported.AccountProfileId.Should().Be(targetAccount.Id);
        imported.UploadCompletedAt.Should().Be(sourceItem.UploadCompletedAt);
        imported.StepStates[QueueStepKeys.GenerateProofMaterial].Should().Be(QueueStepStatus.Completed);
        imported.StepStates[QueueStepKeys.UploadSeries].Should().Be(QueueStepStatus.Completed);
        Directory.Exists(imported.ProjectDir).Should().BeTrue();

        var importedContext = ProjectWorkspaceService.LoadContext(imported.ProjectDir);
        importedContext.SourceProjectDir.Should().Be(imported.ProjectDir);
        importedContext.WorkflowProjectDir.Should().StartWith(Path.Combine(targetWorkspace, "workflow"));
        File.Exists(Path.Combine(importedContext.WorkflowProjectDir, "proof.pdf")).Should().BeTrue();
        var importedDocument = ProjectStateDocumentStore.LoadDocument(
            targetWorkspace,
            imported.ProjectDir,
            "merge-test");
        importedDocument["source_path"].GetString().Should().Be(imported.ProjectDir);
        importedDocument["workflow_path"].GetString().Should().Be(importedContext.WorkflowProjectDir);

        var second = WorkspaceMergeService.Merge(
            analysis,
            targetAccount,
            targetArchiveRootDir: Path.Combine(targetWorkspace, "archive"));

        second.ImportedProjectCount.Should().Be(0);
        second.ReusedProjectCount.Should().Be(1);
        WorkspaceQueueService.ScanProjects(targetWorkspace).Should().ContainSingle();
    }

    [Fact]
    public async Task Merge_rewrites_archived_project_for_restore_into_target_workspace()
    {
        var sourceWorkspace = Path.Combine(_root, "archive-source-workspace");
        var targetWorkspace = Path.Combine(_root, "archive-target-workspace");
        var sourceArchiveRoot = Path.Combine(sourceWorkspace, "archive");
        var targetArchiveRoot = Path.Combine(targetWorkspace, "archive");
        Directory.CreateDirectory(sourceWorkspace);
        Directory.CreateDirectory(targetWorkspace);
        var (sourceProject, sourceWorkflow) = CreateProject(sourceWorkspace, "归档短剧");
        var targetAccount = CreateTargetAccount();
        WorkspaceBindingService.Bind(targetWorkspace, targetAccount.Id, targetAccount.DisplayName);
        WorkspaceQueueDatabase.Save(
            sourceWorkspace,
            [
                new QueueProjectItem
                {
                    ProjectDir = sourceProject,
                    DisplayName = "归档短剧",
                    OriginalTitle = "归档原剧名",
                    NewTitle = "归档新剧名",
                    StatusText = QueueStepStatus.Completed,
                    StepStates = new Dictionary<string, string>
                    {
                        [QueueStepKeys.UploadSeries] = QueueStepStatus.Completed,
                    },
                },
            ]);

        await TikTokArchivedProjectService.ArchiveQueueProjectAsync(
            sourceWorkspace,
            sourceProject,
            sourceArchiveRoot,
            deleteSourceVideos: false,
            deleteWorkflowVideos: false,
            deleteMaterialVideos: false);
        Directory.Exists(sourceProject).Should().BeFalse();
        Directory.Exists(sourceWorkflow).Should().BeFalse();

        var analysis = WorkspaceMergeService.Analyze(targetWorkspace, [sourceWorkspace]);
        analysis.ArchivedProjectCount.Should().Be(1);
        var result = WorkspaceMergeService.Merge(
            analysis,
            targetAccount,
            targetArchiveRootDir: targetArchiveRoot);

        result.ImportedArchiveCount.Should().Be(1);
        var archive = TikTokArchivedProjectService.List(targetWorkspace, targetArchiveRoot)
            .Should().ContainSingle().Subject;
        archive.AccountProfileId.Should().Be(targetAccount.Id);
        archive.SourceProjectDir.Should().StartWith(targetWorkspace);
        archive.WorkflowProjectDir.Should().StartWith(Path.Combine(targetWorkspace, "workflow"));
        archive.ArchivedSourceDir.Should().StartWith(Path.Combine(targetArchiveRoot, "source"));
        archive.ArchivedWorkflowDir.Should().StartWith(Path.Combine(targetArchiveRoot, "workflow"));

        TikTokArchivedProjectService.Restore(
            targetWorkspace,
            archive.ArchiveProjectDir,
            targetArchiveRoot);

        Directory.Exists(archive.SourceProjectDir).Should().BeTrue();
        Directory.Exists(archive.WorkflowProjectDir).Should().BeTrue();
        File.Exists(Path.Combine(archive.WorkflowProjectDir, "proof.pdf")).Should().BeTrue();
    }

    [Fact]
    public void Merge_keeps_both_projects_when_directory_names_collide()
    {
        var sourceWorkspace = Path.Combine(_root, "collision-source");
        var targetWorkspace = Path.Combine(_root, "collision-target");
        Directory.CreateDirectory(sourceWorkspace);
        Directory.CreateDirectory(targetWorkspace);
        var (sourceProject, _) = CreateProject(sourceWorkspace, "同名目录");
        var (targetProject, _) = CreateProject(targetWorkspace, "同名目录");
        var targetAccount = CreateTargetAccount();
        WorkspaceBindingService.Bind(targetWorkspace, targetAccount.Id, targetAccount.DisplayName);
        WorkspaceQueueDatabase.Save(
            sourceWorkspace,
            [
                new QueueProjectItem
                {
                    ProjectDir = sourceProject,
                    OriginalTitle = "来源原剧名",
                    NewTitle = "来源新剧名",
                },
            ]);
        WorkspaceQueueDatabase.Save(
            targetWorkspace,
            [
                new QueueProjectItem
                {
                    ProjectDir = targetProject,
                    OriginalTitle = "目标原剧名",
                    NewTitle = "目标新剧名",
                },
            ]);

        var result = WorkspaceMergeService.Merge(
            WorkspaceMergeService.Analyze(targetWorkspace, [sourceWorkspace]),
            targetAccount,
            targetArchiveRootDir: Path.Combine(targetWorkspace, "archive"));

        result.ImportedProjectCount.Should().Be(1);
        var projects = WorkspaceQueueService.ScanProjects(targetWorkspace);
        projects.Should().HaveCount(2);
        projects.Select(project => project.NewTitle)
            .Should().Contain("来源新剧名");
        projects.Select(project => project.ProjectDir)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Should().HaveCount(2);
        Directory.Exists(targetProject).Should().BeTrue();
    }

    private static TikTokAccountProfile CreateTargetAccount() => new()
    {
        Id = "target-account",
        Name = "当前账号",
        TiktokAccountNickname = "当前账号",
    };

    private static (string SourceDir, string WorkflowDir) CreateProject(
        string workspace,
        string name)
    {
        var source = Path.Combine(workspace, name);
        var workflow = Path.Combine(workspace, "workflow", name);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workflow);
        File.WriteAllText(Path.Combine(source, "episode-01.mp4"), "video");
        File.WriteAllText(Path.Combine(workflow, "proof.pdf"), "proof");
        File.WriteAllText(
            Path.Combine(source, "短剧信息.txt"),
            $"原剧名：{name}{Environment.NewLine}新剧名：{name}{Environment.NewLine}");
        File.WriteAllText(
            Path.Combine(workflow, "短剧信息.txt"),
            $"新剧名：{name}{Environment.NewLine}");
        var metadata = new Dictionary<string, object?>
        {
            ["sourceProjectDir"] = source,
            ["workflowProjectDir"] = workflow,
            ["workflowDirName"] = name,
            ["originalTitle"] = name,
            ["newTitle"] = name,
            ["episodeCount"] = 1,
        };
        var json = JsonSerializer.Serialize(metadata);
        File.WriteAllText(Path.Combine(source, "shortdrama-project.json"), json);
        File.WriteAllText(Path.Combine(workflow, "shortdrama-project.json"), json);
        return (source, workflow);
    }
}
