using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class ProjectWorkspaceServiceTests
{
    [Fact]
    public void ValidateContextOwnership_accepts_custom_workflow_with_matching_source_metadata()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"project-workspace-owner-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspace, "source-a");
        var workflowDir = Path.Combine(workspace, "workflow", "_renamed-a");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(workflowDir);
        WriteMetadata(sourceDir, sourceDir, workflowDir);
        WriteMetadata(workflowDir, sourceDir, workflowDir);

        try
        {
            var context = ProjectWorkspaceService.LoadContext(sourceDir);

            var action = () => ProjectWorkspaceService.ValidateContextOwnership(context);

            action.Should().NotThrow();
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void ValidateContextOwnership_rejects_workflow_owned_by_another_project()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"project-workspace-cross-owner-{Guid.NewGuid():N}");
        var sourceA = Path.Combine(workspace, "source-a");
        var sourceB = Path.Combine(workspace, "source-b");
        var workflowB = Path.Combine(workspace, "workflow", "_renamed-b");
        Directory.CreateDirectory(sourceA);
        Directory.CreateDirectory(sourceB);
        Directory.CreateDirectory(workflowB);
        WriteMetadata(sourceA, sourceA, workflowB);
        WriteMetadata(workflowB, sourceB, workflowB);

        try
        {
            var context = ProjectWorkspaceService.LoadContext(sourceA);

            var action = () => ProjectWorkspaceService.ValidateContextOwnership(context);

            action.Should().Throw<InvalidDataException>()
                .WithMessage("*属于另一项目*");
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void ValidateContextOwnership_repairs_metadata_after_workspace_parent_move()
    {
        var root = Path.Combine(Path.GetTempPath(), $"project-workspace-moved-{Guid.NewGuid():N}");
        var oldWorkspace = Path.Combine(root, "old-workspace");
        var currentWorkspace = Path.Combine(root, "current-workspace");
        var sourceDir = Path.Combine(currentWorkspace, "source-a");
        var workflowDir = Path.Combine(currentWorkspace, "workflow", "_renamed-a");
        var oldSourceDir = Path.Combine(oldWorkspace, "source-a");
        var oldWorkflowDir = Path.Combine(oldWorkspace, "workflow", "_renamed-a");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(workflowDir);
        WriteMetadata(sourceDir, oldSourceDir, oldWorkflowDir);
        WriteMetadata(workflowDir, oldSourceDir, oldWorkflowDir);

        try
        {
            var context = ProjectWorkspaceService.LoadContext(sourceDir);

            var action = () => ProjectWorkspaceService.ValidateContextOwnership(context);

            action.Should().NotThrow();
            ReadMetadataPath(sourceDir, "sourceProjectDir").Should().Be(sourceDir);
            ReadMetadataPath(sourceDir, "workflowProjectDir").Should().Be(workflowDir);
            ReadMetadataPath(workflowDir, "sourceProjectDir").Should().Be(sourceDir);
            ReadMetadataPath(workflowDir, "workflowProjectDir").Should().Be(workflowDir);
            Directory.Exists(oldWorkspace).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ValidateContextOwnership_does_not_repair_when_declared_source_still_exists()
    {
        var root = Path.Combine(Path.GetTempPath(), $"project-workspace-existing-owner-{Guid.NewGuid():N}");
        var oldWorkspace = Path.Combine(root, "old-workspace");
        var currentWorkspace = Path.Combine(root, "current-workspace");
        var oldSourceDir = Path.Combine(oldWorkspace, "source-a");
        var sourceDir = Path.Combine(currentWorkspace, "source-a");
        var workflowDir = Path.Combine(currentWorkspace, "workflow", "_renamed-a");
        Directory.CreateDirectory(oldSourceDir);
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(workflowDir);
        WriteMetadata(sourceDir, oldSourceDir, workflowDir);
        WriteMetadata(workflowDir, oldSourceDir, workflowDir);

        try
        {
            var context = ProjectWorkspaceService.LoadContext(sourceDir);

            var action = () => ProjectWorkspaceService.ValidateContextOwnership(context);

            action.Should().Throw<InvalidDataException>()
                .WithMessage("*sourceProjectDir 与实际目录不一致*");
            ReadMetadataPath(sourceDir, "sourceProjectDir").Should().Be(oldSourceDir);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ValidateContextOwnership_does_not_repair_metadata_from_different_project_name()
    {
        var root = Path.Combine(Path.GetTempPath(), $"project-workspace-wrong-name-{Guid.NewGuid():N}");
        var currentWorkspace = Path.Combine(root, "current-workspace");
        var sourceDir = Path.Combine(currentWorkspace, "source-a");
        var workflowDir = Path.Combine(currentWorkspace, "workflow", "_renamed-a");
        var staleSourceDir = Path.Combine(root, "old-workspace", "source-b");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(workflowDir);
        WriteMetadata(sourceDir, staleSourceDir, workflowDir);
        WriteMetadata(workflowDir, staleSourceDir, workflowDir);

        try
        {
            var context = ProjectWorkspaceService.LoadContext(sourceDir);

            var action = () => ProjectWorkspaceService.ValidateContextOwnership(context);

            action.Should().Throw<InvalidDataException>()
                .WithMessage("*sourceProjectDir 与实际目录不一致*");
            ReadMetadataPath(sourceDir, "sourceProjectDir").Should().Be(staleSourceDir);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

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
            info["制作公司"].Should().Be("未填写公司");
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void ResolveSourceEpisodeCount_PrefersDeclaredMetadataOverDuplicateVideoCopies()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"project-workspace-count-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspace, "七年归墟报深恩");
        var workflowDir = Path.Combine(workspace, "workflow", "_功成名就回乡报答恩人");
        var workflowVideos = Path.Combine(workflowDir, "videos");
        var materialVideos = Path.Combine(workflowDir, "项目原始资料", "视频副本");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(workflowVideos);
        Directory.CreateDirectory(materialVideos);
        File.WriteAllText(
            Path.Combine(sourceDir, "shortdrama-project.json"),
            JsonSerializer.Serialize(new
            {
                episodeCount = 31,
                sourceProjectDir = sourceDir,
                workflowProjectDir = workflowDir,
                workflowDirName = Path.GetFileName(workflowDir),
            }));
        WriteMetadata(workflowDir, sourceDir, workflowDir);

        for (var episode = 1; episode <= 31; episode++)
        {
            File.WriteAllText(Path.Combine(sourceDir, $"第{episode}集.mp4"), "source");
            File.WriteAllText(Path.Combine(workflowVideos, $"第{episode}集.mp4"), "workflow");
            File.WriteAllText(Path.Combine(materialVideos, $"第{episode}集.mp4"), "material");
        }

        try
        {
            ProjectWorkspaceService.ResolveSourceEpisodeCount(workflowDir).Should().Be(31);
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void ResolveSourceEpisodeCount_DeduplicatesEpisodeNumbersWhenMetadataIsMissing()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"project-workspace-fallback-count-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspace, "source");
        var workflowDir = Path.Combine(workspace, "workflow", "_renamed");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(workflowDir);
        WriteMetadata(sourceDir, sourceDir, workflowDir);
        WriteMetadata(workflowDir, sourceDir, workflowDir);

        for (var episode = 1; episode <= 3; episode++)
        {
            File.WriteAllText(Path.Combine(sourceDir, $"第{episode}集.mp4"), "source");
            File.WriteAllText(Path.Combine(workflowDir, $"第{episode}集.mp4"), "workflow");
        }

        try
        {
            ProjectWorkspaceService.ResolveSourceEpisodeCount(workflowDir).Should().Be(3);
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    private static void WriteMetadata(string directory, string sourceProjectDir, string workflowProjectDir)
    {
        File.WriteAllText(
            Path.Combine(directory, "shortdrama-project.json"),
            JsonSerializer.Serialize(new
            {
                sourceProjectDir,
                workflowProjectDir,
                workflowDirName = Path.GetFileName(workflowProjectDir),
            }));
    }

    private static string ReadMetadataPath(string directory, string propertyName)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory, "shortdrama-project.json")));
        return document.RootElement.GetProperty(propertyName).GetString() ?? "";
    }
}
