using System.Text.Json;
using FluentAssertions;
using SixLabors.ImageSharp;

namespace TikTokPublisher.Core.Tests;

public sealed class ProjectImageTemplateBundlingTests
{
    [Fact]
    public void Build_output_contains_templates_3_through_10_and_all_manifest_pages()
    {
        var templateRoot = Path.Combine(AppContext.BaseDirectory, "templates", "project-image");
        var expectedVersions = new Dictionary<int, long>
        {
            [3] = 2026060104,
            [4] = 2026060103,
            [5] = 2026060103,
            [6] = 2026060103,
            [7] = 2026073102,
            [8] = 2026080101,
            [9] = 2026080504,
            [10] = 2026080603,
        };

        foreach (var number in Enumerable.Range(3, 8))
        {
            var templateDirectory = Path.Combine(templateRoot, $"image_template_project_image_{number}");
            var rootManifestPath = Path.Combine(templateDirectory, "template.json");
            File.Exists(rootManifestPath).Should().BeTrue($"template {number} must publish its root manifest");

            using (var rootManifest = JsonDocument.Parse(File.ReadAllText(rootManifestPath)))
            {
                rootManifest.RootElement.GetProperty("id").GetString()
                    .Should().Be($"image-template-project-image-{number}");
                rootManifest.RootElement.GetProperty("asset_version").GetInt64()
                    .Should().Be(expectedVersions[number]);
                rootManifest.RootElement.GetProperty("count").GetInt32().Should().Be(4);
                rootManifest.RootElement.GetProperty("templates").GetArrayLength().Should().Be(4);

                var extractDialogue = rootManifest.RootElement.TryGetProperty("extract_dialogue", out var extractElement)
                    ? extractElement.GetBoolean()
                    : true;
                extractDialogue.Should().Be(number < 8);
                var timelineOverlay = rootManifest.RootElement.TryGetProperty("render_timeline_overlay", out var overlayElement) &&
                                      overlayElement.GetBoolean();
                timelineOverlay.Should().Be(number == 5);
            }

            var templateBoundary = Path.GetFullPath(templateDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var manifests = Directory.EnumerateFiles(
                    templateDirectory,
                    "template.json",
                    SearchOption.AllDirectories)
                .ToArray();
            manifests.Should().NotBeEmpty();

            foreach (var manifestPath in manifests)
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var pages = manifest.RootElement.GetProperty("templates").EnumerateArray().ToArray();
                pages.Should().HaveCount(4, $"{manifestPath} must declare the four screenshot pages");

                foreach (var page in pages)
                {
                    var relativePagePath = page.GetProperty("file").GetString();
                    relativePagePath.Should().NotBeNullOrWhiteSpace();
                    var pagePath = Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(manifestPath)!,
                        relativePagePath!));
                    pagePath.StartsWith(templateBoundary, StringComparison.OrdinalIgnoreCase)
                        .Should().BeTrue($"{manifestPath} must only reference self-contained assets");
                    File.Exists(pagePath).Should().BeTrue($"manifest page must be published: {pagePath}");
                    new FileInfo(pagePath).Length.Should().BeGreaterThan(0);
                    var imageInfo = Image.Identify(pagePath);
                    imageInfo.Should().NotBeNull();
                    imageInfo!.Width.Should().Be(1920);
                    imageInfo.Height.Should().BeOneOf(1032, 1080);
                }
            }

            if (number is 6 or 7)
            {
                File.Exists(Path.Combine(templateDirectory, "landscape", "template.json"))
                    .Should().BeTrue($"template {number} must publish its landscape variant");
            }
        }
    }
}
