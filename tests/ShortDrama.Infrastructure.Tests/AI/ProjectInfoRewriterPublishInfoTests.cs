using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.AI;
using ShortDrama.Infrastructure.Parsing;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.AI;

public sealed class ProjectInfoRewriterPublishInfoTests
{
    private static readonly string LongSynopsis = string.Concat(Enumerable.Repeat("她在误解中坚持追查真相，", 10));
    private const string ShortEnglishSynopsis =
        "Mira has handled every difficult case at the station for years. After a storm cuts power, she finds an old letter inside a locked drawer, names three witnesses, and calls her brother for help. No one believes her story. She has one night to find the missing evidence before the mayor clears the building and the official records disappear for good.";
    private const string RewrittenEnglishSynopsis =
        "For years, Mira has taken on the station's toughest investigations. A power outage during a storm leads her to an old letter in a locked drawer. She identifies three witnesses and phones her brother for help. Nobody trusts her account. Before the mayor empties the building and the official files vanish forever, she gets one night to recover the lost evidence.";

    [Fact]
    public async Task Publish_info_mode_sends_one_request_and_writes_themes_and_audience_without_tagline()
    {
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        var infoPath = await WriteInfoAsync(projectDir, "替嫁新娘", "她被迫替嫁进豪门。");
        var configPath = WriteConfig(projectDir, rewriteSynopsis: true);
        var handler = new RecordingHandler(
        [
            JsonSerializer.Serialize(new
            {
                title = "错嫁之后我翻盘了",
                description = LongSynopsis,
                themes = new[] { "都市", "自创题材", "逆袭", "都市" },
                audience = "女",
                language = "zh",
            }),
        ]);

        var result = await CreateRewriter(handler).RewriteAsync(
            new ProjectInfoRewriteRequest(projectDir, configPath, infoPath, Overwrite: true),
            CancellationToken.None);

        handler.RequestBodies.Should().HaveCount(1);
        Message(handler.RequestBodies[0], 0).Should().Be("发布信息系统提示词");
        var userPrompt = Message(handler.RequestBodies[0], 1);
        userPrompt.Should().Contain("\"theme_options\":[\"都市\",\"逆袭\",\"豪门\",\"复仇\"]");
        userPrompt.Should().Contain("\"theme_count\":3");
        userPrompt.Should().Contain("\"ask_audience\":true");
        userPrompt.Should().NotContain("tagline");

        result.Title.Should().Be("错嫁之后我翻盘了");
        result.Tagline.Should().BeEmpty();
        var info = File.ReadAllText(infoPath);
        info.Should().Contain("新剧名: 错嫁之后我翻盘了");
        info.Should().Contain($"简介: {LongSynopsis}");
        info.Should().Contain("题材类型: 都市、逆袭");
        info.Should().Contain("目标观众: 女");
        info.Should().NotContain("推荐语");
        info.Should().Contain("时长: 43 分钟");
    }

    [Fact]
    public async Task Publish_info_mode_keeps_source_synopsis_and_only_asks_for_missing_themes()
    {
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        var infoPath = await WriteInfoAsync(projectDir, "替嫁新娘", "她被迫替嫁进豪门。", "题材类型: 豪门\n目标观众: 男\n");
        var configPath = WriteConfig(projectDir, rewriteSynopsis: false, themeCount: 2);
        var handler = new RecordingHandler(
        [
            """{"title":"错嫁之后我翻盘了","description":"不该使用","themes":"复仇、豪门","audience":"女","language":"zh"}""",
        ]);

        await CreateRewriter(handler).RewriteAsync(
            new ProjectInfoRewriteRequest(projectDir, configPath, infoPath, Overwrite: true),
            CancellationToken.None);

        var userPrompt = Message(handler.RequestBodies.Single(), 1);
        userPrompt.Should().Contain("\"theme_count\":1");
        userPrompt.Should().Contain("\"existing_themes\":[\"豪门\"]");
        userPrompt.Should().Contain("\"ask_audience\":false");
        var info = File.ReadAllText(infoPath);
        info.Should().Contain("简介: 她被迫替嫁进豪门。");
        info.Should().Contain("题材类型: 豪门、复仇");
        info.Should().Contain("目标观众: 男");
    }

    [Fact]
    public async Task Publish_info_mode_detects_source_language_in_the_same_request()
    {
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        var infoPath = await WriteInfoAsync(
            projectDir,
            "Não Mexa com Esse Mendigo",
            "Após três anos vivendo incógnito, Vince Astor esconde sua identidade.");
        var configPath = WriteConfig(projectDir, rewriteSynopsis: false, followSourceLanguage: true);
        var handler = new RecordingHandler(
        [
            """{"title":"Não Provoque Este Homem","description":"","themes":["复仇"],"audience":"男","language":"pt"}""",
        ]);

        var result = await CreateRewriter(handler).RewriteAsync(
            new ProjectInfoRewriteRequest(projectDir, configPath, infoPath, Overwrite: true),
            CancellationToken.None);

        handler.RequestBodies.Should().HaveCount(1);
        Message(handler.RequestBodies[0], 1).Should().Contain("跟随原文");
        result.Title.Should().Be("Não Provoque Este Homem");
        RewriteSourceLanguage.ReadCached(projectDir).Should().Be(RewriteSourceLanguage.Portuguese);
    }

    [Fact]
    public async Task Publish_info_mode_retries_once_with_the_failure_reason()
    {
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        var infoPath = await WriteInfoAsync(projectDir, "替嫁新娘", "她被迫替嫁进豪门。");
        var configPath = WriteConfig(projectDir, rewriteSynopsis: false);
        var handler = new RecordingHandler(
        [
            """{"title":"替嫁新娘","themes":["都市"],"audience":"女","language":"zh"}""",
            """{"title":"错嫁之后我翻盘了","themes":["都市"],"audience":"女","language":"zh"}""",
        ]);

        var result = await CreateRewriter(handler).RewriteAsync(
            new ProjectInfoRewriteRequest(projectDir, configPath, infoPath, Overwrite: true),
            CancellationToken.None);

        handler.RequestBodies.Should().HaveCount(2);
        Message(handler.RequestBodies[0], 1).Should().NotContain("\"retry\":{");
        var retryPrompt = Message(handler.RequestBodies[1], 1);
        retryPrompt.Should().Contain("\"retry\":{");
        retryPrompt.Should().Contain("新剧名与原剧名相同或过于相似");
        result.Title.Should().Be("错嫁之后我翻盘了");
    }

    [Fact]
    public async Task Light_synopsis_retries_only_the_description_using_the_original_text()
    {
        const string original = "Lina finds the missing ledger and shows it to her brother. They take the evidence to court and recover the family shop.";
        const string paraphrase = "Upon finding the lost ledger, Lina shows her brother what it contains. Together they present proof in court and reclaim the family shop.";
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        var infoPath = await WriteInfoAsync(projectDir, "The Hidden Ledger", "Previously expanded workflow synopsis.");
        var configPath = WriteConfig(projectDir, rewriteSynopsis: true, followSourceLanguage: true);
        var handler = new RecordingHandler(
        [
            LightPublishReply(original + " " + original),
            JsonSerializer.Serialize(new { description = paraphrase }),
            """{"matches_source":true,"reason":""}""",
        ]);

        var result = await CreateRewriter(handler).RewriteAsync(
            new ProjectInfoRewriteRequest(projectDir, configPath, infoPath, Overwrite: true,
                ForbiddenSynopses: [original], OriginalSynopsis: original),
            CancellationToken.None);

        result.Title.Should().Be("A Price at Midnight");
        result.Synopsis.Should().Be(paraphrase);
        handler.RequestBodies.Should().HaveCount(3);
        Message(handler.RequestBodies[0], 1).Should().Contain(original).And.NotContain("Previously expanded workflow synopsis.");
        Message(handler.RequestBodies[1], 1).Should().Contain("只改写简介").And.Contain("本次 44 词")
            .And.Contain("连续 5 词片段复用率").And.Contain("\"reused_five_word_fragments\":[\"Lina finds the missing ledger\"");
        Message(handler.RequestBodies[2], 1).Should().Contain(original).And.Contain(paraphrase);
        File.ReadAllText(infoPath).Should().Contain(paraphrase);
    }

    [Fact]
    public async Task Light_synopsis_accepts_the_first_draft_for_a_sixty_one_word_source()
    {
        var project = Directory.CreateTempSubdirectory();
        try
        {
            var infoPath = await WriteInfoAsync(project.FullName, "The Lost Files", ShortEnglishSynopsis);
            var configPath = WriteConfig(project.FullName, rewriteSynopsis: true, followSourceLanguage: true);
            var handler = new RecordingHandler(
            [
                LightPublishReply(RewrittenEnglishSynopsis),
                """{"matches_source":true,"reason":""}""",
            ]);

            var result = await CreateRewriter(handler).RewriteAsync(
                new ProjectInfoRewriteRequest(project.FullName, configPath, infoPath, Overwrite: true,
                    OriginalSynopsis: ShortEnglishSynopsis), CancellationToken.None);

            result.Synopsis.Should().Be(RewrittenEnglishSynopsis);
            handler.RequestBodies.Should().HaveCount(2);
            Message(handler.RequestBodies[0], 0).Should().Contain("只返回最终一版简介").And.Contain("25%");
            Message(handler.RequestBodies[0], 1).Should().Contain("目标 61 词，允许 55～67 词");
            Message(handler.RequestBodies[1], 0).Should().Contain("事实核对员");
        }
        finally
        {
            project.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Light_synopsis_names_the_actual_copied_sentence_and_stops_after_one_retry()
    {
        var project = Directory.CreateTempSubdirectory();
        try
        {
            var infoPath = await WriteInfoAsync(project.FullName, "The Lost Files", "Saved synopsis.", "新剧名: Existing Title");
            var previous = await File.ReadAllTextAsync(infoPath);
            var configPath = WriteConfig(project.FullName, rewriteSynopsis: true, followSourceLanguage: true);
            var copied = RewrittenEnglishSynopsis.Replace("Nobody trusts her account.", "NO ONE BELIEVES HER STORY!");
            var handler = new RecordingHandler([LightPublishReply(copied), JsonSerializer.Serialize(new { description = copied })]);

            var action = () => CreateRewriter(handler).RewriteAsync(
                new ProjectInfoRewriteRequest(project.FullName, configPath, infoPath, Overwrite: true,
                    OriginalSynopsis: ShortEnglishSynopsis), CancellationToken.None);

            await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*首版*整句照搬*重试*整句照搬*");
            handler.RequestBodies.Should().HaveCount(2);
            Message(handler.RequestBodies[1], 1).Should().Contain(
                "\"copied_sentences\":[{\"original\":\"No one believes her story.\",\"candidate\":\"NO ONE BELIEVES HER STORY!\"}]");
            (await File.ReadAllTextAsync(infoPath)).Should().Be(previous);
        }
        finally
        {
            project.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Publish_title_retry_reuses_the_initial_light_synopsis()
    {
        var project = Directory.CreateTempSubdirectory();
        try
        {
            var infoPath = await WriteInfoAsync(project.FullName, "The Lost Files", ShortEnglishSynopsis);
            var configPath = WriteConfig(project.FullName, rewriteSynopsis: true, followSourceLanguage: true);
            var handler = new RecordingHandler(
            [
                JsonSerializer.Serialize(new { title = "The Lost Files", description = RewrittenEnglishSynopsis, language = "en" }),
                LightPublishReply("Ignore this unrelated draft."),
                """{"matches_source":true,"reason":""}""",
            ]);

            var result = await CreateRewriter(handler).RewriteAsync(
                new ProjectInfoRewriteRequest(project.FullName, configPath, infoPath, Overwrite: true,
                    OriginalSynopsis: ShortEnglishSynopsis), CancellationToken.None);

            result.Title.Should().Be("A Price at Midnight");
            result.Synopsis.Should().Be(RewrittenEnglishSynopsis);
            handler.RequestBodies.Should().HaveCount(3);
            Message(handler.RequestBodies[1], 1).Should().Contain("\"rewrite_synopsis\":false");
            Message(handler.RequestBodies[2], 1).Should().Contain(RewrittenEnglishSynopsis);
        }
        finally
        {
            project.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Light_synopsis_sends_the_plot_issue_to_the_only_retry_and_preserves_existing_output_on_failure()
    {
        const string original = "Lina finds the missing ledger and shows it to her brother. They take the evidence to court and recover the family shop.";
        const string changed = "Lina locates the lost ledger and shares it with her sister. They bring the evidence to court and win back the family shop.";
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        var infoPath = await WriteInfoAsync(projectDir, "The Hidden Ledger", "Saved synopsis and title.", "新剧名: Existing Title");
        var previous = await File.ReadAllTextAsync(infoPath);
        var configPath = WriteConfig(projectDir, rewriteSynopsis: true, followSourceLanguage: true);
        var handler = new RecordingHandler(
        [
            LightPublishReply(changed),
            """{"matches_source":false,"reason":"把哥哥改成了姐妹"}""",
            JsonSerializer.Serialize(new { description = changed }),
            """{"matches_source":false,"reason":"把哥哥改成了姐妹"}""",
        ]);

        var action = () => CreateRewriter(handler).RewriteAsync(
            new ProjectInfoRewriteRequest(projectDir, configPath, infoPath, Overwrite: true, OriginalSynopsis: original),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*剧情核对未通过*");
        handler.RequestBodies.Should().HaveCount(4);
        Message(handler.RequestBodies[2], 1).Should().Contain("只改写简介").And.Contain("把哥哥改成了姐妹");
        (await File.ReadAllTextAsync(infoPath)).Should().Be(previous);
    }

    [Fact]
    public void Copied_sentences_are_detected_despite_case_and_punctuation_without_splitting_at_titles()
    {
        const string original = "Dr. Evelyn waited in the hall.She carried the evidence home.";
        ProjectInfoRewriter.ContainsCopiedSynopsisSentence(original,
            "DR. EVELYN WAITED IN THE HALL! The proof eventually went home.").Should().BeTrue();
        ProjectInfoRewriter.ContainsCopiedSynopsisSentence(original,
            "Dr. Evelyn remained in the hallway. She took the proof back home.").Should().BeFalse();
    }

    [Fact]
    public void Five_word_reuse_counts_sliding_windows_and_normalizes_case_and_punctuation()
    {
        const string source = "One two three four five six seven eight nine.";
        ProjectInfoRewriter.CountReusedSynopsisFragments(source, "ONE, two three four five alpha beta gamma.")
            .Should().Be((1, 4));
        ProjectInfoRewriter.CountReusedSynopsisFragments(source, "One two three four five six beta gamma.")
            .Should().Be((2, 4));
        ProjectInfoRewriter.CountReusedSynopsisFragments(source, "Only four words here.").Should().Be((0, 0));
    }

    [Fact]
    public async Task Light_synopsis_counts_chinese_characters_without_padding_short_sources()
    {
        const string original = "小雨找到失踪的妹妹，两人一起回家。";
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        var infoPath = await WriteInfoAsync(projectDir, "雨夜寻人", original);
        var configPath = WriteConfig(projectDir, rewriteSynopsis: true);
        var handler = new RecordingHandler(
        [
            """{"title":"归途有她陪伴","description":"小雨寻到走失的妹妹，两人结伴回家。","language":"zh"}""",
            """{"matches_source":true,"reason":""}""",
        ]);

        var result = await CreateRewriter(handler).RewriteAsync(
            new ProjectInfoRewriteRequest(projectDir, configPath, infoPath, Overwrite: true, OriginalSynopsis: original),
            CancellationToken.None);

        result.Synopsis.Should().Be("小雨寻到走失的妹妹，两人结伴回家。");
        Message(handler.RequestBodies[0], 1).Should().Contain("14～16 字");
    }

    private static string LightPublishReply(string synopsis) =>
        JsonSerializer.Serialize(new { title = "A Price at Midnight", description = synopsis, language = "en" });

    private static ProjectInfoRewriter CreateRewriter(RecordingHandler handler) =>
        new(new TxtProjectInfoParser(), new HttpClient(handler), NullLogger<ProjectInfoRewriter>.Instance);

    private static async Task<string> WriteInfoAsync(string projectDir, string originalTitle, string synopsis, string extra = "")
    {
        var path = Path.Combine(projectDir, "短剧信息.txt");
        await File.WriteAllTextAsync(path, $"""
原剧名: {originalTitle}
简介: {synopsis}
集数: 41
时长: 43 分钟
制作公司: 测试公司
{extra}
""");
        return path;
    }

    private static string WriteConfig(
        string projectDir,
        bool rewriteSynopsis,
        int themeCount = 3,
        bool followSourceLanguage = false)
    {
        var configPath = Path.Combine(projectDir, "config.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["AiTextEndpoint"] = "https://example.com/api/v3",
            ["AiTextApiKey"] = "test-key",
            ["AiTextModel"] = "test-model",
            ["AiTextSystemPrompt"] = "发布信息系统提示词",
            ["AiPublishInfoMode"] = true,
            ["AiPublishThemeOptions"] = "都市|逆袭|豪门|复仇",
            ["AiPublishThemeCount"] = themeCount,
            ["AiPublishAskAudience"] = true,
            ["AiRewriteSynopsis"] = rewriteSynopsis,
            ["AiRewriteFollowsSourceLanguage"] = followSourceLanguage,
        }));
        return configPath;
    }

    private static string Message(string requestBody, int index)
    {
        using var document = JsonDocument.Parse(requestBody);
        return document.RootElement.GetProperty("messages")[index].GetProperty("content").GetString() ?? "";
    }

    private sealed class RecordingHandler(IEnumerable<string> responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var content = _responses.Dequeue();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }),
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
