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

public sealed class ProjectInfoRewriterUnifiedPublishPromptTests
{
    private const string VisiblePrompt = "使用可见提示词。\n只返回 title、description、themes、audience 和 language。";
    private const string OriginalSynopsis = "The siblings find a ledger in the old shop. They take it to court.";
    private const string ExistingSynopsis = "This is the existing description.";

    [Theory]
    [InlineData(true, "auto", "en")]
    [InlineData(false, "zh", "zh")]
    public async Task Sends_one_visible_prompt_and_project_data_without_a_second_content_check(
        bool followSourceLanguage, string outputLanguage, string language)
    {
        // The title is both very short and on the forbidden list. The synopsis
        // repeats the source. A structurally valid model response must still be kept.
        const string modelTitle = "X";
        using var fixture = new RewriteFixture(true, followSourceLanguage,
            [Reply(modelTitle, OriginalSynopsis, language)]);

        var result = await fixture.RunAsync();

        var requestBytes = fixture.Requests.Should().ContainSingle().Subject;
        using var envelope = JsonDocument.Parse(requestBytes);
        var messages = envelope.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().Be(2);
        messages[0].GetProperty("role").GetString().Should().Be("system");
        messages[0].GetProperty("content").GetString().Should().Be(VisiblePrompt);
        messages[1].GetProperty("role").GetString().Should().Be("user");

        using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        input.RootElement.GetProperty("output_language").GetString().Should().Be(outputLanguage);
        input.RootElement.GetProperty("synopsis").GetString().Should().Be(OriginalSynopsis);
        input.RootElement.GetProperty("rewrite_synopsis").GetBoolean().Should().BeTrue();
        input.RootElement.GetProperty("theme_options").EnumerateArray().Select(value => value.GetString())
            .Should().Equal("都市", "逆袭");
        input.RootElement.GetProperty("theme_count").GetInt32().Should().Be(1);
        input.RootElement.GetProperty("forbidden_titles")[0].GetString().Should().Be(modelTitle);
        input.RootElement.GetProperty("occupied_exact_titles")[0].GetString().Should().Be("Occupied title");
        input.RootElement.TryGetProperty("retry", out _).Should().BeFalse();
        input.RootElement.TryGetProperty("length_rules", out _).Should().BeFalse();

        result.Title.Should().Be(modelTitle);
        result.Synopsis.Should().Be(OriginalSynopsis);
        var saved = await File.ReadAllTextAsync(fixture.InfoPath);
        saved.Should().Contain($"原简介: {OriginalSynopsis}").And.Contain($"\n简介: {OriginalSynopsis}");
        saved.Should().NotContain(ExistingSynopsis);
    }

    [Theory]
    [InlineData("broken JSON {")]
    [InlineData("{\"description\":\"A valid description.\",\"themes\":[],\"audience\":\"\",\"language\":\"en\"}")]
    [InlineData("{\"title\":\"Still valid\",\"description\":\"\",\"themes\":[],\"audience\":\"\",\"language\":\"en\"}")]
    public async Task Retries_only_once_with_identical_request_bytes(string rejectedResponse)
    {
        using var fixture = new RewriteFixture(true, true, [rejectedResponse, Reply("A new title", "Recovered text.")]);

        var result = await fixture.RunAsync();

        fixture.Requests.Should().HaveCount(2);
        fixture.Requests[1].Should().Equal(fixture.Requests[0]);
        result.Title.Should().Be("A new title");
        result.Synopsis.Should().Be("Recovered text.");
    }

    [Fact]
    public async Task Disabled_synopsis_rewrite_keeps_the_previously_saved_description()
    {
        using var fixture = new RewriteFixture(false, true, [Reply("A new title", "Unexpected model description.")]);

        var result = await fixture.RunAsync();

        using var envelope = JsonDocument.Parse(fixture.Requests.Should().ContainSingle().Subject);
        using var input = JsonDocument.Parse(envelope.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        input.RootElement.GetProperty("rewrite_synopsis").GetBoolean().Should().BeFalse();
        result.Synopsis.Should().Be(ExistingSynopsis);
        var saved = await File.ReadAllTextAsync(fixture.InfoPath);
        saved.Should().Contain($"原简介: {OriginalSynopsis}").And.Contain($"\n简介: {ExistingSynopsis}");
        saved.Should().NotContain("Unexpected model description.");
    }

    [Fact]
    public async Task Invalid_responses_leave_existing_output_untouched()
    {
        using var fixture = new RewriteFixture(true, true, ["not JSON", "still not JSON"]);
        var previous = await File.ReadAllBytesAsync(fixture.InfoPath);

        var run = () => fixture.RunAsync();
        await run.Should().ThrowAsync<InvalidOperationException>();

        fixture.Requests.Should().HaveCount(2);
        fixture.Requests[1].Should().Equal(fixture.Requests[0]);
        (await File.ReadAllBytesAsync(fixture.InfoPath)).Should().Equal(previous);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_retry_or_replace_existing_output()
    {
        using var source = new CancellationTokenSource();
        using var fixture = new RewriteFixture(true, true, [], source.Cancel);
        var previous = await File.ReadAllBytesAsync(fixture.InfoPath);

        var run = () => fixture.RunAsync(source.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();

        fixture.Requests.Should().ContainSingle();
        (await File.ReadAllBytesAsync(fixture.InfoPath)).Should().Equal(previous);
    }

    [Theory]
    [InlineData(false, "忠实翻译原简介")]
    [InlineData(true, "轻度改写原简介")]
    public async Task English_publish_text_generates_both_fields_in_one_request(bool rewriteSynopsis, string instruction)
    {
        const string translatedSynopsis = "Two siblings uncover a secret ledger and bring it to court.";
        const string sourceSynopsis = "兄妹在旧店找到一本账簿，并把它交给法庭。";
        using var fixture = new RewriteFixture(rewriteSynopsis, false,
            [Reply("Secrets in the Ledger", translatedSynopsis)], useEnglish: true, originalSynopsis: sourceSynopsis);

        var result = await fixture.RunAsync();

        using var envelope = JsonDocument.Parse(fixture.Requests.Should().ContainSingle().Subject);
        var messages = envelope.RootElement.GetProperty("messages");
        messages[0].GetProperty("content").GetString().Should().Contain(instruction);
        using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        input.RootElement.GetProperty("output_language").GetString().Should().Be("en");
        input.RootElement.GetProperty("rewrite_synopsis").GetBoolean().Should().BeTrue();
        result.Title.Should().Be("Secrets in the Ledger");
        result.Synopsis.Should().Be(translatedSynopsis);
        (await File.ReadAllTextAsync(fixture.InfoPath)).Should()
            .Contain($"原简介: {sourceSynopsis}").And.Contain($"\n简介: {translatedSynopsis}");
    }

    [Fact]
    public async Task Long_english_synopsis_is_repaired_without_regenerating_the_title()
    {
        const string title = "Secrets in the Ledger";
        const string corrected = "Two siblings uncover a secret ledger and bring it to court.";
        using var fixture = new RewriteFixture(false, false,
            [Reply(title, new string('a', 1028)), JsonSerializer.Serialize(new { description = corrected })],
            useEnglish: true);

        var result = await fixture.RunAsync();

        fixture.Requests.Should().HaveCount(2);
        var repair = ReadRequestInput(fixture.Requests[1]);
        repair.GetProperty("repair_fields").EnumerateObject().Select(field => field.Name).Should().Equal("description");
        repair.GetProperty("repair_fields").GetProperty("description").GetString().Should().Contain("1028").And.Contain("900");
        repair.GetProperty("original_request").GetProperty("occupied_exact_titles")[0].GetString().Should().Be("Occupied title");
        repair.GetProperty("original_request").GetProperty("synopsis").GetString().Should().Be(OriginalSynopsis);
        result.Title.Should().Be(title);
        result.Synopsis.Should().Be(corrected);
        (await File.ReadAllTextAsync(fixture.InfoPath)).Should().Contain("题材类型: 都市").And.Contain("目标观众: 女");
    }

    [Fact]
    public async Task Short_english_title_is_repaired_and_other_returned_fields_are_ignored()
    {
        const string validSynopsis = "Two siblings bring a secret ledger to court.";
        const string correctedTitle = "The Lost Ledger";
        using var fixture = new RewriteFixture(true, false,
            [Reply("Ledger", validSynopsis), Reply(correctedTitle, "Different synopsis.", "zh")],
            useEnglish: true);

        var result = await fixture.RunAsync();

        fixture.Requests.Should().HaveCount(2);
        var fields = ReadRequestInput(fixture.Requests[1]).GetProperty("repair_fields");
        fields.EnumerateObject().Select(field => field.Name).Should().Equal("title");
        fields.GetProperty("title").GetString().Should().Contain("6 个字符").And.Contain("8–80");
        result.Title.Should().Be(correctedTitle);
        result.Synopsis.Should().Be(validSynopsis);
    }

    [Fact]
    public async Task Invalid_length_after_repair_reports_each_field_and_keeps_existing_output()
    {
        using var fixture = new RewriteFixture(true, false,
            [Reply("Ledger", new string('a', 1028)), Reply(new string('b', 81), new string('a', 951))], useEnglish: true);
        var previous = await File.ReadAllBytesAsync(fixture.InfoPath);
        var run = () => fixture.RunAsync();

        await run.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*修正不合规字段一次后仍然失败*英文新剧名 81 个字符*8–80*英文简介 951 个字符*900 个字符*");

        fixture.Requests.Should().HaveCount(2);
        var fields = ReadRequestInput(fixture.Requests[1]).GetProperty("repair_fields");
        fields.EnumerateObject().Select(field => field.Name).Should().BeEquivalentTo(new[] { "title", "description" });
        (await File.ReadAllBytesAsync(fixture.InfoPath)).Should().Equal(previous);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(80)]
    public async Task English_title_and_synopsis_at_the_length_limits_are_accepted(int titleLength)
    {
        var title = new string('a', titleLength);
        var synopsis = new string('b', 900);
        using var fixture = new RewriteFixture(false, false, [Reply(title, synopsis)], useEnglish: true);

        var result = await fixture.RunAsync();

        fixture.Requests.Should().ContainSingle();
        result.Title.Should().Be(title);
        result.Synopsis.Should().Be(synopsis);
    }

    [Theory]
    [InlineData("Secrets in the Ledger", "兄妹找到账簿。", "en")]
    [InlineData("寻找失落的账簿", "They find the missing ledger.", "en")]
    [InlineData("Secrets in the Ledger", "", "en")]
    [InlineData("Secrets in the Ledger", "They find the missing ledger.", "zh")]
    [InlineData("Secrets in the Ledger", "They find the missing ledger.", "unsupported")]
    public async Task Invalid_english_publish_text_retries_and_preserves_the_existing_output(
        string title, string synopsis, string language)
    {
        var invalidResponse = Reply(title, synopsis, language);
        using var fixture = new RewriteFixture(false, false, [invalidResponse, invalidResponse], useEnglish: true);
        var previous = await File.ReadAllBytesAsync(fixture.InfoPath);

        var run = () => fixture.RunAsync();
        await run.Should().ThrowAsync<InvalidOperationException>();
        fixture.Requests.Should().HaveCount(2);
        (await File.ReadAllBytesAsync(fixture.InfoPath)).Should().Equal(previous);
    }

    [Fact]
    public async Task English_publish_text_requires_an_original_synopsis_even_in_translation_only_mode()
    {
        using var fixture = new RewriteFixture(false, false, [], useEnglish: true, originalSynopsis: "");
        var run = () => fixture.RunAsync();
        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*原简介*");
        fixture.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Returning_to_the_original_synopsis_replaces_a_saved_translation()
    {
        const string sourceSynopsis = "兄妹在旧店找到一本账簿，并把它交给法庭。";
        using var fixture = new RewriteFixture(false, false, [Reply("寻回旧账簿", "", "zh")],
            originalSynopsis: sourceSynopsis, restoreOriginalSynopsis: true);

        var result = await fixture.RunAsync();

        result.Synopsis.Should().Be(sourceSynopsis);
        (await File.ReadAllTextAsync(fixture.InfoPath)).Should().Contain($"\n简介: {sourceSynopsis}");
    }

    private static JsonElement ReadRequestInput(byte[] request)
    {
        using var envelope = JsonDocument.Parse(request);
        using var input = JsonDocument.Parse(envelope.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        return input.RootElement.Clone();
    }

    private static string Reply(string title, string description, string language = "en") =>
        JsonSerializer.Serialize(new { title, description, themes = new[] { "都市" }, audience = "女", language });

    private sealed class RewriteFixture : IDisposable
    {
        private readonly DirectoryInfo _project = Directory.CreateTempSubdirectory("unified-publish-test-");
        private readonly HttpClient _http;
        private readonly RecordingHandler _handler;
        private readonly ProjectInfoRewriteRequest _request;

        public RewriteFixture(bool rewriteSynopsis, bool followSourceLanguage, IEnumerable<string> responses,
            Action? afterRequest = null, bool useEnglish = false, string originalSynopsis = OriginalSynopsis,
            bool restoreOriginalSynopsis = false)
        {
            InfoPath = Path.Combine(_project.FullName, "短剧信息.txt");
            File.WriteAllText(InfoPath, $"原剧名: The Old Ledger\n新剧名: Previous Title\n简介: {ExistingSynopsis}\n集数: 41\n制作公司: 测试公司\n");
            var config = Path.Combine(_project.FullName, "config.json");
            File.WriteAllText(config, JsonSerializer.Serialize(new
            {
                AiTextEndpoint = "https://example.com/api/v3",
                AiTextApiKey = "test-key",
                AiTextModel = "test-model",
                AiTextSystemPrompt = $"\n  {VisiblePrompt}  \n",
                AiPublishInfoMode = true,
                AiPublishThemeOptions = "都市|逆袭",
                AiPublishThemeCount = 1,
                AiPublishAskAudience = true,
                AiRewriteSynopsis = rewriteSynopsis,
                AiRewriteFollowsSourceLanguage = followSourceLanguage,
            }));
            _request = new ProjectInfoRewriteRequest(_project.FullName, config, InfoPath, Overwrite: true,
                ForbiddenTitles: ["X"], OccupiedExactTitles: ["Occupied title"], OriginalSynopsis: originalSynopsis,
                UseUnifiedPublishPrompt: true, UseEnglishPublishText: useEnglish, RestoreOriginalSynopsis: restoreOriginalSynopsis);
            _handler = new RecordingHandler(responses, afterRequest);
            _http = new HttpClient(_handler);
        }

        public string InfoPath { get; }
        public IReadOnlyList<byte[]> Requests => _handler.Requests;

        public Task<ProjectInfoRewriteResult> RunAsync(CancellationToken cancellationToken = default) =>
            new ProjectInfoRewriter(new TxtProjectInfoParser(), _http, NullLogger<ProjectInfoRewriter>.Instance)
                .RewriteAsync(_request, cancellationToken);

        public void Dispose()
        {
            _http.Dispose();
            _project.Delete(recursive: true);
        }
    }

    private sealed class RecordingHandler(IEnumerable<string> responses, Action? afterRequest) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);
        public List<byte[]> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            afterRequest?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
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
