using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

[CollectionDefinition("Account AI data root", DisableParallelization = true)]
public sealed class AccountAiDataRootCollection { }

[Collection("Account AI data root")]
public sealed class TikTokAccountAiSettingsTests
{
    [Fact]
    public void Account_uses_local_model_and_keys_without_serializing_keys_into_the_profile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tiktok-account-ai-{Guid.NewGuid():N}");
        var previous = AppPaths.DataRootOverride;
        AppPaths.DataRootOverride = root;
        try
        {
            var defaults = new ClientSettings
            {
                AiTextEndpoint = "https://default.example/v1",
                AiTextApiKey = "default-text-key",
                ImageProvider = "ofox_image2",
                TiktokRoleVectorViewMode = "single",
                PosterTitleVerifyAiRetryCount = 3,
                ImageModelApiKey = "default-image-key",
                PosterAgentDirectPrompt = "legacy custom poster prompt",
                PosterAgentErasePrompt = "legacy custom erase prompt",
            };
            var preset = new TikTokAiTextPreset("preset", "Text", "https://other.example/v1", "account-model", 77);
            var account = new TikTokAccountProfile
            {
                Id = "a",
                AiOptions = TikTokAccountAiOptions.FromSettings(defaults) with
                {
                    TextPresetId = preset.Id,
                    PublishPrompt = "account prompt",
                },
            };
            TikTokAccountAiSettings.Save(account.Id,
                new(new(preset.Endpoint, "local-text-key"), new(defaults.ImageModelEndpoint, "local-image-key")),
                [preset], []);

            var effective = TikTokAccountAiSettings.Apply(defaults, account);
            effective.AiTextModel.Should().Be("account-model");
            effective.AiTextTimeoutSeconds.Should().Be(77);
            effective.AiTextApiKey.Should().Be("local-text-key");
            effective.ImageProvider.Should().Be("doubao");
            effective.TiktokRoleVectorViewMode.Should().Be("multi_angle");
            effective.PosterTitleVerifyAiRetryCount.Should().Be(1);
            effective.ImageModelApiKey.Should().Be("local-image-key");
            effective.AiPublishInfoPrompt.Should().Be("account prompt");
            JsonSerializer.Serialize(account).Should().NotContain("local-text-key").And.NotContain("local-image-key");
            effective.PosterAgentDirectPrompt.Should().Be(ClientSettingsDefaults.PosterAgentDirectPrompt);
            effective.PosterAgentErasePrompt.Should().Be(ClientSettingsDefaults.PosterAgentErasePrompt);

            var legacy = TikTokAccountAiSettings.Apply(defaults, new TikTokAccountProfile());
            legacy.AiTextApiKey.Should().Be(defaults.AiTextApiKey);
            legacy.ImageProvider.Should().Be("doubao");
            legacy.TiktokRoleVectorViewMode.Should().Be("multi_angle");
            legacy.PosterTitleVerifyAiRetryCount.Should().Be(1);
            legacy.PosterAgentDirectPrompt.Should().Be(ClientSettingsDefaults.PosterAgentDirectPrompt);
            defaults.PosterAgentDirectPrompt.Should().Be("legacy custom poster prompt");

            TikTokAccountAiSettings.Save("b", new(), [preset with { Endpoint = "https://changed.example/v1" }], []);
            TikTokAccountAiSettings.Apply(defaults, account).AiTextApiKey.Should().BeEmpty();
            account.AiOptions = account.AiOptions with { TextPresetId = "missing" };
            TikTokAccountAiSettings.Apply(defaults, account).AiTextApiKey.Should().BeEmpty();
        }
        finally
        {
            AppPaths.DataRootOverride = previous;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
