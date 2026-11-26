using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;

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
    public void ApplyKnown_keeps_auto_until_upload_detects_the_title()
    {
        var options = new TikTokPublishOptions { SourceLanguage = "zh" };
        TikTokNewTitleLanguageService.ApplyKnown(
            options,
            new TikTokAccountProfile
            {
                TiktokSourceLanguage = TikTokNewTitleLanguageService.Auto,
                TiktokDramaMarket = "overseas",
            },
            projectDir: null);

        options.SourceLanguage.Should().Be("auto");
    }

    [Fact]
    public async Task ApplyAsync_detects_an_overseas_drama_by_its_publish_title()
    {
        var detected = new TikTokPublishOptions { SourceLanguage = "auto", IsAiDrama = true };
        var logs = new List<string>();
        await TikTokNewTitleLanguageService.ApplyAsync(
            detected,
            new TikTokAccountProfile { TiktokSourceLanguage = "auto", TiktokDramaMarket = "overseas" },
            projectDir: null,
            "我的保安老公是隐世高人",
            logs.Add,
            CancellationToken.None);

        detected.SourceLanguage.Should().Be("zh");
        logs.Should().Contain(line => line.Contains("识别为中文", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("旧梦归途", "兄妹寻找当年失落的账簿。", "auto", "zh")]
    [InlineData("旧梦归途", "兄妹寻找当年失落的账簿。", "id", "id")]
    [InlineData("2026", "兄妹寻找当年失落的账簿。", "auto", "zh")]
    [InlineData("경호원의 비밀", "경호원은 비밀을 알게 된다.", "auto", "ko")]
    public async Task Local_source_language_uses_account_selection_independently_of_english_publish_text(
        string originalTitle, string originalSynopsis, string selection, string expected)
    {
        var workspace = Directory.CreateTempSubdirectory("original-language-");
        try
        {
            var dir = workspace.CreateSubdirectory("source").FullName;
            File.WriteAllText(Path.Combine(dir, "shortdrama-project.json"), """{ "rewriteSourceLanguage": "en" }""");
            var workflow = ProjectWorkspaceService.PrepareWorkflowProject(dir);
            File.WriteAllText(Path.Combine(workflow, "短剧信息.txt"), $"""
                原剧名: {originalTitle}
                新剧名: Secrets in the Ledger
                原简介: {originalSynopsis}
                简介: The siblings search for a missing ledger.
                """);
            var account = new TikTokAccountProfile { TiktokSourceLanguage = selection };
            foreach (var useEnglish in new[] { false, true })
            {
                account.TiktokLocalDramaEnglishPublishText = useEnglish;
                var options = TikTokPublishOptionsBuilder.FromAccount(account);
                await TikTokNewTitleLanguageService.ApplyAsync(options, account,
                    dir, "Secrets in the Ledger", null, CancellationToken.None);

                options.SourceLanguage.Should().Be(expected);
            }
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_source_language_can_use_the_original_title_from_the_upload_item(bool useEnglish)
    {
        var options = new TikTokPublishOptions { SourceLanguage = "auto" };
        await TikTokNewTitleLanguageService.ApplyAsync(options,
            new TikTokAccountProfile { TiktokSourceLanguage = "auto", TiktokLocalDramaEnglishPublishText = useEnglish },
            projectDir: null, "Secrets in the Ledger", null, CancellationToken.None, originalTitle: "경호원의 비밀");

        options.SourceLanguage.Should().Be("ko");
    }

    [Fact]
    public void Stored_source_language_keeps_auto_and_a_chosen_language()
    {
        TikTokAccountProfile.NormalizeStoredSourceLanguage("auto", "overseas").Should().Be("auto");
        TikTokAccountProfile.NormalizeStoredSourceLanguage("auto", "local").Should().Be("auto");
        TikTokAccountProfile.NormalizeStoredSourceLanguage("", "overseas").Should().Be("en");
        TikTokAccountProfile.NormalizeStoredSourceLanguage("", "local").Should().Be("zh");
        TikTokAccountProfile.NormalizeStoredSourceLanguage("zh", "overseas").Should().Be("zh");
        TikTokAccountProfile.NormalizeStoredSourceLanguage("id", "local").Should().Be("id");
        TikTokAccountProfile.RewriteFollowsImportedLanguage("overseas").Should().BeTrue();
        TikTokAccountProfile.RewriteFollowsImportedLanguage("local").Should().BeFalse();
    }

    private static string CreateProjectDir(string projectJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), "title-lang-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "shortdrama-project.json"), projectJson);
        return dir;
    }
}
