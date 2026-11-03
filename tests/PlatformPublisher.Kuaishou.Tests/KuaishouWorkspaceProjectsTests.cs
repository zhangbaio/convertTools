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
        var videosOnly = Directory.CreateDirectory(Path.Combine(root.FullName, "红果_只有视频"));
        File.WriteAllBytes(Path.Combine(videosOnly.FullName, "1.mp4"), [1]);
        Assert.False(KuaishouWorkspaceProjects.IsListed(root.FullName, videosOnly.FullName));
        var imported = Directory.CreateDirectory(Path.Combine(root.FullName, "红果_已导入"));
        File.WriteAllText(
            Path.Combine(imported.FullName, KuaishouWorkspaceProjects.ProjectFileName),
            """{"localImported":true,"importMode":"local"}""");
        Assert.True(KuaishouWorkspaceProjects.IsListed(root.FullName, imported.FullName));
        var listed = KuaishouWorkspaceProjects.List(root.FullName);
        Assert.Contains(project.FullName, listed);
        Assert.Contains(imported.FullName, listed);
        Assert.DoesNotContain(videosOnly.FullName, listed);
        Assert.DoesNotContain(empty.FullName, listed);
    }

    [Fact]
    public void Listed_result_is_cached_until_invalidated()
    {
        var root = Directory.CreateTempSubdirectory();
        var project = Directory.CreateDirectory(Path.Combine(root.FullName, "心藏温善"));
        var projectFile = Path.Combine(project.FullName, KuaishouWorkspaceProjects.ProjectFileName);
        File.WriteAllText(projectFile, "{}");

        Assert.True(KuaishouWorkspaceProjects.IsListed(root.FullName, project.FullName));
        File.Delete(projectFile);
        Assert.True(KuaishouWorkspaceProjects.IsListed(root.FullName, project.FullName));
        Assert.True(KuaishouWorkspaceProjects.IsStructuralChild(root.FullName, project.FullName));

        KuaishouWorkspaceProjects.Invalidate(project.FullName);
        Assert.False(KuaishouWorkspaceProjects.IsListed(root.FullName, project.FullName));
        Assert.True(KuaishouWorkspaceProjects.TryGetListed(root.FullName, project.FullName, out var listed));
        Assert.False(listed);
    }
}
