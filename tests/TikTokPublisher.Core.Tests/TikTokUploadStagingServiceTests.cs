using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokUploadStagingServiceTests
{
    [Fact]
    public void PrepareUploadFiles_Synchronizes_Reused_Files_To_Current_Title()
    {
        var root = Path.Combine(Path.GetTempPath(), $"upload-staging-{Guid.NewGuid():N}");
        var staging = Path.Combine(root, TikTokUploadStagingService.StagingDirName);
        Directory.CreateDirectory(staging);
        var first = Path.Combine(staging, "旧剧名-第1集.mp4");
        var second = Path.Combine(staging, "旧剧名-第2集.mp4");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [2]);

        try
        {
            var result = TikTokUploadStagingService.PrepareUploadFiles(
                root,
                "当前新剧名",
                [first, second],
                rebuildStaging: false,
                repairSmallVideos: false,
                log: null);

            result.Should().BeEquivalentTo(
                Path.Combine(staging, "当前新剧名-第1集.mp4"),
                Path.Combine(staging, "当前新剧名-第2集.mp4"));
            File.Exists(first).Should().BeFalse();
            File.Exists(second).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
