using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokAiDramaProductionMaterialServiceTests
{
    [Fact]
    public void MinimumSourcePolicy_UsesTwelveFramesAndAtMostThreeEpisodes()
    {
        TikTokAiDramaProductionMaterialService.RequiredSourceFrameCount.Should().Be(12);
        TikTokAiDramaProductionMaterialService.MaxSourceSupplementEpisodeCount.Should().Be(3);
    }

    [Fact]
    public void VisualEvidenceCache_RequiresRequestedRetainedFrameCount()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-visual-evidence-{Guid.NewGuid():N}");
        var output = TikTokAiGenerationScreenshotService.GetOutputDirectory(workflow);
        var retained = TikTokAiGenerationScreenshotService.GetRetainedFramesDirectory(workflow);
        Directory.CreateDirectory(retained);
        try
        {
            for (var index = 1; index <= TikTokAiGenerationScreenshotService.RequiredImageCount; index++)
                File.WriteAllText(Path.Combine(output, $"{index:D2}_分镜工作台.png"), "storyboard");
            File.WriteAllText(
                TikTokAiGenerationScreenshotService.GetRetainedFramesManifestPath(workflow),
                "{}");

            TikTokVisualEvidencePreparationService.HasCurrentOutput(workflow, 0).Should().BeTrue();
            TikTokVisualEvidencePreparationService.HasCurrentOutput(workflow, 12).Should().BeFalse();

            for (var index = 1; index <= 12; index++)
                File.WriteAllText(Path.Combine(retained, $"frame-{index:D2}.jpg"), "frame");

            TikTokVisualEvidencePreparationService.HasCurrentOutput(workflow, 12).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(workflow)) Directory.Delete(workflow, true);
        }
    }

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
