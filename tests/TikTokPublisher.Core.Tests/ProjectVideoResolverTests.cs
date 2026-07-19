using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class ProjectVideoResolverTests
{
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
    public void BuildPayload_UsesExistingStagedVideos_WhenOriginalVideosAreMissingAndTitleChanged()
    {
        var workspaceDir = Path.Combine(Path.GetTempPath(), $"upload-staging-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspaceDir, "source");
        var stagingDir = Path.Combine(workspaceDir, "workflow", "source", TikTokUploadStagingService.StagingDirName);
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(stagingDir);

        try
        {
            var first = Path.Combine(stagingDir, "Old title-episode-1.mp4");
            var second = Path.Combine(stagingDir, "Old title-episode-2.mp4");
            File.WriteAllBytes(first, [1]);
            File.WriteAllBytes(second, [2]);

            var result = TikTokUploadStagingService.BuildPayload(
                sourceDir,
                "New title",
                "Original title",
                rebuildStaging: false,
                repairSmallVideos: false,
                log: null,
                CancellationToken.None);

            result.UploadPaths.Should().Equal(first, second);
        }
        finally
        {
            try { Directory.Delete(workspaceDir, recursive: true); }
            catch (IOException) { }
        }
    }
}
