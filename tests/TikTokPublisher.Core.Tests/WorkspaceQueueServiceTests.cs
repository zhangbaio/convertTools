using FluentAssertions;
using TikTokPublisher.Core.Queue;

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

    private static void CreateProject(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "shortdrama-project.json"), "{}");
    }
}
