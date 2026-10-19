using FluentAssertions;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokNewTitleLanguageServiceTests
{
    [Theory]
    [InlineData("我的保安老公是隐世高人", "zh")]
    [InlineData("CEO的秘密", "zh")]
    [InlineData("隠れた帝王の夫", "ja")]
    [InlineData("경호원의 비밀", "ko")]
    [InlineData("ความลับของยาม", "th")]
    [InlineData("गार्ड का रहस्य", "hi")]
    public void ClassifyScript_maps_unique_scripts_to_existing_languages(string title, string expected)
    {
        TikTokNewTitleLanguageService.ClassifyScript(title).Should().Be(expected);
    }

    [Fact]
    public void ClassifyScript_leaves_latin_titles_for_the_model()
    {
        TikTokNewTitleLanguageService.ClassifyScript("The Guard Who Married Her Mother").Should().BeNull();
    }

    [Theory]
    [InlineData("{\"language\":\"pt\"}", "pt")]
    [InlineData("语言是 English", "en")]
    [InlineData("fr", "")]
    public void ParseLanguageCode_only_accepts_configured_languages(string content, string expected)
    {
        TikTokNewTitleLanguageService.ParseLanguageCode(content).Should().Be(expected);
    }
}
