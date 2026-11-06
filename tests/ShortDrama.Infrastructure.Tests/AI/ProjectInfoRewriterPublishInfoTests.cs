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
