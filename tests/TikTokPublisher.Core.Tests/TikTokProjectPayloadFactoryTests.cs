using FluentAssertions;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokProjectPayloadFactoryTests
{
    [Fact]
    public void BuildFromPublishItem_UsesFinalizedStagingCountInsteadOfStaleDeclaredCount()
    {
        var workspaceDir = Path.Combine(Path.GetTempPath(), $"tiktok-payload-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspaceDir, "source");
        var workflowDir = Path.Combine(workspaceDir, "workflow", "source");
        var stagingDir = Path.Combine(workflowDir, TikTokUploadStagingService.StagingDirName);
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(stagingDir);

        try
        {
            File.WriteAllText(Path.Combine(sourceDir, "短剧信息.txt"), "剧名: 测试短剧\n集数: 109\n");
            File.WriteAllText(Path.Combine(workflowDir, "短剧信息.txt"), "新剧名: 测试新剧名\n集数: 109\n");
            for (var episode = 1; episode <= 108; episode++)
            {
                File.WriteAllBytes(
                    Path.Combine(stagingDir, $"测试新剧名-第{episode}集.mp4"),
                    [1]);
            }

            var payload = TikTokProjectPayloadFactory.BuildFromPublishItem(new PublishItem
            {
                ProjectDir = sourceDir,
                EpisodeCount = 109,
            });

            payload.EpisodeCount.Should().Be(108);
        }
        finally
        {
            try { Directory.Delete(workspaceDir, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void BuildFromPublishItem_UsesDeclaredCountWhenNoFinalizedStagingSetExists()
    {
        var workspaceDir = Path.Combine(Path.GetTempPath(), $"tiktok-payload-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspaceDir, "source");
        Directory.CreateDirectory(sourceDir);

        try
        {
            File.WriteAllText(Path.Combine(sourceDir, "短剧信息.txt"), "剧名: 测试短剧\n集数: 109\n");
            File.WriteAllBytes(Path.Combine(sourceDir, "测试短剧-第1集.mp4"), [1]);

            var payload = TikTokProjectPayloadFactory.BuildFromPublishItem(new PublishItem
            {
                ProjectDir = sourceDir,
                EpisodeCount = 108,
            });

            payload.EpisodeCount.Should().Be(109);
        }
        finally
        {
            try { Directory.Delete(workspaceDir, recursive: true); }
            catch (IOException) { }
        }
    }
}
