using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokProjectImageTemplateSelectionTests
{
    [Fact]
    public void Built_in_options_cover_templates_3_through_10_with_ui_labels_and_ids()
    {
        var expected = Enumerable.Range(3, 8)
            .Select(number => new
            {
                Id = $"image-template-project-image-{number}",
                Name = $"图片模板工程图{number}",
            })
            .ToArray();

        TikTokProjectImageTemplateCatalog.BuiltInOptions
            .Select(option => new { option.Id, option.Name })
            .Should().Equal(expected);
        TikTokProjectImageTemplateCatalog.BuiltInOptions
            .Should().OnlyContain(option =>
                option.SelectionLabel.Contains(option.Name, StringComparison.Ordinal) &&
                option.SelectionLabel.Contains(option.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_legacy_template_keeps_its_id_in_name_and_ui_label()
    {
        const string legacyId = "legacy-private-template";

        TikTokProjectImageTemplateCatalog.ResolveName(legacyId).Should().Be(legacyId);
        TikTokProjectImageTemplateCatalog.CreateSelectionLabel(legacyId)
            .Should().Contain(legacyId)
            .And.Contain("保留原值");
    }

    [Theory]
    [InlineData("image-template-project-image-4", "图片模板工程图4")]
    [InlineData("image-template-project-image-10", "图片模板工程图10")]
    [InlineData("legacy-private-template", "legacy-private-template")]
    public void Workflow_config_writes_the_selected_template_name(string templateId, string expectedName)
    {
        var path = ClientSettingsWorkflowConfigWriter.WriteTempConfig(new ClientSettings
        {
            TiktokProjectImageTemplateId = templateId,
        });

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            document.RootElement.GetProperty("ProjectImageTemplateId").GetString().Should().Be(templateId);
            document.RootElement.GetProperty("ProjectImageTemplateName").GetString().Should().Be(expectedName);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void Settings_store_preserves_an_unknown_non_blank_template_id()
    {
        WithTemporaryDirectory(tempDirectory =>
        {
            var databasePath = Path.Combine(tempDirectory, "app.db");
            ClientSettingsStore.Save(new ClientSettings
            {
                TiktokProjectImageTemplateId = "  legacy-private-template  ",
            }, databasePath);

            ClientSettingsStore.Load(databasePath).TiktokProjectImageTemplateId
                .Should().Be("legacy-private-template");
            SqliteConnection.ClearAllPools();
        });
    }

    [Fact]
    public void Template_resolution_prefers_the_explicit_root_for_the_selected_id()
    {
        WithTemporaryDirectory(tempDirectory =>
        {
            var explicitRoot = Path.Combine(tempDirectory, "explicit");
            var bundledRoot = Path.Combine(tempDirectory, "bundled");
            var explicitTemplate = CreateTemplate(explicitRoot, "explicit-ten", 10);
            CreateTemplate(bundledRoot, "bundled-ten", 10);

            var logs = new List<string>();
            var resolved = TikTokProjectImageService.ResolveTemplateDirectoryFromRoots(
                new ClientSettings
                {
                    TiktokProjectImageTemplateRoot = explicitRoot,
                    TiktokProjectImageTemplateId = "image-template-project-image-10",
                },
                bundledRoot,
                logs.Add);

            resolved.Should().Be(Path.GetFullPath(explicitTemplate));
            logs.Should().BeEmpty();
        });
    }

    [Fact]
    public void Template_resolution_falls_back_only_to_the_bundled_template_with_the_same_id()
    {
        WithTemporaryDirectory(tempDirectory =>
        {
            var explicitRoot = Path.Combine(tempDirectory, "explicit");
            var bundledRoot = Path.Combine(tempDirectory, "bundled");
            CreateTemplate(explicitRoot, "explicit-three", 3);
            var bundledTemplate = CreateTemplate(bundledRoot, "bundled-ten", 10);

            var logs = new List<string>();
            var resolved = TikTokProjectImageService.ResolveTemplateDirectoryFromRoots(
                new ClientSettings
                {
                    TiktokProjectImageTemplateRoot = explicitRoot,
                    TiktokProjectImageTemplateId = "image-template-project-image-10",
                },
                bundledRoot,
                logs.Add);

            resolved.Should().Be(Path.GetFullPath(bundledTemplate));
            logs.Should().ContainSingle(message => message.Contains("同 ID 模板", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Missing_selected_template_does_not_fall_back_to_template_3()
    {
        WithTemporaryDirectory(tempDirectory =>
        {
            var explicitRoot = Path.Combine(tempDirectory, "explicit");
            var bundledRoot = Path.Combine(tempDirectory, "bundled");
            CreateTemplate(explicitRoot, "explicit-three", 3);
            CreateTemplate(bundledRoot, "bundled-three", 3);

            var logs = new List<string>();
            var resolved = TikTokProjectImageService.ResolveTemplateDirectoryFromRoots(
                new ClientSettings
                {
                    TiktokProjectImageTemplateRoot = explicitRoot,
                    TiktokProjectImageTemplateId = "image-template-project-image-10",
                },
                bundledRoot,
                logs.Add);

            resolved.Should().BeEmpty();
            logs.Should().ContainSingle(message => message.Contains("不会回退到其他模板", StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData("template.json")]
    [InlineData("工程图_5.png")]
    public void Directory_fingerprint_includes_nested_json_and_png_files(string fileName)
    {
        WithTemporaryDirectory(tempDirectory =>
        {
            var nestedDirectory = Path.Combine(tempDirectory, "landscape", "pages");
            Directory.CreateDirectory(nestedDirectory);
            var resourcePath = Path.Combine(nestedDirectory, fileName);
            File.WriteAllBytes(resourcePath, [1, 2, 3, 4]);

            var before = TikTokProjectImageService.ComputeDirectoryFingerprint(tempDirectory);
            File.WriteAllBytes(resourcePath, [1, 2, 3, 4, 5]);
            File.SetLastWriteTimeUtc(resourcePath, DateTime.UtcNow.AddMinutes(1));
            var after = TikTokProjectImageService.ComputeDirectoryFingerprint(tempDirectory);

            after.Should().NotBe(before);
        });
    }

    [Fact]
    public void Directory_fingerprint_uses_relative_paths_for_nested_files()
    {
        WithTemporaryDirectory(tempDirectory =>
        {
            var firstRoot = Path.Combine(tempDirectory, "first");
            var secondRoot = Path.Combine(tempDirectory, "second");
            var firstPath = Path.Combine(firstRoot, "portrait", "template.json");
            var secondPath = Path.Combine(secondRoot, "landscape", "template.json");
            Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
            File.WriteAllText(firstPath, "{}");
            File.WriteAllText(secondPath, "{}");
            var timestamp = new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(firstPath, timestamp);
            File.SetLastWriteTimeUtc(secondPath, timestamp);

            TikTokProjectImageService.ComputeDirectoryFingerprint(firstRoot)
                .Should().NotBe(TikTokProjectImageService.ComputeDirectoryFingerprint(secondRoot));
        });
    }

    [Fact]
    public void Directory_fingerprint_detects_same_length_content_replacement_with_preserved_timestamp()
    {
        WithTemporaryDirectory(tempDirectory =>
        {
            var resourcePath = Path.Combine(tempDirectory, "template.json");
            var timestamp = new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc);
            File.WriteAllBytes(resourcePath, [1, 2, 3, 4]);
            File.SetLastWriteTimeUtc(resourcePath, timestamp);
            var before = TikTokProjectImageService.ComputeDirectoryFingerprint(tempDirectory);

            File.WriteAllBytes(resourcePath, [4, 3, 2, 1]);
            File.SetLastWriteTimeUtc(resourcePath, timestamp);
            var after = TikTokProjectImageService.ComputeDirectoryFingerprint(tempDirectory);

            after.Should().NotBe(before);
        });
    }

    private static string CreateTemplate(string root, string directoryName, int number)
    {
        var directory = Path.Combine(root, directoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "template.json"),
            JsonSerializer.Serialize(new
            {
                id = $"image-template-project-image-{number}",
                name = $"图片模板工程图{number}",
                count = 4,
                templates = Array.Empty<object>(),
            }));
        return directory;
    }

    private static void WithTemporaryDirectory(Action<string> assertion)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"tiktok-template-selection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            assertion(tempDirectory);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
