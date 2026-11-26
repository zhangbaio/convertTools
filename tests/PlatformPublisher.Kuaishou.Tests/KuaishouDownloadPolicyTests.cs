using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouDownloadPolicyTests
{
    [Fact]
    public void Local_import_with_videos_skips_download()
    {
        var root = CreateProject("""{"localImported":true,"importMode":"local","downloadDisabled":true}""");
        try
        {
            File.WriteAllBytes(Path.Combine(root, "第1集.mp4"), [1, 2, 3]);
            Assert.True(KuaishouDownloadPolicy.ShouldSkip(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Local_import_with_book_id_but_no_videos_still_downloads()
    {
        var root = CreateProject("""{"localImported":true,"bookId":"123"}""");
        try
        {
            Assert.False(KuaishouDownloadPolicy.ShouldSkip(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Remote_project_does_not_skip_download()
    {
        var root = CreateProject("""{"bookId":"8899","remoteDownloadPending":true}""");
        try
        {
            Assert.False(KuaishouDownloadPolicy.ShouldSkip(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(KuaishouSettlementEpisodeVideoService.DedupWritingName)]
    [InlineData(KuaishouSettlementEpisodeVideoService.DedupManifestName)]
    public void Playback_check_prefers_source_episodes_over_workflow_copies(string? marker)
    {
        var root = Directory.CreateTempSubdirectory("kuaishou-playback-source-");
        try
        {
            var source = root.CreateSubdirectory("source");
            var sourceVideos = source.CreateSubdirectory("videos");
            var workflow = root.CreateSubdirectory("workflow");
            var workflowVideos = workflow.CreateSubdirectory("videos");
            var original = Path.Combine(source.FullName, "第16集.mp4");
            var nestedOriginal = Path.Combine(sourceVideos.FullName, "第17集.m4v");
            File.WriteAllBytes(original, [1]);
            File.WriteAllBytes(nestedOriginal, [1]);
            File.WriteAllBytes(Path.Combine(workflowVideos.FullName, "第16集.mp4"), [0]);
            if (marker is not null)
                File.WriteAllText(Path.Combine(workflowVideos.FullName, marker), "{}");

            Assert.Equal(new[] { original, nestedOriginal },
                KuaishouDownloadPolicy.ListVideosForPlaybackCheck(source.FullName, workflow.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(true, KuaishouSettlementEpisodeVideoService.DedupWritingName)]
    [InlineData(false, KuaishouSettlementEpisodeVideoService.DedupWritingName)]
    [InlineData(false, KuaishouSettlementEpisodeVideoService.DedupManifestName)]
    [InlineData(false, null)]
    public void Playback_check_can_find_moved_originals_but_excludes_managed_dedup_batches(bool archived, string? marker)
    {
        var root = Directory.CreateTempSubdirectory("kuaishou-playback-moved-");
        try
        {
            var source = root.CreateSubdirectory("source");
            var workflow = root.CreateSubdirectory("workflow");
            var prepared = Path.Combine(workflow.CreateSubdirectory("videos").FullName, "第16集.mp4");
            var original = Path.Combine(workflow.CreateSubdirectory("原片").FullName, "第16集.mp4");
            File.WriteAllBytes(prepared, [1]);
            if (archived)
                File.WriteAllBytes(original, [1]);
            if (marker is not null)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(prepared)!, marker), "{}");

            var expected = archived ? new[] { original } : marker is null ? new[] { prepared } : [];
            Assert.Equal(expected, KuaishouDownloadPolicy.ListVideosForPlaybackCheck(source.FullName, workflow.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static string CreateProject(string metadata)
    {
        var root = Path.Combine(Path.GetTempPath(), "kuaishou-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "shortdrama-project.json"), metadata);
        return root;
    }
}
