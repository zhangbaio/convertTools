using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class ProjectInfoTextHelperTests
{
    [Fact]
    public void ParseInfoFile_UsesEarliestSeparatorWhenValueContainsOtherColonStyle()
    {
        var directory = CreateTempDirectory();
        var infoPath = Path.Combine(directory, "短剧信息.txt");
        const string synopsis = "重逢后他得知当年真相：她独自扛下了所有。";

        try
        {
            File.WriteAllText(
                infoPath,
                $"""
                简介: {synopsis}
                推荐语：误会重逢: 爱恨再度翻涌
                """);

            var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);

            info["简介"].Should().Be(synopsis);
            info["推荐语"].Should().Be("误会重逢: 爱恨再度翻涌");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UpdateFields_ReplacesFieldWhenExistingValueContainsOtherColonStyle()
    {
        var directory = CreateTempDirectory();
        var infoPath = Path.Combine(directory, "短剧信息.txt");

        try
        {
            File.WriteAllText(
                infoPath,
                """
                新剧名: 四年重逢贺先生情难自禁
                简介: 直到他得知当年真相：她是独自扛下绝境才忍痛分手。
                """);

            ProjectInfoTextHelper.UpdateFields(
                infoPath,
                new Dictionary<string, string> { ["简介"] = "更新后的简介：真相终于揭开。" });

            var lines = File.ReadAllLines(infoPath);
            lines.Should().ContainSingle(line => line.StartsWith("简介:", StringComparison.Ordinal));
            ProjectInfoTextHelper.ParseInfoFile(infoPath)["简介"]
                .Should().Be("更新后的简介：真相终于揭开。");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WorkspaceProjectScanner_UsesSharedParserForSynopsisContainingFullWidthColon()
    {
        var directory = CreateTempDirectory();
        var projectDir = Path.Combine(directory, "原剧名");
        Directory.CreateDirectory(projectDir);
        const string synopsis = "直到他得知当年真相：她是独自扛下绝境才忍痛分手。";

        try
        {
            File.WriteAllText(
                Path.Combine(projectDir, "短剧信息.txt"),
                $"""
                原剧名: 原剧名
                新剧名: 四年重逢贺先生情难自禁
                简介: {synopsis}
                """);

            var project = WorkspaceProjectScanner.BuildProject(projectDir);

            project.Description.Should().Be(synopsis);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WorkspaceProjectScanner_UsesStagingCount_InsteadOfRecursiveMaterialCopies()
    {
        var workspace = CreateTempDirectory();
        var projectDir = Path.Combine(workspace, "原剧名");
        var workflowDir = Path.Combine(workspace, "workflow", "原剧名");
        var stagingDir = Path.Combine(workflowDir, TikTokUploadStagingService.StagingDirName);
        var materialVideosDir = Path.Combine(workflowDir, "项目原始资料", "参考格式原始素材包", "videos");
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(materialVideosDir);

        try
        {
            File.WriteAllText(Path.Combine(projectDir, "shortdrama-project.json"), """{"episodeCount":120}""");
            for (var episode = 1; episode <= 40; episode++)
            {
                File.WriteAllBytes(Path.Combine(stagingDir, $"新剧名-第{episode}集.mp4"), [1]);
                File.WriteAllBytes(Path.Combine(materialVideosDir, $"新剧名-第{episode}集.mp4"), [2]);
            }

            var project = WorkspaceProjectScanner.BuildProject(projectDir);

            project.EpisodeCount.Should().Be(40);
            project.PrimaryVideoPath.Should().StartWith(stagingDir);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"project-info-helper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
