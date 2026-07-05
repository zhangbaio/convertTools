using FluentAssertions;
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

    private static void CreateProject(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "shortdrama-project.json"), "{}");
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
