using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokAiDramaProductionMaterialServiceTests
{
    [Fact]
    public void NeedsSourceMaterialRefresh_RequiresTwelveFramesAndStoryboardImage()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-ai-drama-materials-{Guid.NewGuid():N}");
        var frames = Path.Combine(
            workflow,
            TikTokAiGenerationScreenshotService.OutputDirectoryName,
            TikTokAiGenerationScreenshotService.RetainedFramesDirectoryName);
        var storyboard = Path.Combine(workflow, TikTokAiGenerationScreenshotService.OutputDirectoryName);
        Directory.CreateDirectory(frames);
        try
        {
            for (var index = 1; index <= 11; index++)
                File.WriteAllText(Path.Combine(frames, $"frame-{index:D2}.jpg"), "frame");
            File.WriteAllText(Path.Combine(storyboard, "01_分镜工作台.png"), "storyboard");

            Assert.True(TikTokAiDramaProductionMaterialService.NeedsSourceMaterialRefresh(workflow));

            File.WriteAllText(Path.Combine(frames, "frame-12.jpg"), "frame");
            Assert.False(TikTokAiDramaProductionMaterialService.NeedsSourceMaterialRefresh(workflow));

            File.Delete(Path.Combine(storyboard, "01_分镜工作台.png"));
            Assert.True(TikTokAiDramaProductionMaterialService.NeedsSourceMaterialRefresh(workflow));
        }
        finally
        {
            if (Directory.Exists(workflow)) Directory.Delete(workflow, true);
        }
    }
}
