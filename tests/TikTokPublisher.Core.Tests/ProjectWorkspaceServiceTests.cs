using FluentAssertions;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class ProjectWorkspaceServiceTests
{
    [Fact]
    public void EnsureWorkflowInfo_WritesMetadataIntroWhenCreatingInfoFile()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"project-workspace-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspace, "潮王归海");
        Directory.CreateDirectory(sourceDir);

        const string intro = "被儿媳嫌弃的老渔民回到蚝壳屯，凭祖传听潮辨鱼本领守住天然渔场。";
        File.WriteAllText(
            Path.Combine(sourceDir, "shortdrama-project.json"),
            $$"""
            {
              "title": "潮王归海",
              "newTitle": "落难王爷重返故土",
              "intro": "{{intro}}"
            }
            """);

        try
        {
            var workflowDir = ProjectWorkspaceService.EnsureWorkflowInfo(sourceDir, 71);

            var info = ProjectInfoTextHelper.ParseInfoFile(Path.Combine(workflowDir, "短剧信息.txt"));
            info["简介"].Should().Be(intro);
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void EnsureWorkflowInfo_BackfillsBlankSynopsisFromMetadataIntro()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"project-workspace-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspace, "潮王归海");
        Directory.CreateDirectory(sourceDir);

        const string intro = "被儿媳嫌弃的老渔民回到蚝壳屯，凭祖传听潮辨鱼本领守住天然渔场。";
        File.WriteAllText(
            Path.Combine(sourceDir, "shortdrama-project.json"),
            $$"""
            {
              "title": "潮王归海",
              "newTitle": "落难王爷重返故土",
              "intro": "{{intro}}"
            }
            """);

        try
        {
            var workflowDir = ProjectWorkspaceService.EnsureWorkflowProjectDir(sourceDir);
            var infoPath = Path.Combine(workflowDir, "短剧信息.txt");
            File.WriteAllText(infoPath, """
            新剧名: 落难王爷重返故土
            原剧名: 潮王归海
            简介:
            集数: 1
            """);

            ProjectWorkspaceService.EnsureWorkflowInfo(sourceDir, 71);

            var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);
            info["简介"].Should().Be(intro);
            info["集数"].Should().Be("71");
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }
}
