using FluentAssertions;
using TikTokPublisher.Core.Queue;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueStatePersistServiceTests
{
    [Fact]
    public void Flush_Should_Persist_Enqueued_Queue_Items()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"queue-persist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        var projectDir = Path.Combine(workspace, "demo");
        Directory.CreateDirectory(projectDir);

        try
        {
            var item = new QueueProjectItem
            {
                ProjectDir = projectDir,
                DisplayName = "demo",
                Enabled = true,
                StatusText = QueueStepStatus.Pending,
            };
            File.WriteAllText(Path.Combine(projectDir, "shortdrama-project.json"), "{}");

            using (var service = new QueueStatePersistService(TimeSpan.FromMilliseconds(50)))
            {
                service.Enqueue(workspace, [item], new QueueRunOptions { EnabledSteps = ["download"] });
                service.Flush(workspace, TimeSpan.FromSeconds(5)).Should().BeTrue();
            }

            Thread.Sleep(100);
            var loaded = WorkspaceQueueService.ScanProjects(workspace);
            loaded.Should().ContainSingle(project =>
                string.Equals(Path.GetFullPath(project.ProjectDir), Path.GetFullPath(projectDir), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
                // SQLite on Windows may still hold the queue db briefly after dispose.
            }
        }
    }

    [Fact]
    public void Flush_Does_Not_Write_Projects_From_Another_Workspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"queue-persist-own-{Guid.NewGuid():N}");
        var otherWorkspace = Path.Combine(Path.GetTempPath(), $"queue-persist-other-{Guid.NewGuid():N}");
        var ownProject = Path.Combine(workspace, "own");
        var foreignProject = Path.Combine(otherWorkspace, "foreign");
        Directory.CreateDirectory(ownProject);
        Directory.CreateDirectory(foreignProject);

        try
        {
            var own = new QueueProjectItem
            {
                ProjectDir = ownProject,
                DisplayName = "own",
                NewTitle = "自己的项目",
                Enabled = true,
                StatusText = QueueStepStatus.Pending,
            };
            File.WriteAllText(Path.Combine(ownProject, "shortdrama-project.json"), "{}");
            File.WriteAllText(Path.Combine(foreignProject, "shortdrama-project.json"), "{}");
            WorkspaceQueueService.SaveProjects(workspace, [own]);

            using (var service = new QueueStatePersistService(TimeSpan.FromMilliseconds(20)))
            {
                service.Enqueue(workspace, [new QueueProjectItem
                {
                    ProjectDir = foreignProject,
                    DisplayName = "foreign",
                    NewTitle = "别人的项目",
                }]);
                service.Flush(workspace, TimeSpan.FromSeconds(5)).Should().BeTrue();
            }

            var loaded = WorkspaceQueueDatabase.Load(workspace).Items.Should().ContainSingle().Subject;
            loaded.ProjectDir.Should().Be(Path.GetFullPath(ownProject));
            loaded.NewTitle.Should().Be("自己的项目");
        }
        finally
        {
            TryDelete(workspace);
            TryDelete(otherWorkspace);
        }
    }

    private static void TryDelete(string workspace)
    {
        try
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
        catch (IOException)
        {
            // SQLite on Windows may still hold the queue db briefly after dispose.
        }
    }
}
