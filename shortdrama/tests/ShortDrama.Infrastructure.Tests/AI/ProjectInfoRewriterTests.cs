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
    public async Task RewriteAsync_Should_Send_UserPrompt_Only()
    {
        var projectDir = Directory.CreateTempSubdirectory();
        var configPath = WriteConfig(projectDir.FullName);
        var outputPath = Path.Combine(projectDir.FullName, "改写结果.txt");
        await WriteProjectInfoAsync(projectDir.FullName, "亲戚别来我家");

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
        var configPath = WriteConfig(projectDir.FullName);
        var outputPath = Path.Combine(projectDir.FullName, "改写结果.txt");
        await WriteProjectInfoAsync(projectDir.FullName, "亲戚别来我家");

        var handler = new RecordingHandler(
        [
            """
            {"items":[{"id":"1","title":"断亲之后我掀桌改命","tagline":"亲戚登门这次我不忍了","synopsis":"结婚三年我处处忍让，直到极品亲戚登门霸占我家，我才当众掀桌断亲，逼得所有人低头求和。","short_title":"断亲掀桌","tags":["家庭","断亲","打脸"]}]}
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
        File.Exists(outputPath).Should().BeTrue();
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

    private static async Task WriteProjectInfoAsync(string projectDir, string originalTitle)
    {
        await File.WriteAllTextAsync(Path.Combine(projectDir, "短剧信息.txt"), $$"""
原剧名: {{originalTitle}}
集数: 41
时长: 43 分钟
成本: 6 万元
制作公司: 武汉云起漫影科技有限公司
""");
    }

    private static string WriteConfig(string projectDir)
    {
        var configPath = Path.Combine(projectDir, "config.txt");
        File.WriteAllText(configPath, """
AiTextEndpoint=https://example.com/api/v3
AiTextApiKey=test-key
AiTextModel=test-model
""");
        return configPath;
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
