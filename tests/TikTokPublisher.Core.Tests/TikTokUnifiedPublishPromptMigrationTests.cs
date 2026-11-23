using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

[Collection("Account AI data root")]
public sealed class TikTokUnifiedPublishPromptMigrationTests
{
    private const string OldSystemPrompt = "旧的系统提示词";
    private const string OldAccountPrompt = "旧的账号提示词";

    [Fact]
    public void First_launch_resets_system_and_account_prompts_and_preserves_later_edits()
    {
        var previousRoot = AppPaths.DataRootOverride;
        var dataRoot = Directory.CreateTempSubdirectory("tiktok-prompt-migration-");
        AppPaths.DataRootOverride = dataRoot.FullName;
        try
        {
            WriteLegacySettings();
            File.WriteAllText(AppPaths.AccountsFile, """
                [
                  {"id":"a","name":"账号 A","aiOptions":{"textPresetId":"text-a","imagePresetId":"image-a","publishPrompt":"旧的账号提示词","posterMode":"original","verifyTitle":true},"futureAccountField":{"value":7}},
                  {"id":"b","name":"账号 B","aiOptions":null},
                  {"id":"c","name":"账号 C"}
                ]
                """);
            var originalAccounts = ReadAccounts();
            var originalSettings = ReadSettings();

            TikTokUnifiedPublishPromptMigration.ApplyOnce();

            var expectedAccounts = (JsonArray)originalAccounts.DeepClone();
            expectedAccounts[0]!["aiOptions"]!["publishPrompt"] = "";
            JsonNode.DeepEquals(ReadAccounts(), expectedAccounts).Should().BeTrue();
            var expectedSettings = (JsonObject)originalSettings.DeepClone();
            expectedSettings["ai_publish_info_prompt"] = ClientSettingsDefaults.TikTokUnifiedPublishInfoPrompt;
            JsonNode.DeepEquals(ReadSettings(), expectedSettings).Should().BeTrue();

            var account = JsonSerializer.Deserialize<TikTokAccountProfile>(ReadAccounts()[0]!, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            TikTokAccountAiSettings.Apply(ClientSettingsStore.Load(), account).AiPublishInfoPrompt
                .Should().Be(ClientSettingsDefaults.TikTokUnifiedPublishInfoPrompt);

            expectedAccounts[0]!["aiOptions"]!["publishPrompt"] = "用户重新编辑的账号提示词";
            expectedSettings["ai_publish_info_prompt"] = "用户重新编辑的系统提示词";
            File.WriteAllText(AppPaths.AccountsFile, expectedAccounts.ToJsonString());
            AppSettingStore.SaveJson(ClientSettingsStore.SettingsKey, expectedSettings);

            TikTokUnifiedPublishPromptMigration.ApplyOnce();

            JsonNode.DeepEquals(ReadAccounts(), expectedAccounts).Should().BeTrue();
            JsonNode.DeepEquals(ReadSettings(), expectedSettings).Should().BeTrue();
        }
        finally
        {
            AppPaths.DataRootOverride = previousRoot;
            SqliteConnection.ClearAllPools();
            dataRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_fresh_install_saves_the_unified_system_default_before_any_account_exists()
    {
        var previousRoot = AppPaths.DataRootOverride;
        var root = Directory.CreateTempSubdirectory("tiktok-prompt-migration-");
        AppPaths.DataRootOverride = root.FullName;
        try
        {
            TikTokUnifiedPublishPromptMigration.ApplyOnce();

            File.Exists(AppPaths.AccountsFile).Should().BeFalse();
            var settings = ClientSettingsStore.Load();
            settings.AiPublishInfoPrompt.Should().Be(ClientSettingsDefaults.TikTokUnifiedPublishInfoPrompt);
            TikTokAccountAiOptions.FromSettings(settings).PublishPrompt.Should().Be(settings.AiPublishInfoPrompt);
        }
        finally
        {
            AppPaths.DataRootOverride = previousRoot;
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_different_data_root_resets_independently()
    {
        var previousRoot = AppPaths.DataRootOverride;
        var root = Directory.CreateTempSubdirectory("tiktok-prompt-migration-");
        try
        {
            foreach (var profile in new[] { "first-install", "second-install" })
            {
                AppPaths.DataRootOverride = root.CreateSubdirectory(profile).FullName;
                WriteLegacySettings();
                WriteLegacyAccount();

                TikTokUnifiedPublishPromptMigration.ApplyOnce();

                ReadSettings()["ai_publish_info_prompt"]!.GetValue<string>()
                    .Should().Be(ClientSettingsDefaults.TikTokUnifiedPublishInfoPrompt);
                ReadAccounts()[0]!["aiOptions"]!["publishPrompt"]!.GetValue<string>().Should().BeEmpty();
            }
        }
        finally
        {
            AppPaths.DataRootOverride = previousRoot;
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_corrupt_account_file_does_not_prevent_retry_after_recovery()
    {
        var previousRoot = AppPaths.DataRootOverride;
        var root = Directory.CreateTempSubdirectory("tiktok-prompt-migration-");
        AppPaths.DataRootOverride = root.FullName;
        try
        {
            WriteLegacySettings();
            const string invalidJson = "not valid JSON";
            File.WriteAllText(AppPaths.AccountsFile, invalidJson);

            Record.Exception(() => TikTokUnifiedPublishPromptMigration.ApplyOnce()).Should().BeAssignableTo<JsonException>();

            File.ReadAllText(AppPaths.AccountsFile).Should().Be(invalidJson);
            ReadSettings()["ai_publish_info_prompt"]!.GetValue<string>().Should().Be(OldSystemPrompt);
            WriteLegacyAccount();
            TikTokUnifiedPublishPromptMigration.ApplyOnce();

            ReadAccounts()[0]!["aiOptions"]!["publishPrompt"]!.GetValue<string>().Should().BeEmpty();
            ReadSettings()["ai_publish_info_prompt"]!.GetValue<string>()
                .Should().Be(ClientSettingsDefaults.TikTokUnifiedPublishInfoPrompt);
        }
        finally
        {
            AppPaths.DataRootOverride = previousRoot;
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    private static void WriteLegacySettings() => AppSettingStore.SaveJson(ClientSettingsStore.SettingsKey, new JsonObject
    {
        ["ai_publish_info_prompt"] = OldSystemPrompt,
        ["ai_full_info_system_prompt"] = "为旧版工作流保留的提示词",
        ["ai_text_model"] = "custom-text-model",
        ["future_setting"] = new JsonObject { ["enabled"] = true },
    });

    private static void WriteLegacyAccount() => File.WriteAllText(AppPaths.AccountsFile, new JsonArray
    {
        new JsonObject
        {
            ["id"] = "a",
            ["aiOptions"] = new JsonObject { ["publishPrompt"] = OldAccountPrompt },
        },
    }.ToJsonString());

    private static JsonArray ReadAccounts() => JsonNode.Parse(File.ReadAllText(AppPaths.AccountsFile))!.AsArray();

    private static JsonObject ReadSettings()
    {
        AppSettingStore.TryLoadJson<JsonObject>(ClientSettingsStore.SettingsKey, out var settings).Should().BeTrue();
        return settings!;
    }
}
