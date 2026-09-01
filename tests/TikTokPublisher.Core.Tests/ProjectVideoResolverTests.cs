using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class ProjectVideoResolverTests
{
    [Fact]
    public void Narrative_videos_exclude_static_frame_fallback()
    {
        var source = Path.Combine(Path.GetTempPath(), $"narrative-video-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        try
        {
            File.WriteAllBytes(Path.Combine(source, "证明材料抽帧兜底.mp4"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(source, "第001集.mp4"), [4, 5, 6]);

            ProjectVideoResolver.ResolveMaterialVideos(source).Should().HaveCount(2);
            ProjectVideoResolver.ResolveNarrativeVideos(source)
                .Should().ContainSingle(path => Path.GetFileName(path) == "第001集.mp4");
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

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
    public void Published_recovery_staging_cache_is_visible_to_narrative_steps_but_never_to_uploads()
    {
        var workspaceDir = Path.Combine(Path.GetTempPath(), $"published-recovery-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspaceDir, "恢复剧名_版权恢复");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(
            Path.Combine(sourceDir, "shortdrama-project.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                queueEntryDramaType = DeletedCopyrightProofPublishedVideoRecoveryService.RecoverySourceType,
                newTitle = "恢复剧名",
                tiktokSeriesId = "series-123",
            }));
        var recoveryCache = DeletedCopyrightProofPublishedVideoRecoveryService.ResolveStagingDirectory(
            workspaceDir,
            "恢复剧名",
            "series-123");
        Directory.CreateDirectory(recoveryCache);
        var restored = Path.Combine(recoveryCache, "第001集.mp4");
        File.WriteAllBytes(restored, [1, 2, 3]);

        try
        {
            ProjectVideoResolver.ResolveNarrativeVideos(sourceDir)
                .Should().Equal(restored);
            ProjectVideoResolver.ResolveSourceVideos(sourceDir, allowStagedFallback: true)
                .Should().BeEmpty();
            ProjectVideoResolver.ResolveUploadVideos(sourceDir, allowStagedFallback: true)
                .Should().BeEmpty();
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
    public void Ai_screenshot_video_resolution_falls_back_to_recovery_source_videos_when_material_resolution_fails()
    {
        var workspaceDir = Path.Combine(Path.GetTempPath(), $"ai-recovery-video-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspaceDir, "恢复剧名_版权恢复");
        var workflowDir = Path.Combine(workspaceDir, "workflow", "_恢复剧名");
        var videosDir = Path.Combine(sourceDir, "videos");
        Directory.CreateDirectory(videosDir);
        Directory.CreateDirectory(workflowDir);
        File.WriteAllText(
            Path.Combine(sourceDir, "shortdrama-project.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                sourceProjectDir = sourceDir,
                workflowProjectDir = workflowDir,
                queueEntryDramaType = DeletedCopyrightProofPublishedVideoRecoveryService.RecoverySourceType,
                newTitle = "恢复剧名",
                tiktokSeriesId = "series-123",
            }));
        File.WriteAllText(
            Path.Combine(workflowDir, "shortdrama-project.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                sourceProjectDir = sourceDir,
                workflowProjectDir = workflowDir,
            }));
        var sourceVideo = Path.Combine(videosDir, "第001集.mp4");
        File.WriteAllBytes(sourceVideo, [1, 2, 3]);

        // A differently shaped natural-sort key reproduces a resolver-side exception while
        // merging the local recovery copy and the durable download cache.
        var recoveryCache = DeletedCopyrightProofPublishedVideoRecoveryService.ResolveStagingDirectory(
            workspaceDir,
            "恢复剧名",
            "series-123");
        Directory.CreateDirectory(recoveryCache);
        File.WriteAllBytes(Path.Combine(recoveryCache, "001.mp4"), [1, 2, 3]);

        try
        {
            var logs = new List<string>();

            TikTokAiGenerationScreenshotService.ResolveVideoSources(workflowDir, logs.Add)
                .Should().Equal(sourceVideo);
            logs.Should().Contain(message =>
                message.Contains("直接扫描源项目", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workspaceDir, recursive: true);
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
