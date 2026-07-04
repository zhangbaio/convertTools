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

            using (var service = new QueueStatePersistService(TimeSpan.Zero))
            {
                service.Enqueue(workspace, [item], new QueueRunOptions { EnabledSteps = ["download"] });
                service.Flush(workspace, TimeSpan.FromSeconds(5)).Should().BeTrue();
            }

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
}
