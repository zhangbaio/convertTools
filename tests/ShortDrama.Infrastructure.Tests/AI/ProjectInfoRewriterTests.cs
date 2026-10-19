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

public sealed class ProjectInfoRewriterTests
{
    [Fact]
    public void Detect_treats_an_english_import_title_as_english()
    {
        RewriteSourceLanguage.Detect("My CEO Mom Married a Security Guard", "A CEO discovers her mother married the building guard.")
            .Should().Be(RewriteSourceLanguage.English);
        RewriteSourceLanguage.Detect("亲戚别来我家", "极品亲戚登门。")
            .Should().Be(RewriteSourceLanguage.Chinese);
        RewriteSourceLanguage.Detect(
                "MyCEOMomMarriedaSecurityGuard",
                "五年前，隐退的顶级强者兰登意外结识遭人设计的萨曼莎，两人结缘后生下女儿。")
            .Should().Be(RewriteSourceLanguage.English);
    }

    [Fact]
    public async Task RewriteAsync_follows_the_english_source_language()
    {
        var projectDir = Directory.CreateTempSubdirectory();
        var configPath = Path.Combine(projectDir.FullName, "config.json");
        File.WriteAllText(configPath, """
{
  "AiTextEndpoint": "https://example.com/api/v3",
  "AiTextApiKey": "test-key",
  "AiTextModel": "test-model",
  "AiRewriteSynopsis": false,
  "AiRewriteFollowsSourceLanguage": true
}
""");
        var outputPath = Path.Combine(projectDir.FullName, "改写结果.txt");
        await WriteProjectInfoAsync(
            projectDir.FullName,
            "My CEO Mom Married a Security Guard",
            "A powerful CEO learns that her mother quietly married the guard who protected their building.");

        var handler = new RecordingHandler(
        [
            """
            {"title":"The Guard Who Married Her Mother","tagline":"A family secret reaches the altar","synopsis":"unused"}
            """
        ]);
        var rewriter = new ProjectInfoRewriter(
            new TxtProjectInfoParser(),
            new HttpClient(handler),
            NullLogger<ProjectInfoRewriter>.Instance);

        var result = await rewriter.RewriteAsync(
            new ProjectInfoRewriteRequest(projectDir.FullName, configPath, outputPath, Overwrite: true),
            CancellationToken.None);

        result.Title.Should().Be("The Guard Who Married Her Mother");
        ExtractUserPrompt(handler.RequestBodies[0]).Should().Contain("禁止翻译成中文");
    }

    [Fact]
    public async Task RewriteAsync_Should_Send_UserPrompt_Only()
    {
        var projectDir = Directory.CreateTempSubdirectory();
        var configPath = WriteConfig(projectDir.FullName);
        var outputPath = Path.Combine(projectDir.FullName, "改写结果.txt");
        const string sourceSynopsis = "原始简介保留，不交给 AI 改写。";
        await WriteProjectInfoAsync(projectDir.FullName, "亲戚别来我家", sourceSynopsis);

        var handler = new RecordingHandler(
        [
            """
            {"title":"断亲之后我掀桌改命","tagline":"亲戚登门这次我不忍了","synopsis":"结婚三年我处处忍让，直到极品亲戚登门霸占我家，我才当众掀桌断亲，逼得所有人低头求和。","short_title":"断亲掀桌","tags":["家庭","断亲","打脸"]}
            """
        ]);

        var rewriter = new ProjectInfoRewriter(
            new TxtProjectInfoParser(),
            new HttpClient(handler),
            NullLogger<ProjectInfoRewriter>.Instance);

        var result = await rewriter.RewriteAsync(
            new ProjectInfoRewriteRequest(projectDir.FullName, configPath, outputPath, Overwrite: true),
            CancellationToken.None);

        result.Title.Should().Be("断亲之后我掀桌改命");
        result.ShortTitle.Should().BeEmpty();
        result.Tags.Should().BeEmpty();
        var outputText = await File.ReadAllTextAsync(outputPath);
        outputText.Should().NotContain("短标题:");
        outputText.Should().NotContain("标签:");
        outputText.Should().Contain($"简介: {sourceSynopsis}");
        outputText.Should().NotContain("结婚三年我处处忍让");
        handler.RequestBodies.Should().ContainSingle();

        using var document = JsonDocument.Parse(handler.RequestBodies[0]);
        var messages = document.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().Be(1);
        messages[0].GetProperty("role").GetString().Should().Be("user");
    }

    [Fact]
    public async Task RewriteAsync_Should_Also_Accept_Legacy_Items_Array_Response()
    {
        var projectDir = Directory.CreateTempSubdirectory();
        var configPath = WriteConfig(projectDir.FullName, aiRewriteSynopsis: true);
        var outputPath = Path.Combine(projectDir.FullName, "改写结果.txt");
        await WriteProjectInfoAsync(projectDir.FullName, "亲戚别来我家");

        var handler = new RecordingHandler(
        [
            """
            {"items":[{"id":"1","title":"断亲之后我掀桌改命","tagline":"亲戚登门这次我不忍了","synopsis":"结婚三年我处处忍让，直到极品亲戚登门霸占我家，我才当众掀桌断亲，逼得所有人低头求和。","short_title":"断亲掀桌","tags":["家庭","断亲","打脸"]}]}
            """,
            """
            {"synopsis":"结婚三年她处处忍让，直到亲戚登门霸占屋子，才当众掀桌断亲。此后她收起退让，用证据和分寸把被侵占的日子一点点要回来，也让曾经轻视她的人不得不正视她的选择。风波过后，她守住了自己的家，也终于能安心睡一觉。"}
            """
        ]);

        var rewriter = new ProjectInfoRewriter(
            new TxtProjectInfoParser(),
            new HttpClient(handler),
            NullLogger<ProjectInfoRewriter>.Instance);

        var result = await rewriter.RewriteAsync(
            new ProjectInfoRewriteRequest(projectDir.FullName, configPath, outputPath, Overwrite: true),
            CancellationToken.None);

        result.Title.Should().Be("断亲之后我掀桌改命");
        result.ShortTitle.Should().BeEmpty();
        result.Tags.Should().BeEmpty();
        File.Exists(outputPath).Should().BeTrue();
    }

    [Fact]
    public async Task RewriteAsync_Should_Retry_When_Title_Or_Synopsis_Matches_Forbidden_History()
    {
        var projectDir = Directory.CreateTempSubdirectory();
        var configPath = WriteConfig(projectDir.FullName, aiRewriteSynopsis: true);
        var outputPath = Path.Combine(projectDir.FullName, "改写结果.txt");
        await WriteProjectInfoAsync(projectDir.FullName, "亲戚别来我家");

        var forbiddenTitle = "断亲之后我掀桌改命";
        var forbiddenSynopsis = "结婚三年我处处忍让，直到极品亲戚登门霸占我家，我才当众掀桌断亲，逼得所有人低头求和。";
        var handler = new RecordingHandler(
        [
            $$"""
            {"title":"{{forbiddenTitle}}","tagline":"亲戚登门这次我不忍了","synopsis":"{{forbiddenSynopsis}}","short_title":"断亲掀桌","tags":["家庭","断亲","打脸"]}
            """,
            """
            {"title":"恶亲登门我反手断供","tagline":"亲戚欺上门我绝地反击","synopsis":"多年忍让换来亲戚步步紧逼，女主终于撕开虚伪亲情，当众断供反击，把失控家庭重新握回手中。","short_title":"断供反击","tags":["家庭","断亲","打脸"]}
            """,
            """
            {"synopsis":"结婚三年她处处忍让，直到亲戚登门霸占屋子，才当众掀桌断亲。此后她收起退让，用证据和分寸把被侵占的日子一点点要回来，也让曾经轻视她的人不得不正视她的选择。风波过后，她守住了自己的家，也终于能安心睡一觉。"}
            """
        ]);

        var rewriter = new ProjectInfoRewriter(
            new TxtProjectInfoParser(),
            new HttpClient(handler),
            NullLogger<ProjectInfoRewriter>.Instance);

        var result = await rewriter.RewriteAsync(
            new ProjectInfoRewriteRequest(
                projectDir.FullName,
                configPath,
                outputPath,
                Overwrite: true,
                ForbiddenTitles: [forbiddenTitle],
                ForbiddenSynopses: [forbiddenSynopsis],
                TargetSynopsisLength: forbiddenSynopsis.Length,
                RewriteVariantKey: "account-a"),
            CancellationToken.None);

        result.Title.Should().Be("恶亲登门我反手断供");
        result.Synopsis.Length.Should().BeInRange(100, 300);
        handler.RequestBodies.Should().HaveCount(3);
        ExtractPrompt(handler.RequestBodies[2]).Should().Contain("100 到 300");
        ExtractPrompt(handler.RequestBodies[0]).Should().Contain("forbidden_titles");
        ExtractPrompt(handler.RequestBodies[0]).Should().Contain("forbidden_synopses");
    }

    [Fact]
    public async Task RewriteAsync_Should_Fail_When_Rewrite_Remains_Invalid_After_Retries()
    {
        var projectDir = Directory.CreateTempSubdirectory();
        var configPath = WriteConfig(projectDir.FullName);
        var outputPath = Path.Combine(projectDir.FullName, "改写结果.txt");
        await WriteProjectInfoAsync(projectDir.FullName, "亲戚别来我家");

        var invalidPayload = """
            {"title":"亲戚别来我家","tagline":"高能来袭","synopsis":"亲戚又来闹事，剧情持续升级。","short_title":"亲戚别来我家","tags":["家庭"]}
            """;

        var handler = new RecordingHandler([invalidPayload, invalidPayload]);
        var rewriter = new ProjectInfoRewriter(
            new TxtProjectInfoParser(),
            new HttpClient(handler),
            NullLogger<ProjectInfoRewriter>.Instance);

        var act = () => rewriter.RewriteAsync(
            new ProjectInfoRewriteRequest(projectDir.FullName, configPath, outputPath, Overwrite: true),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*已重试 2 次仍未通过*");

        handler.RequestBodies.Should().HaveCount(2);
        File.Exists(outputPath).Should().BeFalse();
    }

    [Fact]
    public async Task RewriteAsync_Should_Accept_Similar_Title_That_Is_Not_Exact_Occupied()
    {
        var projectDir = Directory.CreateTempSubdirectory();
        var configPath = WriteConfig(projectDir.FullName);
        var outputPath = Path.Combine(projectDir.FullName, "改写结果.txt");
        await WriteProjectInfoAsync(projectDir.FullName, "亲戚别来我家");

        var handler = new RecordingHandler(
        [
            """
            {"title":"山野种田养出满仓禾","tagline":"山野耕耘日子慢慢变好","synopsis":"山里人把荒地一点点种成粮仓。"}
            """
        ]);
        var rewriter = new ProjectInfoRewriter(
            new TxtProjectInfoParser(),
            new HttpClient(handler),
            NullLogger<ProjectInfoRewriter>.Instance);

        var result = await rewriter.RewriteAsync(
            new ProjectInfoRewriteRequest(
                projectDir.FullName,
                configPath,
                outputPath,
                Overwrite: true,
                OccupiedExactTitles: ["山野种田养出满仓粮"]),
            CancellationToken.None);

        result.Title.Should().Be("山野种田养出满仓禾");
        handler.RequestBodies.Should().HaveCount(1);
    }

    [Fact]
    public async Task RewriteAsync_Should_Retry_When_Title_Exactly_Matches_Occupied_Name()
    {
        var projectDir = Directory.CreateTempSubdirectory();
        var configPath = WriteConfig(projectDir.FullName);
        var outputPath = Path.Combine(projectDir.FullName, "改写结果.txt");
        await WriteProjectInfoAsync(projectDir.FullName, "亲戚别来我家");

        var handler = new RecordingHandler(
        [
            """
            {"title":"《山野种田养出满仓粮》","tagline":"山野耕耘日子慢慢变好","synopsis":"山里人把荒地一点点种成粮仓。"}
            """,
            """
            {"title":"山野耕耘换来满仓粮","tagline":"山野耕耘日子慢慢变好","synopsis":"山里人把荒地一点点种成粮仓。"}
            """
        ]);
        var rewriter = new ProjectInfoRewriter(
            new TxtProjectInfoParser(),
            new HttpClient(handler),
            NullLogger<ProjectInfoRewriter>.Instance);

        var result = await rewriter.RewriteAsync(
            new ProjectInfoRewriteRequest(
                projectDir.FullName,
                configPath,
                outputPath,
                Overwrite: true,
                OccupiedExactTitles: ["山野种田养出满仓粮"]),
            CancellationToken.None);

        result.Title.Should().Be("山野耕耘换来满仓粮");
        handler.RequestBodies.Should().HaveCount(2);
        ExtractPrompt(handler.RequestBodies[1]).Should().Contain("occupied_exact_titles");
    }

    [Fact]
    public void Synopsis_over_1500_characters_is_cut_on_a_sentence_boundary()
    {
        var sentence = new string('人', 20) + "。";
        var text = string.Concat(Enumerable.Repeat(sentence, 80));

        var fitted = ProjectInfoRewriter.FitSynopsisToAbsoluteMax(text);

        fitted.Length.Should().BeLessOrEqualTo(ProjectInfoRewriter.SynopsisAbsoluteMaxLength);
        fitted.Length.Should().BeGreaterOrEqualTo(ProjectInfoRewriter.SynopsisPreferredMinLength);
        fitted.Should().EndWith("。");
    }

    [Fact]
    public async Task RewriteAsync_keeps_new_title_and_original_synopsis_when_ai_synopsis_stays_under_100()
    {
        var projectDir = Directory.CreateTempSubdirectory();
        var configPath = WriteConfig(projectDir.FullName, aiRewriteSynopsis: true);
        var outputPath = Path.Combine(projectDir.FullName, "改写结果.txt");
        var original = new string('原', 120);
        await WriteProjectInfoAsync(projectDir.FullName, "亲戚别来我家", original);

        var handler = new RecordingHandler(
        [
            """
            {"title":"恶亲登门我反手断供","tagline":"亲戚欺上门我绝地反击","synopsis":"短"}
            """,
            """{"synopsis":"太短"}""",
            """{"synopsis":"还是太短"}"""
        ]);
        var rewriter = new ProjectInfoRewriter(
            new TxtProjectInfoParser(),
            new HttpClient(handler),
            NullLogger<ProjectInfoRewriter>.Instance);

        var result = await rewriter.RewriteAsync(
            new ProjectInfoRewriteRequest(projectDir.FullName, configPath, outputPath, Overwrite: true),
            CancellationToken.None);

        result.Title.Should().Be("恶亲登门我反手断供");
        result.Synopsis.Should().Be(original);
        ExtractUserPrompt(handler.RequestBodies[2]).Should().Contain("请补到 100 字以上");
    }

    [Fact]
    public async Task RewriteAsync_keeps_synopsis_between_301_and_1500_as_fallback()
    {
        var projectDir = Directory.CreateTempSubdirectory();
        var configPath = WriteConfig(projectDir.FullName, aiRewriteSynopsis: true);
        var outputPath = Path.Combine(projectDir.FullName, "改写结果.txt");
        await WriteProjectInfoAsync(projectDir.FullName, "亲戚别来我家", new string('原', 180));
        var longSynopsis = new string('改', 420);

        var handler = new RecordingHandler(
        [
            """
            {"title":"恶亲登门我反手断供","tagline":"亲戚欺上门我绝地反击","synopsis":"占位"}
            """,
            $$"""{"synopsis":"{{longSynopsis}}"}""",
            $$"""{"synopsis":"{{longSynopsis}}"}"""
        ]);
        var rewriter = new ProjectInfoRewriter(
            new TxtProjectInfoParser(),
            new HttpClient(handler),
            NullLogger<ProjectInfoRewriter>.Instance);

        var result = await rewriter.RewriteAsync(
            new ProjectInfoRewriteRequest(projectDir.FullName, configPath, outputPath, Overwrite: true),
            CancellationToken.None);

        result.Title.Should().Be("恶亲登门我反手断供");
        result.Synopsis.Should().Be(longSynopsis);
        ExtractUserPrompt(handler.RequestBodies[2]).Should().Contain("请删到 300 字以内");
    }

    private static async Task WriteProjectInfoAsync(string projectDir, string originalTitle, string synopsis = "")
    {
        await File.WriteAllTextAsync(Path.Combine(projectDir, "短剧信息.txt"), $$"""
原剧名: {{originalTitle}}
简介: {{synopsis}}
集数: 41
时长: 43 分钟
成本: 6 万元
制作公司: 武汉云起漫影科技有限公司
""");
    }

    private static string WriteConfig(string projectDir, bool aiRewriteSynopsis = false)
    {
        var configPath = Path.Combine(projectDir, "config.json");
        File.WriteAllText(configPath, $$"""
{
  "AiTextEndpoint": "https://example.com/api/v3",
  "AiTextApiKey": "test-key",
  "AiTextModel": "test-model",
  "AiRewriteSynopsis": {{JsonSerializer.Serialize(aiRewriteSynopsis)}}
}
""");
        return configPath;
    }

    private static string ExtractPrompt(string requestBody) => ExtractMessage(requestBody, 0);

    private static string ExtractUserPrompt(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        var messages = document.RootElement.GetProperty("messages");
        return ExtractMessage(requestBody, messages.GetArrayLength() - 1);
    }

    private static string ExtractMessage(string requestBody, int index)
    {
        using var document = JsonDocument.Parse(requestBody);
        return document.RootElement
            .GetProperty("messages")[index]
            .GetProperty("content")
            .GetString() ?? "";
    }

    private static HttpResponseMessage CreateChatCompletion(string content)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                {
                  "choices": [
                    {
                      "message": {
                        "content": {{JsonSerializer.Serialize(content)}}
                      }
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public RecordingHandler(IEnumerable<string> responses)
        {
            _responses = new Queue<string>(responses);
        }

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return CreateChatCompletion(_responses.Dequeue());
        }
    }
}
