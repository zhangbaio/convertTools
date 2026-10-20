using FluentAssertions;
using TikTokPublisher.Core.Models;
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

    [Fact]
    public async Task ResolveAsync_reuses_the_title_rewrite_language_for_latin_titles()
    {
        var dir = CreateProjectDir("""{ "rewriteSourceLanguage": "pt" }""");
        try
        {
            var language = await TikTokNewTitleLanguageService.ResolveAsync(
                "O Bilionário que Vivia como Sem-Teto",
                dir,
                new ClientSettings(),
                null,
                CancellationToken.None);

            language.Should().Be("pt");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_keeps_a_chinese_title_in_chinese_when_an_older_rewrite_language_differs()
    {
        var dir = CreateProjectDir("""{ "rewriteSourceLanguage": "pt" }""");
        try
        {
            var language = await TikTokNewTitleLanguageService.ResolveAsync(
                "我的保安老公是隐世高人",
                dir,
                new ClientSettings(),
                null,
                CancellationToken.None);

            language.Should().Be("zh");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ApplyKnown_reuses_the_title_rewrite_language()
    {
        var dir = CreateProjectDir("""{ "rewriteSourceLanguage": "es" }""");
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "短剧信息.txt"),
                "新剧名：No provoques a este mendigo" + Environment.NewLine);
            var options = new TikTokPublishOptions();
            TikTokNewTitleLanguageService.ApplyKnown(
                options,
                new TikTokAccountProfile { TiktokSourceLanguage = TikTokNewTitleLanguageService.Auto },
                dir);

            options.SourceLanguage.Should().Be("es");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string CreateProjectDir(string projectJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), "title-lang-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "shortdrama-project.json"), projectJson);
        return dir;
    }
}
