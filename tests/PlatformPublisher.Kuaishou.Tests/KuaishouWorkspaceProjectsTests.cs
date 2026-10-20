using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouWorkspaceProjectsTests
{
    [Fact]
    public void Listed_project_must_be_a_workspace_child_with_project_json()
    {
        var root = Directory.CreateTempSubdirectory();
        var project = Directory.CreateDirectory(Path.Combine(root.FullName, "心藏温善"));
        File.WriteAllText(Path.Combine(project.FullName, KuaishouWorkspaceProjects.ProjectFileName), "{}");
        var empty = Directory.CreateDirectory(Path.Combine(root.FullName, "太医驾到"));
        Directory.CreateDirectory(Path.Combine(root.FullName, "workflow", "别的项目"));
        File.WriteAllText(
            Path.Combine(root.FullName, "workflow", "别的项目", KuaishouWorkspaceProjects.ProjectFileName),
            "{}");

        Assert.True(KuaishouWorkspaceProjects.IsListed(root.FullName, project.FullName));
        Assert.False(KuaishouWorkspaceProjects.IsListed(root.FullName, empty.FullName));
        Assert.False(KuaishouWorkspaceProjects.IsListed(
            root.FullName,
            Path.Combine(root.FullName, "workflow", "别的项目")));
    }
}
