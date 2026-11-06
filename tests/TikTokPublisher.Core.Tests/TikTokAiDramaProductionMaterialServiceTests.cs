using System.Text.Json;
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
        var retained = TikTokAiGenerationScreenshotService.GetRetainedFramesDirectory(workflow);
        Directory.CreateDirectory(retained);
        try
        {
            foreach (var path in TikTokAiGenerationScreenshotService.GetExpectedOutputPaths(workflow))
                File.WriteAllText(path, "storyboard");
            WriteRetainedFramesManifest(workflow, 0);

            TikTokVisualEvidencePreparationService.HasCurrentOutput(workflow, 0).Should().BeFalse();

            File.WriteAllText(Path.Combine(retained, "frame-01.jpg"), "frame");
            WriteRetainedFramesManifest(workflow, 1);
            TikTokVisualEvidencePreparationService.HasCurrentOutput(workflow, 0).Should().BeTrue();
            TikTokVisualEvidencePreparationService.HasCurrentOutput(workflow, 12).Should().BeFalse();

            for (var index = 2; index <= 12; index++)
                File.WriteAllText(Path.Combine(retained, $"frame-{index:D2}.jpg"), "frame");
            WriteRetainedFramesManifest(workflow, 12);

            TikTokVisualEvidencePreparationService.HasCurrentOutput(workflow, 12).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(workflow)) Directory.Delete(workflow, true);
        }
    }

    private static void WriteRetainedFramesManifest(string workflow, int frameCount)
    {
        var frames = Enumerable.Range(1, frameCount)
            .Select(index => new { file = $"frame-{index:D2}.jpg" })
            .ToArray();
        File.WriteAllText(
            TikTokAiGenerationScreenshotService.GetRetainedFramesManifestPath(workflow),
            JsonSerializer.Serialize(new
            {
                version = TikTokAiGenerationScreenshotService.RetainedFramesVersion,
                screenshot_version = TikTokAiGenerationScreenshotService.ScreenshotVersion,
                frame_count = frameCount,
                frames,
            }));
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
