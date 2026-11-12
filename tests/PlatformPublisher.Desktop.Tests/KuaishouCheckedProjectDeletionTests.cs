using PlatformPublisher.Desktop.Services;
using Xunit;

namespace PlatformPublisher.Desktop.Tests;

public sealed class KuaishouCheckedProjectDeletionTests
{
    [Fact]
    public void Project_folder_and_its_workflow_folder_are_both_deleted()
    {
        var root = NewRoot();
        try
        {
            var project = Path.Combine(root, "女战神");
            var workflow = Path.Combine(root, "workflow", "_女战神");
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(workflow);
            File.WriteAllText(
                Path.Combine(project, "shortdrama-project.json"),
                $$"""{"workflowProjectDir":{{Json(workflow)}}}""");

            var plan = KuaishouCheckedProjectDeletion.Resolve(root, project);

            Assert.Null(plan.Refusal);
            Assert.Equal(
                [Path.GetFullPath(workflow), Path.GetFullPath(project)],
                plan.Directories);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_project_folder_is_already_gone()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(root);
            var plan = KuaishouCheckedProjectDeletion.Resolve(root, Path.Combine(root, "已经删掉"));

            Assert.Null(plan.Refusal);
            Assert.Empty(plan.Directories);
            Assert.True(plan.AlreadyGone);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("workflow")]
    [InlineData("archive")]
    public void Workspace_root_and_reserved_folders_are_refused(string child)
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(root);
            var target = string.IsNullOrEmpty(child) ? root : Path.Combine(root, child);
            Directory.CreateDirectory(target);

            var plan = KuaishouCheckedProjectDeletion.Resolve(root, target);

            Assert.NotNull(plan.Refusal);
            Assert.Empty(plan.Directories);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Folder_outside_the_workspace_is_refused()
    {
        var root = NewRoot();
        var outside = NewRoot();
        try
        {
            var project = Path.Combine(outside, "别的剧");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(project);

            var plan = KuaishouCheckedProjectDeletion.Resolve(root, project);

            Assert.NotNull(plan.Refusal);
            Assert.Empty(plan.Directories);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "ks-delete-" + Guid.NewGuid().ToString("N"));

    private static string Json(string path) => "\"" + path.Replace("\\", "\\\\") + "\"";
}
