using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouLocalDramaImportServiceTests
{
    [Fact]
    public void Candidates_include_video_folders_and_skip_empty_ones()
    {
        var root = Directory.CreateTempSubdirectory();
        var ready = Directory.CreateDirectory(Path.Combine(root.FullName, "红果_迟来的甜蜜6"));
        File.WriteAllText(Path.Combine(ready.FullName, "1.mp4"), "video");
        File.WriteAllText(Path.Combine(ready.FullName, "metadata.json"), """{"title":"迟来的甜蜜6","episode_count":1}""");
        Directory.CreateDirectory(Path.Combine(root.FullName, "空目录"));
        Directory.CreateDirectory(Path.Combine(root.FullName, "workflow"));

        var candidate = Assert.Single(KuaishouLocalDramaImportService.ListCandidates(root.FullName));

        Assert.Equal("迟来的甜蜜6", candidate.DisplayName);
        Assert.Equal(1, candidate.EpisodeCount);
        Assert.False(candidate.MetadataExists);
    }

    [Fact]
    public void Bootstrap_writes_project_metadata_and_keeps_rewritten_title()
    {
        var root = Directory.CreateTempSubdirectory();
        var project = Directory.CreateDirectory(Path.Combine(root.FullName, "红果_原剧"));
        File.WriteAllText(Path.Combine(project.FullName, "1.mp4"), "video");
        File.WriteAllText(Path.Combine(project.FullName, "短剧信息.txt"), "原剧名：原剧\n新剧名：新剧名\n");

        var result = KuaishouLocalDramaImportService.Bootstrap(project.FullName);

        Assert.Equal("原剧", result.DisplayName);
        Assert.True(File.Exists(Path.Combine(project.FullName, "shortdrama-project.json")));
        Assert.True(File.Exists(Path.Combine(root.FullName, "workflow", "_原剧", "shortdrama-project.json")));
        Assert.Contains("新剧名：新剧名", File.ReadAllText(Path.Combine(project.FullName, "短剧信息.txt")));
    }
}