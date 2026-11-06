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

    private static string CreateProject(string metadata)
    {
        var root = Path.Combine(Path.GetTempPath(), "kuaishou-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "shortdrama-project.json"), metadata);
        return root;
    }
}
