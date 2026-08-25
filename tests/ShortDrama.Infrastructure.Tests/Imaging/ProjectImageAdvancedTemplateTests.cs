using FluentAssertions;
using ShortDrama.Infrastructure.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Imaging;

public sealed class ProjectImageAdvancedTemplateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"project-image-advanced-template-{Guid.NewGuid():N}");

    public ProjectImageAdvancedTemplateTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Manifest_reads_asset_version_and_extract_dialogue_switch()
    {
        File.WriteAllText(
            Path.Combine(_root, "template.json"),
            """
            {
              "id": "image-template-project-image-10",
              "name": "template-10",
              "asset_version": 2026080603,
              "extract_dialogue": false,
              "count": 1,
              "templates": [
                {
                  "file": "template1.png",
                  "regions": {
                    "video_track_images": { "x": 1, "y": 2, "width": 30, "height": 4 }
                  }
                }
              ]
            }
            """);

        var manifest = ProjectImageTemplateManifest.Load(_root);

        manifest.AssetVersion.Should().Be(2026080603);
        manifest.ExtractDialogue.Should().BeFalse();
    }

    [Fact]
    public void Resolve_oriented_template_directory_uses_landscape_manifest_for_landscape_video()
    {
        var landscape = Path.Combine(_root, "landscape");
        Directory.CreateDirectory(landscape);
        File.WriteAllText(Path.Combine(landscape, "template.json"), "{}");

        ProjectImageGenerator.ResolveOrientedTemplateDirectory(_root, portrait: false)
            .Should().Be(Path.GetFullPath(landscape));
        ProjectImageGenerator.ResolveOrientedTemplateDirectory(_root, portrait: true)
            .Should().Be(Path.GetFullPath(_root));
    }

    [Fact]
    public void Pad_screenshot_compat_image_keeps_top_pixels_and_extends_1032_to_1080()
    {
        var top = new Rgba32(12, 34, 56, 255);
        var bottom = new Rgba32(78, 90, 123, 255);
        using var source = new Image<Rgba32>(1920, 1032, top);
        source[960, 1031] = bottom;

        using var padded = ProjectImageGenerator.PadScreenshotCompatImage(source);

        padded.Width.Should().Be(1920);
        padded.Height.Should().Be(1080);
        padded[0, 0].Should().Be(top);
        padded[960, 1031].Should().Be(bottom);
        padded[960, 1079].Should().Be(bottom);
    }

    [Fact]
    public void Full_episode_sampling_spans_the_video_in_natural_order()
    {
        var samples = ProjectImageGenerator.BuildFullEpisodeSampleTimes(100, 8);

        samples.Should().HaveCount(8);
        samples.Should().BeInAscendingOrder();
        samples[0].Should().BeApproximately(100d / 9d, 0.001);
        samples[^1].Should().BeApproximately(800d / 9d, 0.001);
    }

    [Theory]
    [InlineData(80, 80, 40, 0.1)]
    [InlineData(0.05, 0.1, 0.1, 0.1)]
    [InlineData(double.NaN, 0.1, 0.1, 0.1)]
    public void Frame_extraction_retries_at_earlier_safe_times(
        double requested,
        double first,
        double second,
        double third)
    {
        var attempts = ProjectImageGenerator.BuildFrameExtractionAttemptTimes(requested);

        attempts.Should().Equal(first, second, third);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup on Windows.
        }
    }
}
