using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class WorkspaceQueueServiceTests
{
    [Fact]
    public void ScanProjects_Uses_Project_Created_Time_When_QueuedAt_Is_Missing()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"workspace-queue-{Guid.NewGuid():N}");
        var firstProject = Path.Combine(workspace, "first");
        var secondProject = Path.Combine(workspace, "second");
        var firstCreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, 120, DateTimeKind.Local);
        var secondCreatedAt = new DateTime(2026, 1, 3, 4, 5, 6, 340, DateTimeKind.Local);

        try
        {
            CreateProject(firstProject);
            CreateProject(secondProject);
            Directory.SetCreationTime(firstProject, firstCreatedAt);
            Directory.SetCreationTime(secondProject, secondCreatedAt);

            var items = WorkspaceQueueService.ScanProjects(workspace);

            items.Should().HaveCount(2);
            var first = items.Single(item => item.ProjectDir == firstProject);
            var second = items.Single(item => item.ProjectDir == secondProject);
            DateTimeOffset.Parse(first.QueuedAt).Should().BeCloseTo(new DateTimeOffset(firstCreatedAt), TimeSpan.FromSeconds(1));
            DateTimeOffset.Parse(second.QueuedAt).Should().BeCloseTo(new DateTimeOffset(secondCreatedAt), TimeSpan.FromSeconds(1));
            first.QueuedAt.Should().NotBe(second.QueuedAt);
        }
        finally
        {
            try
            {
                if (Directory.Exists(workspace))
                    Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
                // SQLite on Windows may still hold the queue db briefly after a scan.
            }
        }
    }

    [Fact]
    public void AddProjectsToQueue_Assigns_Distinct_Monotonic_QueuedAt_Values()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"workspace-queue-{Guid.NewGuid():N}");
        var firstProject = Path.Combine(workspace, "first");
        var secondProject = Path.Combine(workspace, "second");

        try
        {
            CreateProject(firstProject);
            CreateProject(secondProject);

            var added = WorkspaceQueueService.AddProjectsToQueue(workspace, [firstProject, secondProject]);

            added.Should().HaveCount(2);
            var timestamps = added.Select(item => DateTimeOffset.Parse(item.QueuedAt)).ToArray();
            timestamps[0].Should().BeBefore(timestamps[1]);
            added[0].QueuedAt.Should().NotBe(added[1].QueuedAt);
        }
        finally
        {
            try
            {
                if (Directory.Exists(workspace))
                    Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
                // SQLite on Windows may still hold the queue db briefly after a save.
            }
        }
    }

    [Fact]
    public void ScanProjects_Keeps_Queued_Project_Outside_Workspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"workspace-queue-{Guid.NewGuid():N}");
        var downloadRoot = Path.Combine(Path.GetTempPath(), $"workspace-download-{Guid.NewGuid():N}");
        var externalProject = Path.Combine(downloadRoot, "downloaded-project");

        try
        {
            Directory.CreateDirectory(workspace);
            CreateProject(externalProject);
            WorkspaceBindingService.Bind(workspace, "acct-current", "Current Account");

            var added = WorkspaceQueueService.AddProjectsToQueue(workspace, [externalProject]);
            added.Should().ContainSingle(item => item.ProjectDir == externalProject);

            var item = WorkspaceQueueService.ScanProjects(workspace).Should().ContainSingle().Subject;
            item.ProjectDir.Should().Be(externalProject);
            item.AccountProfileId.Should().Be("acct-current");
            item.AccountProfileName.Should().Be("Current Account");
        }
        finally
        {
            DeleteWorkspaceBestEffort(workspace);
            DeleteWorkspaceBestEffort(downloadRoot);
        }
    }

    [Fact]
    public void ScanProjects_Applies_Workspace_Binding_To_Unbound_Items()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"workspace-queue-{Guid.NewGuid():N}");
        var project = Path.Combine(workspace, "first");

        try
        {
            CreateProject(project);
            WorkspaceBindingService.Bind(workspace, "acct-current", "Current Account");

            var item = WorkspaceQueueService.ScanProjects(workspace).Should().ContainSingle().Subject;

            item.AccountProfileId.Should().Be("acct-current");
            item.AccountProfileName.Should().Be("Current Account");
        }
        finally
        {
            DeleteWorkspaceBestEffort(workspace);
        }
    }

    [Fact]
    public void ScanProjects_Keeps_Existing_Project_Account_Binding()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"workspace-queue-{Guid.NewGuid():N}");
        var firstProject = Path.Combine(workspace, "first");
        var secondProject = Path.Combine(workspace, "second");

        try
        {
            CreateProject(firstProject);
            CreateProject(secondProject);
            WorkspaceBindingService.Bind(workspace, "acct-current", "Current Account");
            WorkspaceQueueService.SaveProjects(
                workspace,
                [
                    new QueueProjectItem
                    {
                        ProjectDir = firstProject,
                        DisplayName = "first",
                        AccountProfileId = "acct-other",
                        AccountProfileName = "Other Account",
                    },
                ]);

            var items = WorkspaceQueueService.ScanProjects(workspace);

            var first = items.Single(item => item.ProjectDir == firstProject);
            var second = items.Single(item => item.ProjectDir == secondProject);
            first.AccountProfileId.Should().Be("acct-other");
            first.AccountProfileName.Should().Be("Other Account");
            second.AccountProfileId.Should().Be("acct-current");
            second.AccountProfileName.Should().Be("Current Account");
        }
        finally
        {
            DeleteWorkspaceBestEffort(workspace);
        }
    }

    [Fact]
    public void ScanProjects_Preserves_Remark_And_Applies_Manual_Upload_Status()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"workspace-queue-{Guid.NewGuid():N}");
        var project = Path.Combine(workspace, "first");

        try
        {
            CreateProject(project);
            WorkspaceQueueService.SaveProjects(
                workspace,
                [
                    new QueueProjectItem
                    {
                        ProjectDir = project,
                        DisplayName = "first",
                        Remark = "needs review",
                        ManualUploadStatus = QueueStepStatus.Failed,
                        StatusText = QueueStepStatus.Completed,
                        UploadCompletedAt = DateTimeOffset.Now.ToString("o"),
                        StepStates = new Dictionary<string, string>
                        {
                            [QueueStepKeys.UploadSeries] = QueueStepStatus.Completed,
                        },
                    },
                ]);

            var item = WorkspaceQueueService.ScanProjects(workspace).Should().ContainSingle().Subject;

            item.Remark.Should().Be("needs review");
            item.ManualUploadStatus.Should().Be(QueueStepStatus.Failed);
            item.StatusText.Should().Be(QueueStepStatus.Failed);
            item.StepStates[QueueStepKeys.UploadSeries].Should().Be(QueueStepStatus.Failed);
            item.UploadCompletedAt.Should().BeEmpty();
            item.LastError.Should().NotBeEmpty();
        }
        finally
        {
            DeleteWorkspaceBestEffort(workspace);
        }
    }

    [Fact]
    public void ScanProjects_recovers_proof_material_step_from_workflow_pdf()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"workspace-queue-{Guid.NewGuid():N}");
        var project = Path.Combine(workspace, "first");
        var workflow = Path.Combine(workspace, "workflow", "_first");

        try
        {
            CreateProject(project);
            Directory.CreateDirectory(workflow);
            WriteProjectMetadata(project, project, workflow);
            WriteProjectMetadata(workflow, project, workflow);
            File.WriteAllBytes(Path.Combine(workflow, "证明材料.pdf"), "%PDF-1.7\n"u8.ToArray());
            TikTokSourceFileInfoScreenshotService.Generate(workflow, "first", "测试公司");
            TikTokAiGenerationScreenshotService.Generate(workflow, "first", new ClientSettings());
            WorkspaceQueueService.SaveProjects(
                workspace,
                [
                    new QueueProjectItem
                    {
                        ProjectDir = project,
                        DisplayName = "first",
                        StepStates = new Dictionary<string, string>
                        {
                            [QueueStepKeys.GenerateProofMaterial] = QueueStepStatus.Pending,
                        },
                    },
                ]);

            var item = WorkspaceQueueService.ScanProjects(workspace).Should().ContainSingle().Subject;

            item.StepStates[QueueStepKeys.GenerateProofMaterial].Should().Be(QueueStepStatus.Completed);
        }
        finally
        {
            DeleteWorkspaceBestEffort(workspace);
        }
    }

    [Fact]
    public void ScanProjects_keeps_legacy_non_local_poster_recovery_compatible()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"workspace-queue-{Guid.NewGuid():N}");
        var project = Path.Combine(workspace, "legacy-project");

        try
        {
            CreateProject(project);
            File.WriteAllBytes(Path.Combine(project, "海报图片.jpg"), [1, 2, 3]);
            WorkspaceQueueService.SaveProjects(
                workspace,
                [
                    new QueueProjectItem
                    {
                        ProjectDir = project,
                        DisplayName = "legacy-project",
                        StepStates = new Dictionary<string, string>
                        {
                            [QueueStepKeys.GeneratePoster] = QueueStepStatus.Pending,
                        },
                    },
                ]);

            var item = WorkspaceQueueService.ScanProjects(workspace).Should().ContainSingle().Subject;

            item.StepStates[QueueStepKeys.GeneratePoster].Should().Be(QueueStepStatus.Completed);
            TikTokPosterGenerationStateService.NeedsGeneratePoster(item, new ClientSettings()).Should().BeFalse(
                "没有生成状态的普通旧项目仍应沿用历史海报文件兼容逻辑");
        }
        finally
        {
            DeleteWorkspaceBestEffort(workspace);
        }
    }

    [Fact]
    public void MoveProjectsToAccountWorkspace_Moves_Files_Queue_State_And_Rebinds_Account()
    {
        var sourceWorkspace = Path.Combine(Path.GetTempPath(), $"workspace-source-{Guid.NewGuid():N}");
        var targetWorkspace = Path.Combine(Path.GetTempPath(), $"workspace-target-{Guid.NewGuid():N}");
        var sourceProject = Path.Combine(sourceWorkspace, "first");
        var sourceWorkflow = Path.Combine(sourceWorkspace, "workflow", "first-workflow");
        var targetProject = Path.Combine(targetWorkspace, "first");
        var targetWorkflow = Path.Combine(targetWorkspace, "workflow", "first-workflow");
        var targetStorageState = Path.Combine(targetWorkspace, "acct-b-storage.json");

        try
        {
            Directory.CreateDirectory(targetWorkspace);
            CreateProject(sourceProject);
            Directory.CreateDirectory(sourceWorkflow);
            Directory.CreateDirectory(Path.Combine(sourceWorkflow, "upload"));
            File.WriteAllText(Path.Combine(sourceWorkflow, "upload", "01.mp4"), "video");
            WriteProjectMetadata(sourceProject, sourceProject, sourceWorkflow);
            WriteProjectMetadata(sourceWorkflow, sourceProject, sourceWorkflow);
            WorkspaceBindingService.Bind(sourceWorkspace, "acct-a", "Account A");
            WorkspaceBindingService.Bind(targetWorkspace, "acct-old", "Old Account");

            ProjectStateDocumentStore.SaveDocument(
                sourceWorkspace,
                sourceProject,
                TikTokUploadManifestService.DocumentType,
                new Dictionary<string, object?>
                {
                    ["project_dir"] = sourceProject,
                    ["workflow_project_dir"] = sourceWorkflow,
                    ["upload_video_paths"] = new List<object?> { Path.Combine(sourceWorkflow, "upload", "01.mp4") },
                    ["publish_config"] = new Dictionary<string, object?>
                    {
                        ["storage_state_path"] = "old-state.json",
                        ["upload_profile_path"] = sourceWorkspace,
                    },
                },
                sourceWorkflow);
            TikTokUploadStateStore.SaveState(
                sourceWorkflow,
                new Dictionary<string, object?>
                {
                    ["upload_step_attempted"] = true,
                    ["last_upload_completed_at"] = "2026-01-01T00:00:00",
                    ["platform_series_lookup"] = new Dictionary<string, object?>
                    {
                        ["status"] = "found",
                        ["detail_url"] = "https://www.tiktokdramacenter.com/series/draft/1234567890123456",
                    },
                });
            WorkspaceQueueService.SaveProjects(
                sourceWorkspace,
                [
                    new QueueProjectItem
                    {
                        ProjectDir = sourceProject,
                        DisplayName = "first",
                        AccountProfileId = "acct-a",
                        AccountProfileName = "Account A",
                        StatusText = QueueStepStatus.Completed,
                        UploadCompletedAt = "2026-01-01T00:00:00",
                        StepStates = new Dictionary<string, string>
                        {
                            [QueueStepKeys.MaterialValidate] = QueueStepStatus.Completed,
                            [QueueStepKeys.UploadSeries] = QueueStepStatus.Completed,
                        },
                    },
                ]);
            var targetAccount = new TikTokAccountProfile
            {
                Id = "acct-b",
                Name = "Account B",
                TiktokUploadProfilePath = targetWorkspace,
                TiktokStorageStatePath = targetStorageState,
                TiktokSeriesUrl = "https://example.test/series",
            };

            var result = WorkspaceQueueService.MoveProjectsToAccountWorkspace(
                sourceWorkspace,
                WorkspaceQueueService.ScanProjects(sourceWorkspace),
                targetAccount);

            result.Count.Should().Be(1);
            Directory.Exists(sourceProject).Should().BeFalse();
            Directory.Exists(sourceWorkflow).Should().BeFalse();
            Directory.Exists(targetProject).Should().BeTrue();
            Directory.Exists(targetWorkflow).Should().BeTrue();
            WorkspaceBindingService.ResolveAccountProfileId(targetWorkspace).Should().Be("acct-b");

            WorkspaceQueueService.ScanProjects(sourceWorkspace).Should().BeEmpty();
            var moved = WorkspaceQueueService.ScanProjects(targetWorkspace).Should().ContainSingle().Subject;
            moved.ProjectDir.Should().Be(targetProject);
            moved.AccountProfileId.Should().Be("acct-b");
            moved.AccountProfileName.Should().Be("Account B");
            moved.StepStates[QueueStepKeys.MaterialValidate].Should().Be(QueueStepStatus.Completed);
            moved.StepStates[QueueStepKeys.UploadSeries].Should().Be(QueueStepStatus.Pending);
            moved.UploadCompletedAt.Should().BeEmpty();
            moved.StatusText.Should().Be(QueueStepStatus.Pending);

            var sourceMetadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(targetProject, "shortdrama-project.json"))).RootElement;
            sourceMetadata.GetProperty("sourceProjectDir").GetString().Should().Be(targetProject);
            sourceMetadata.GetProperty("workflowProjectDir").GetString().Should().Be(targetWorkflow);

            var manifest = ProjectStateDocumentStore.LoadDocument(
                targetWorkspace,
                targetProject,
                TikTokUploadManifestService.DocumentType);
            manifest["project_dir"].GetString().Should().Be(targetProject);
            manifest["workflow_project_dir"].GetString().Should().Be(targetWorkflow);
            manifest["upload_video_paths"].EnumerateArray().Single().GetString()
                .Should().Be(Path.Combine(targetWorkflow, "upload", "01.mp4"));
            var publishConfig = manifest["publish_config"];
            publishConfig.GetProperty("storage_state_path").GetString().Should().Be(targetStorageState);
            publishConfig.GetProperty("upload_profile_path").GetString().Should().Be(targetWorkspace);

            TikTokUploadStateStore.LoadState(targetWorkflow).Should().NotContainKey("last_upload_completed_at");
            TikTokUploadStateStore.LoadCachedEditDetailUrl(targetWorkflow).Should().BeEmpty();
            ProjectStateDocumentStore.LoadDocument(
                    sourceWorkspace,
                    sourceProject,
                    TikTokUploadManifestService.DocumentType)
                .Should().BeEmpty();
        }
        finally
        {
            DeleteWorkspaceBestEffort(sourceWorkspace);
            DeleteWorkspaceBestEffort(targetWorkspace);
        }
    }

    private static void CreateProject(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "shortdrama-project.json"), "{}");
    }

    private static void WriteProjectMetadata(string projectDir, string sourceProjectDir, string workflowProjectDir)
    {
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(
            Path.Combine(projectDir, "shortdrama-project.json"),
            JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["sourceProjectDir"] = sourceProjectDir,
                    ["workflowProjectDir"] = workflowProjectDir,
                    ["workflowDirName"] = Path.GetFileName(workflowProjectDir),
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void DeleteWorkspaceBestEffort(string workspace)
    {
        try
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
        catch (IOException)
        {
            // SQLite on Windows may still hold the queue db briefly after a save.
        }
    }
}
