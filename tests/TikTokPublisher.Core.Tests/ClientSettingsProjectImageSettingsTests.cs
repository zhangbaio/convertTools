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
    public void Saving_settings_removes_legacy_fablecut_options_and_preserves_the_template()
    {
        WithTemporaryDatabase(databasePath =>
        {
            AppSettingStore.SaveJson(
                ClientSettingsStore.SettingsKey,
                new Dictionary<string, string>
                {
                    ["tiktok_project_image_generation_mode"] = "fablecut",
                    ["tiktok_project_image_fable_cut_root"] = "old-editor-path",
                    ["tiktok_project_image_fable_cut_clip_count"] = "30",
                    ["tiktok_project_image_template_id"] = "image_template_project_image_13",
                },
                databasePath);

            var settings = ClientSettingsStore.Load(databasePath);
            settings.TiktokProjectImageTemplateId.Should().Be("image_template_project_image_13");
            ClientSettingsStore.Save(settings, databasePath);

            AppSettingStore.TryLoadJson<Dictionary<string, JsonElement>>(
                ClientSettingsStore.SettingsKey, out var saved, databasePath).Should().BeTrue();
            saved!.Should().NotContainKey("tiktok_project_image_generation_mode");
            saved.Should().NotContainKey("tiktok_project_image_fable_cut_root");
            saved.Should().NotContainKey("tiktok_project_image_fable_cut_clip_count");
        });
    }

    [Fact]
    public void Workflow_config_contains_image_template_settings()
    {
        var settings = new ClientSettings
        {
            TiktokProjectImageTemplateId = "image_template_project_image_13",
            TiktokProjectImageSubtitleAiMode = "off",
        };
        var path = ClientSettingsWorkflowConfigWriter.WriteTempConfig(settings);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            root.GetProperty("ProjectImageTemplateId").GetString().Should().Be(settings.TiktokProjectImageTemplateId);
            root.GetProperty("ProjectImageSubtitleAiMode").GetString().Should().Be("off");
            root.TryGetProperty("ProjectImageGenerationMode", out _).Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
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
