using FluentAssertions;
using ShortDrama.Infrastructure.Imaging;
using System.Text.Json;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Imaging;

public sealed class ProjectImageTemplateCatalogTests
{
    [Fact]
    public void ResolveTemplateDirectory_should_fall_back_to_project_default_root_when_configured_paths_are_invalid()
    {
        using var tempDir = new TempDir();
        var projectRoot = tempDir.Path;
        var templateDir = CreateTemplatePackage(projectRoot, "image-template-project-image-3");

        var resolved = ProjectImageTemplateCatalog.ResolveTemplateDirectory(
            Path.Combine(projectRoot, "missing-root"),
            "image-template-project-image-3",
            Path.Combine(projectRoot, "missing-dir"),
            projectRoot);

        resolved.Should().Be(templateDir);
    }

    [Fact]
    public void ResolveTemplateRoot_should_ignore_stale_configured_root_and_fall_back_to_default_root()
    {
        using var tempDir = new TempDir();
        var projectRoot = tempDir.Path;
        var templateDir = CreateTemplatePackage(projectRoot, "image-template-project-image-5");

        var resolved = ProjectImageTemplateCatalog.ResolveTemplateRoot(
            Path.Combine(projectRoot, "stale-root"),
            string.Empty,
            projectRoot);

        resolved.Should().Be(Path.GetDirectoryName(templateDir));
    }

    private static string CreateTemplatePackage(string projectRoot, string templateId)
    {
        var root = Path.Combine(projectRoot, "templates", "project-image");
        var templateDir = Path.Combine(root, templateId);
        Directory.CreateDirectory(templateDir);

        var payload = new
        {
            id = templateId,
            name = templateId,
            count = 1,
            templates = new[]
            {
                new
                {
                    file = "工程图1.png",
                    regions = new
                    {
                        player = new { x = 0, y = 0, width = 1, height = 1 },
                        material_panel = new { x = 0, y = 0, width = 1, height = 1 },
                        timeline_strip = new { x = 0, y = 0, width = 1, height = 1 }
                    }
                }
            }
        };

        File.WriteAllText(
            Path.Combine(templateDir, "template.json"),
            JsonSerializer.Serialize(payload),
            System.Text.Encoding.UTF8);

        return templateDir;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"shortdrama-template-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }
            }
            catch
            {
                // Ignore cleanup failures in tests.
            }
        }
    }
}
