using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class ClientSettingsProjectImageSettingsTests
{
    [Fact]
    public void Project_image_fablecut_settings_use_expected_defaults_and_clone_values()
    {
        var defaults = new ClientSettings();
        defaults.TiktokProjectImageFableCutRoot.Should().BeEmpty();
        defaults.TiktokProjectImageFableCutClipCount.Should().Be(24);

        var settings = new ClientSettings
        {
            TiktokProjectImageGenerationMode = "fablecut",
            TiktokProjectImageFableCutRoot = @"D:\tools\FableCut",
            TiktokProjectImageFableCutClipCount = 30,
        };

        var clone = settings.Clone();

        clone.TiktokProjectImageGenerationMode.Should().Be("fablecut");
        clone.TiktokProjectImageFableCutRoot.Should().Be(@"D:\tools\FableCut");
        clone.TiktokProjectImageFableCutClipCount.Should().Be(30);
    }

    [Theory]
    [InlineData("fablecut", "fablecut")]
    [InlineData(" FABLECUT_EDITOR ", "fablecut")]
    [InlineData("image_template", "image_template")]
    [InlineData(" IMAGE_TEMPLATES ", "image_template")]
    [InlineData("image_template_overlay", "image_template")]
    [InlineData("unknown", "image_template")]
    [InlineData(" ", "image_template")]
    public void Store_normalizes_project_image_generation_mode(string input, string expected)
    {
        WithTemporaryDatabase(databasePath =>
        {
            ClientSettingsStore.Save(new ClientSettings
            {
                TiktokProjectImageGenerationMode = input,
            }, databasePath);

            ClientSettingsStore.Load(databasePath).TiktokProjectImageGenerationMode.Should().Be(expected);
        });
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(1, 12)]
    [InlineData(18, 18)]
    [InlineData(48, 36)]
    public void Store_trims_fablecut_root_and_clamps_clip_count(int input, int expected)
    {
        WithTemporaryDatabase(databasePath =>
        {
            ClientSettingsStore.Save(new ClientSettings
            {
                TiktokProjectImageFableCutRoot = @"  D:\tools\FableCut  ",
                TiktokProjectImageFableCutClipCount = input,
            }, databasePath);

            var loaded = ClientSettingsStore.Load(databasePath);
            loaded.TiktokProjectImageFableCutRoot.Should().Be(@"D:\tools\FableCut");
            loaded.TiktokProjectImageFableCutClipCount.Should().Be(expected);
        });
    }

    [Fact]
    public void Workflow_config_contains_fablecut_project_image_settings()
    {
        var settings = new ClientSettings
        {
            TiktokProjectImageGenerationMode = "fablecut",
            TiktokProjectImageFableCutRoot = @"D:\tools\FableCut",
            TiktokProjectImageFableCutClipCount = 30,
        };
        var path = ClientSettingsWorkflowConfigWriter.WriteTempConfig(settings);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            root.GetProperty("ProjectImageGenerationMode").GetString().Should().Be("fablecut");
            root.GetProperty("ProjectImageFableCutRoot").GetString().Should().Be(@"D:\tools\FableCut");
            root.GetProperty("ProjectImageFableCutClipCount").GetInt32().Should().Be(30);
            root.GetProperty("ProjectImageFableCutScreenshotStyle").GetString().Should().Be("standard");
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { }
        }
    }

    private static void WithTemporaryDatabase(Action<string> assertion)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"client-settings-project-image-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(tempDir, "app.db");
        Directory.CreateDirectory(tempDir);

        try
        {
            assertion(databasePath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); }
            catch (IOException) { }
        }
    }
}
