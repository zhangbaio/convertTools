using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokPublishDefaultsTests
{
    [Fact]
    public void Client_settings_ai_defaults_include_non_key_values_only()
    {
        var settings = new ClientSettings();

        settings.AiTextEndpoint.Should().Be(ClientSettingsDefaults.AiTextEndpoint);
        settings.AiTextModel.Should().Be(ClientSettingsDefaults.AiTextModel);
        settings.AiTextTimeoutSeconds.Should().Be(ClientSettingsDefaults.AiTextTimeoutSeconds);
        settings.AiTextMaxBatchSize.Should().Be(ClientSettingsDefaults.AiTextMaxBatchSize);
        settings.AiTextApiKey.Should().BeEmpty();

        settings.ImageProvider.Should().Be(ClientSettingsDefaults.ImageProvider);
        settings.ImageModelId.Should().Be(ClientSettingsDefaults.ImageModelId);
        settings.ImageModelEndpoint.Should().Be(ClientSettingsDefaults.ImageModelEndpoint);
        settings.ImageModelApiKey.Should().BeEmpty();
        settings.OfoxImage2ModelId.Should().Be(ClientSettingsDefaults.OfoxImage2ModelId);
        settings.OfoxImage2Endpoint.Should().Be(ClientSettingsDefaults.OfoxImage2Endpoint);
        settings.OfoxImage2Quality.Should().Be(ClientSettingsDefaults.OfoxImage2Quality);
        settings.OfoxImage2Size.Should().Be(ClientSettingsDefaults.OfoxImage2Size);
        settings.OfoxImage2ApiKey.Should().BeEmpty();
        settings.PosterTitleVerifyEnabled.Should().Be(ClientSettingsDefaults.PosterTitleVerifyEnabled);
        settings.PosterTitleVerifyMode.Should().Be(ClientSettingsDefaults.PosterTitleVerifyMode);
        settings.TiktokAllowOverLimitUploadImport.Should().Be(ClientSettingsDefaults.TiktokAllowOverLimitUploadImport);
        settings.TiktokOverLimitDownloadEpisodeCount.Should().Be(ClientSettingsDefaults.TiktokOverLimitDownloadEpisodeCount);
    }

    [Fact]
    public void Client_settings_store_normalizes_blank_ai_non_key_values_to_defaults()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"client-settings-{Guid.NewGuid():N}.db");
        try
        {
            ClientSettingsStore.Save(new ClientSettings
            {
                AiTextEndpoint = "",
                AiTextApiKey = "",
                AiTextModel = "",
                AiTextTimeoutSeconds = 0,
                AiTextMaxBatchSize = 0,
                PosterMode = "",
                ImageProvider = "",
                ImageModelId = "",
                ImageModelApiKey = "",
                ImageModelEndpoint = "",
                DoubaoImageResolution = "",
                DoubaoImageRatio = "",
                OfoxImage2ModelId = "",
                OfoxImage2ApiKey = "",
                OfoxImage2Endpoint = "",
                OfoxImage2Quality = "",
                OfoxImage2Size = "",
                PosterTitleVerifyMode = "",
                TiktokOverLimitDownloadEpisodeCount = 0,
            }, databasePath);

            var loaded = ClientSettingsStore.Load(databasePath);

            loaded.AiTextEndpoint.Should().Be(ClientSettingsDefaults.AiTextEndpoint);
            loaded.AiTextApiKey.Should().BeEmpty();
            loaded.AiTextModel.Should().Be(ClientSettingsDefaults.AiTextModel);
            loaded.AiTextTimeoutSeconds.Should().Be(ClientSettingsDefaults.AiTextTimeoutSeconds);
            loaded.AiTextMaxBatchSize.Should().Be(ClientSettingsDefaults.AiTextMaxBatchSize);
            loaded.PosterMode.Should().Be(ClientSettingsDefaults.PosterMode);
            loaded.ImageProvider.Should().Be(ClientSettingsDefaults.ImageProvider);
            loaded.ImageModelId.Should().Be(ClientSettingsDefaults.ImageModelId);
            loaded.ImageModelApiKey.Should().BeEmpty();
            loaded.ImageModelEndpoint.Should().Be(ClientSettingsDefaults.ImageModelEndpoint);
            loaded.DoubaoImageResolution.Should().Be(ClientSettingsDefaults.DoubaoImageResolution);
            loaded.DoubaoImageRatio.Should().Be(ClientSettingsDefaults.DoubaoImageRatio);
            loaded.OfoxImage2ModelId.Should().Be(ClientSettingsDefaults.OfoxImage2ModelId);
            loaded.OfoxImage2ApiKey.Should().BeEmpty();
            loaded.OfoxImage2Endpoint.Should().Be(ClientSettingsDefaults.OfoxImage2Endpoint);
            loaded.OfoxImage2Quality.Should().Be(ClientSettingsDefaults.OfoxImage2Quality);
            loaded.OfoxImage2Size.Should().Be(ClientSettingsDefaults.OfoxImage2Size);
            loaded.PosterTitleVerifyMode.Should().Be(ClientSettingsDefaults.PosterTitleVerifyMode);
            loaded.TiktokAllowOverLimitUploadImport.Should().Be(ClientSettingsDefaults.TiktokAllowOverLimitUploadImport);
            loaded.TiktokOverLimitDownloadEpisodeCount.Should().Be(ClientSettingsDefaults.TiktokOverLimitDownloadEpisodeCount);
        }
        finally
        {
            try
            {
                File.Delete(databasePath);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Reset_hgnew_credentials_clears_only_hongguo_account_fields()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"client-settings-reset-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(tempDir, "app.db");
        Directory.CreateDirectory(tempDir);

        try
        {
            ClientSettingsStore.Save(new ClientSettings
            {
                HgnewAccount = "demo@example.com",
                HgnewPassword = "secret",
                HgnewUdid = "abc-def",
                HgnewClientVersion = "1.3.8",
                DramaDownloadConcurrent = 4,
            }, databasePath);

            ClientSettingsStore.ResetHgnewCredentials(databasePath);

            var loaded = ClientSettingsStore.Load(databasePath);
            loaded.HgnewAccount.Should().BeEmpty();
            loaded.HgnewPassword.Should().BeEmpty();
            loaded.HgnewUdid.Should().Be("ABC-DEF");
            loaded.HgnewClientVersion.Should().Be("1.3.8");
            loaded.DramaDownloadConcurrent.Should().Be(4);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Account_profile_publish_defaults_match_python_settings()
    {
        var account = new TikTokAccountProfile();

        account.TiktokSubmitEnabled.Should().BeTrue();
        account.TiktokSubmitAction.Should().Be("submit");
        account.TiktokUploadBrowserMode.Should().Be("playwright");
        account.TiktokPlaywrightUploadHeadless.Should().BeTrue();
        account.TiktokContractId.Should().Be("");
        account.TiktokContractIdMode.Should().Be("manual");
        account.TiktokAnchorPromotionEnabled.Should().BeTrue();
        account.TiktokTargetAudienceMode.Should().Be("ai_recommend");
        account.TiktokGenreCount.Should().Be(3);
        account.TiktokSourceLanguage.Should().Be("zh");
        account.TiktokIsAiDrama.Should().BeTrue();
        account.TiktokPublishMode.Should().Be("auto_after_review");
        account.TiktokConsignmentEnabled.Should().BeTrue();
        account.TiktokPaidEnabled.Should().BeFalse();
        account.TiktokPaidRatioEnabled.Should().BeFalse();
        account.TiktokPaidRatioPercent.Should().Be(20.0);
        account.TiktokProfilePreviewEpisodes.Should().Be(3);
        account.TiktokFreePreviewEpisodes.Should().Be(3);
        account.TiktokExpectedFullPriceMode.Should().Be("manual");
        account.TiktokExpectedFullPriceOptionIndex.Should().Be(1);
        account.TiktokUploadStrategy.Should().Be("classic");
        account.TiktokUploadBatchSize.Should().Be(3);
        account.TiktokUploadBatchStallSeconds.Should().Be(75);
        account.TiktokUploadBatchMaxRetries.Should().Be(3);
        account.TiktokSilenceValidationEnabled.Should().BeTrue();
        account.TiktokMaxContinuousSilenceSeconds.Should().Be(20);
        account.TiktokSilenceThresholdDb.Should().Be(-45.0);
    }

    [Fact]
    public void Publish_options_builder_uses_python_fallbacks_for_missing_values()
    {
        var account = new TikTokAccountProfile
        {
            TiktokTargetAudienceMode = "",
            TiktokGenreCount = 0,
            TiktokSourceLanguage = "",
            TiktokPublishMode = "",
            TiktokProfilePreviewEpisodes = 0,
            TiktokFreePreviewEpisodes = 0,
            TiktokExpectedFullPriceMode = "",
            TiktokExpectedFullPriceOptionIndex = 0,
            TiktokUploadStallSeconds = 0,
            TiktokUploadStrategy = "",
            TiktokUploadBatchSize = 0,
            TiktokUploadBatchStallSeconds = 0,
            TiktokUploadBatchMaxRetries = 0,
        };

        var options = TikTokPublishOptionsBuilder.FromAccount(account);

        options.TargetAudienceMode.Should().Be("ai_recommend");
        options.GenreCount.Should().Be(3);
        options.SourceLanguage.Should().Be("zh");
        options.PublishMode.Should().Be("auto_after_review");
        options.ProfilePreviewEpisodes.Should().Be(3);
        options.FreePreviewEpisodes.Should().Be(3);
        options.ExpectedFullPriceMode.Should().Be("manual");
        options.ExpectedFullPriceOptionIndex.Should().Be(1);
        options.UploadStallSeconds.Should().Be(180);
        options.UploadStrategy.Should().Be("classic");
        options.UploadBatchSize.Should().Be(3);
        options.UploadBatchStallSeconds.Should().Be(75);
        options.UploadBatchMaxRetries.Should().Be(3);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("draft")]
    [InlineData("保存")]
    [InlineData("保存草稿")]
    public void Final_action_parser_accepts_python_save_and_legacy_draft(string value)
    {
        FinalActionExtensions.Parse(value).Should().Be(FinalAction.Draft);
    }
}
