using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokPublishDefaultsTests
{
    [Fact]
    public void Account_profile_publish_defaults_match_python_settings()
    {
        var account = new TikTokAccountProfile();

        account.TiktokSubmitEnabled.Should().BeTrue();
        account.TiktokSubmitAction.Should().Be("submit");
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
