using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class ProjectVideoResolverTests
{
    [Fact]
    public void Published_material_cache_is_visible_to_materials_but_never_to_uploads()
    {
        var workspaceDir = Path.Combine(Path.GetTempPath(), $"published-material-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspaceDir, "source");
        var workflowDir = Path.Combine(workspaceDir, "workflow", "source");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(workflowDir);
        File.WriteAllText(
            Path.Combine(sourceDir, "shortdrama-project.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                sourceProjectDir = sourceDir,
                workflowProjectDir = workflowDir,
            }));
        try
        {
            var cache = ProjectVideoResolver.ResolvePublishedMaterialVideoDirectory(sourceDir);
            Directory.CreateDirectory(cache);
            var restored = Path.Combine(cache, "第001集.mp4");
            File.WriteAllBytes(restored, [1, 2, 3]);
            var local = Path.Combine(sourceDir, "第002集.mp4");
            File.WriteAllBytes(local, [4, 5, 6]);

            ProjectVideoResolver.ResolveMaterialVideos(sourceDir)
                .Should().Equal(restored, local);
            ProjectVideoResolver.ResolveSourceVideos(sourceDir, allowStagedFallback: true)
                .Should().Equal(local);
            ProjectVideoResolver.ResolveUploadVideos(sourceDir, allowStagedFallback: true)
                .Should().Equal(local)
                .And.NotContain(restored);
        }
        finally
        {
            Directory.Delete(workspaceDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveSourceVideos_IgnoresSilenceRepairTempFiles()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), $"project-video-resolver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectDir);

        try
        {
            File.WriteAllBytes(Path.Combine(projectDir, "episode-1.mp4"), [1]);
            File.WriteAllBytes(Path.Combine(projectDir, "episode-1.mp4.silencefix.mp4"), [2]);
            File.WriteAllBytes(Path.Combine(projectDir, "episode-2.mp4"), [3]);

            var names = ProjectVideoResolver.ResolveSourceVideos(projectDir)
                .Select(Path.GetFileName)
                .ToList();

            names.Should().Equal("episode-1.mp4", "episode-2.mp4");
        }
        finally
        {
            try { Directory.Delete(projectDir, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void ResolveUploadVideos_IgnoresStagedSilenceRepairTempFiles()
    {
        var workspaceDir = Path.Combine(Path.GetTempPath(), $"project-video-resolver-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspaceDir, "source");
        var stagingDir = Path.Combine(workspaceDir, "workflow", "source", TikTokUploadStagingService.StagingDirName);
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(stagingDir);

        try
        {
            File.WriteAllBytes(Path.Combine(stagingDir, "show-episode-1.mp4"), [1]);
            File.WriteAllBytes(Path.Combine(stagingDir, "show-episode-1.mp4.silencefix.mp4"), [2]);

            var names = ProjectVideoResolver.ResolveUploadVideos(sourceDir, allowStagedFallback: true)
                .Select(Path.GetFileName)
                .ToList();

            names.Should().Equal("show-episode-1.mp4");
        }
        finally
        {
            try { Directory.Delete(workspaceDir, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void ResolveUploadVideos_UsesStagingAsCanonicalSet_WhenSourceCountDiffers()
    {
        var workspaceDir = Path.Combine(Path.GetTempPath(), $"project-video-resolver-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspaceDir, "source");
        var stagingDir = Path.Combine(workspaceDir, "workflow", "source", TikTokUploadStagingService.StagingDirName);
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(stagingDir);

        try
        {
            File.WriteAllBytes(Path.Combine(sourceDir, "show-第1集.mp4"), [1]);
            File.WriteAllBytes(Path.Combine(sourceDir, "show-第2集.mp4"), [1]);
            File.WriteAllBytes(Path.Combine(stagingDir, "renamed-第1集.mp4"), [2]);

            var names = ProjectVideoResolver.ResolveUploadVideos(sourceDir, allowStagedFallback: true)
                .Select(Path.GetFileName)
                .ToList();

            names.Should().Equal("renamed-第1集.mp4");
        }
        finally
        {
            try { Directory.Delete(workspaceDir, recursive: true); }
            catch (IOException) { }
        }
    }
}
