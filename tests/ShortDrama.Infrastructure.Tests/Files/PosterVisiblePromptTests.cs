using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ShortDrama.Core.Models;
using ShortDrama.Core.Posters;
using ShortDrama.Infrastructure.Files;
using ShortDrama.Infrastructure.Parsing;
using ShortDrama.Infrastructure.Process;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Files;

public sealed class PosterVisiblePromptTests
{
    private const string Title = "L’été, My Second Chance!";
    private static readonly Rgba32 GeneratedColor = new(175, 80, 40);

    [Fact]
    public async Task Direct_mode_retries_the_exact_visible_prompt_and_original_image()
    {
        using var project = new PosterProject(PosterGenerationTemplates.DirectMode);
        var original = File.ReadAllBytes(project.InputPath);
        var previous = File.ReadAllBytes(project.OutputPath);
        var generated = CreatePng(96, 128, GeneratedColor);
        using var handler = new RecordingImageHandler((attempt, _) =>
        {
            File.ReadAllBytes(project.OutputPath).Should().Equal(previous);
            return attempt == 1 ? TemporaryFailure() : ImageResponse(generated);
        });
        using var http = new HttpClient(handler);

        var result = await CreateRenamer(http).RenameAsync(project.Request, CancellationToken.None);

        result.PosterName.Should().Be(Title);
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Should().Be(handler.Requests[0]);
        AssertImageRequest(handler.Requests[0], original,
            PosterGenerationTemplates.DirectPrompt.Replace("{title}", Title).Replace("{ratio}", "3:4"));
        using var image = Image.Load<Rgba32>(project.OutputPath);
        (image.Width, image.Height).Should().Be((96, 128));
        image[0, 0].Should().Be(GeneratedColor);
        File.ReadAllBytes(project.InputPath).Should().Equal(original);
    }

    [Fact]
    public async Task Plain_text_body_is_sent_verbatim_with_title_and_size_for_direct_mode()
    {
        using var project = new PosterProject(PosterGenerationTemplates.DirectMode);
        const string custom = "  使用自定义构图。\n仅保留用户写入的字面符号 {title} 和 {ratio}。  ";
        var generated = CreatePng(96, 128, GeneratedColor);
        using var handler = new RecordingImageHandler((attempt, _) =>
            attempt == 1 ? TemporaryFailure() : ImageResponse(generated));
        using var http = new HttpClient(handler);
        await CreateRenamer(http).RenameAsync(project.Request with { PlainTextImagePrompt = custom }, CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Should().Be(handler.Requests[0]);
        AssertImageRequest(handler.Requests[0], File.ReadAllBytes(project.InputPath),
            $"{custom}\n\n本次海报剧名为《{Title}》。");
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        body.RootElement.GetProperty("size").GetString().Should().Be("96x128");
        PosterGenerationTemplates.ComposePlainText(PosterGenerationTemplates.EraseMode, custom, Title).Should().Be(custom);
    }

    [Fact]
    public async Task PortraitAndLandscapeCanUseAnInMemoryAccountConfiguration()
    {
        using var project = new PosterProject(PosterGenerationTemplates.DirectMode);
        var png = CreatePng(96, 128, GeneratedColor);
        using var handler = new RecordingImageHandler((_, _) => ImageResponse(png));
        using var http = new HttpClient(handler);
        var config = new Dictionary<string, string>
        {
            ["PosterMode"] = PosterGenerationTemplates.DirectMode,
            ["ImageModelEndpoint"] = "https://model.example/v1",
            ["ImageEditPath"] = "/images/generations",
            ["ImageModelId"] = "account-image-model",
            ["ImageModelApiKey"] = "synthetic-account-key",
            ["ImageSize"] = "96x128",
        };
        var renamer = new PosterRenamer(new TxtProjectInfoParser(), new ExternalProcessRunner(),
            http, NullLogger<PosterRenamer>.Instance, config);

        await renamer.RenameAsync(project.Request with { ConfigFile = null, PlainTextImagePrompt = "账号竖屏提示词" },
            CancellationToken.None);
        var landscape = Path.Combine(project.Request.ProjectDir, "横屏封面.jpg");
        await renamer.GenerateSizedCoverAsync("", project.OutputPath, landscape, Title, "账号横屏提示词",
            "重试", 160, 100, "128x96", false, false, null, CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests.Select(request => request.Key).Should().OnlyContain(key => key == "synthetic-account-key");
        GetPrompt(handler.Requests[0]).Should().Contain("账号竖屏提示词");
        GetPrompt(handler.Requests[1]).Should().Contain("账号横屏提示词");
        var info = await Image.IdentifyAsync(landscape);
        (info.Width, info.Height).Should().Be((160, 100));
    }

    [Fact]
    public async Task A_second_request_failure_preserves_the_previous_poster()
    {
        using var project = new PosterProject(PosterGenerationTemplates.DirectMode);
        var previous = File.ReadAllBytes(project.OutputPath);
        using var handler = new RecordingImageHandler((_, _) => TemporaryFailure());
        using var http = new HttpClient(handler);

        var generate = () => CreateRenamer(http).RenameAsync(project.Request, CancellationToken.None);
        await generate.Should().ThrowAsync<HttpRequestException>().WithMessage("*503*");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Should().Be(handler.Requests[0]);
        File.ReadAllBytes(project.OutputPath).Should().Equal(previous);
    }

    [Fact]
    public async Task Explicit_provider_moderation_is_reported_without_retry_or_overwrite()
    {
        using var project = new PosterProject(PosterGenerationTemplates.DirectMode);
        var previous = File.ReadAllBytes(project.OutputPath);
        using var handler = new RecordingImageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new { error = new { code = "InputImageSensitiveContentDetected", message = "Moderation rejected the request." } }),
        });
        using var http = new HttpClient(handler);

        var generate = () => CreateRenamer(http).RenameAsync(project.Request, CancellationToken.None);
        await generate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*内容审核*");

        handler.Requests.Should().ContainSingle();
        File.ReadAllBytes(project.OutputPath).Should().Equal(previous);
    }

    [Fact]
    public async Task Blocked_original_switches_to_one_request_with_only_the_synopsis_and_verifies_the_drawn_title()
    {
        const string synopsis = "一群唐代亲友在花园相聚。";
        using var project = new PosterProject(PosterGenerationTemplates.DirectMode,
            sourceWidth: 600, sourceHeight: 800, synopsis: synopsis, configuredSize: "600x800");
        var original = File.ReadAllBytes(project.InputPath);
        var previous = File.ReadAllBytes(project.OutputPath);
        var generated = CreatePng(600, 800, GeneratedColor);
        using var handler = new RecordingImageHandler((attempt, _) =>
        {
            File.ReadAllBytes(project.OutputPath).Should().Equal(previous);
            return attempt switch
            {
                1 => ModerationFailure("InputImageSensitiveContentDetected"),
                2 => ImageResponse(generated),
                _ => TitleVerificationResponse(matches: true),
            };
        });
        using var http = new HttpClient(handler);

        await CreateRenamer(http).RenameAsync(project.Request with { AllowTextOnlyFallback = true }, CancellationToken.None);

        handler.Requests.Select(call => call.Path).Should().Equal(
            "/v1/images/generations", "/v1/images/generations", "/v1/chat/completions");
        using var request = JsonDocument.Parse(handler.Requests[1].Body);
        request.RootElement.TryGetProperty("image", out _).Should().BeFalse();
        request.RootElement.TryGetProperty("stream", out _).Should().BeFalse();
        GetPrompt(handler.Requests[1]).Should().Contain(synopsis).And.Contain("无字短剧海报底图").And.NotContain(Title);
        using var verification = JsonDocument.Parse(handler.Requests[2].Body);
        var parts = verification.RootElement.GetProperty("messages")[0].GetProperty("content");
        parts[0].GetProperty("text").GetString().Should().Contain(Title);
        var imageUrl = parts[1].GetProperty("image_url").GetProperty("url").GetString()!;
        var checkedBytes = Convert.FromBase64String(imageUrl[(imageUrl.IndexOf(',') + 1)..]);
        File.ReadAllBytes(project.OutputPath).Should().Equal(checkedBytes).And.NotEqual(generated);
        File.ReadAllBytes(project.InputPath).Should().Equal(original);
        project.Log.Should().Contain(message => message.Contains("纯文生图海报已通过标题校验"));
        Directory.EnumerateFileSystemEntries(project.Request.ProjectDir, ".text-only-poster-*").Should().BeEmpty();
    }

    [Theory]
    [InlineData("InputTextSensitiveContentDetected", "提示词")]
    [InlineData("OutputImageSensitiveContentDetected", "生成的海报")]
    public async Task Moderation_of_prompt_or_output_does_not_trigger_text_only_generation(string code, string reason)
    {
        using var project = new PosterProject(PosterGenerationTemplates.DirectMode, synopsis: "邻里一起筹备庆典。");
        var previous = File.ReadAllBytes(project.OutputPath);
        using var handler = new RecordingImageHandler((_, _) => ModerationFailure(code));
        using var http = new HttpClient(handler);

        var generate = () => CreateRenamer(http).RenameAsync(
            project.Request with { AllowTextOnlyFallback = true }, CancellationToken.None);
        await generate.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{reason}*内容审核*");

        handler.Requests.Should().ContainSingle();
        File.ReadAllBytes(project.OutputPath).Should().Equal(previous);
    }

    [Theory]
    [InlineData(false, "生成的海报被内容审核拦截")]
    [InlineData(true, "标题校验未通过：标题不同")]
    public async Task Text_only_failure_preserves_files_and_reports_its_own_reason(bool verify, string reason)
    {
        using var project = new PosterProject(PosterGenerationTemplates.DirectMode,
            synopsis: "邻里一起筹备庆典。");
        var original = File.ReadAllBytes(project.InputPath);
        var previous = File.ReadAllBytes(project.OutputPath);
        using var handler = new RecordingImageHandler((attempt, _) => attempt switch
        {
            1 => ModerationFailure("InputImageSensitiveContentDetected"),
            2 when !verify => ModerationFailure("OutputImageSensitiveContentDetected"),
            2 => ImageResponse(CreatePng(96, 128, GeneratedColor)),
            _ => TitleVerificationResponse(matches: false),
        });
        using var http = new HttpClient(handler);

        var generate = () => CreateRenamer(http).RenameAsync(
            project.Request with { AllowTextOnlyFallback = true }, CancellationToken.None);
        await generate.Should().ThrowAsync<InvalidOperationException>().WithMessage($"纯文生图回退失败：*{reason}*");

        handler.Requests.Count(call => call.Path.EndsWith("/images/generations")).Should().Be(2);
        File.ReadAllBytes(project.OutputPath).Should().Equal(previous);
        File.ReadAllBytes(project.InputPath).Should().Equal(original);
        Directory.EnumerateFileSystemEntries(project.Request.ProjectDir, ".text-only-poster-*").Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_synopsis_reports_what_to_supply_without_submitting_another_request()
    {
        using var project = new PosterProject(PosterGenerationTemplates.DirectMode);
        using var handler = new RecordingImageHandler((_, _) => ModerationFailure("InputImageSensitiveContentDetected"));
        using var http = new HttpClient(handler);

        var generate = () => CreateRenamer(http).RenameAsync(
            project.Request with { AllowTextOnlyFallback = true }, CancellationToken.None);
        await generate.Should().ThrowAsync<InvalidOperationException>().WithMessage("纯文生图回退失败：缺少剧集简介*");

        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Text_only_http_failure_is_not_retried()
    {
        using var project = new PosterProject(PosterGenerationTemplates.DirectMode, synopsis: "兄妹筹备庆典。");
        var previous = File.ReadAllBytes(project.OutputPath);
        using var handler = new RecordingImageHandler((attempt, _) => attempt == 1
            ? ModerationFailure("InputImageSensitiveContentDetected")
            : TemporaryFailure());
        using var http = new HttpClient(handler);

        var generate = () => CreateRenamer(http).RenameAsync(
            project.Request with { AllowTextOnlyFallback = true }, CancellationToken.None);
        await generate.Should().ThrowAsync<InvalidOperationException>().WithMessage("纯文生图回退失败：*503*");

        handler.Requests.Should().HaveCount(2);
        File.ReadAllBytes(project.OutputPath).Should().Equal(previous);
    }

    [Fact]
    public async Task Cancellation_during_fallback_verification_does_not_save_the_candidate()
    {
        using var project = new PosterProject(PosterGenerationTemplates.DirectMode, synopsis: "兄妹筹备庆典。");
        using var cancellation = new CancellationTokenSource();
        var previous = File.ReadAllBytes(project.OutputPath);
        using var handler = new RecordingImageHandler((attempt, _) =>
        {
            if (attempt == 1)
                return ModerationFailure("InputImageSensitiveContentDetected");
            if (attempt == 2)
                return ImageResponse(CreatePng(96, 128, GeneratedColor));

            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        using var http = new HttpClient(handler);

        var generate = () => CreateRenamer(http).RenameAsync(
            project.Request with { AllowTextOnlyFallback = true }, cancellation.Token);
        await generate.Should().ThrowAsync<OperationCanceledException>();

        handler.Requests.Should().HaveCount(3);
        File.ReadAllBytes(project.OutputPath).Should().Equal(previous);
        Directory.EnumerateFileSystemEntries(project.Request.ProjectDir, ".text-only-poster-*").Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_does_not_retry_or_overwrite_the_previous_poster()
    {
        using var project = new PosterProject(PosterGenerationTemplates.DirectMode);
        using var cancellation = new CancellationTokenSource();
        var previous = File.ReadAllBytes(project.OutputPath);
        using var handler = new RecordingImageHandler((_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        using var http = new HttpClient(handler);

        var generate = () => CreateRenamer(http).RenameAsync(project.Request, cancellation.Token);
        await generate.Should().ThrowAsync<OperationCanceledException>();

        handler.Requests.Should().ContainSingle();
        File.ReadAllBytes(project.OutputPath).Should().Equal(previous);
    }

    [Fact]
    public async Task Erase_mode_sends_no_title_to_the_model_and_draws_it_on_the_original_canvas()
    {
        using var project = new PosterProject(PosterGenerationTemplates.EraseMode, sourceWidth: 600, sourceHeight: 750);
        var original = File.ReadAllBytes(project.InputPath);
        var generated = CreatePng(600, 750, new Rgba32(20, 20, 20));
        using var handler = new RecordingImageHandler((_, _) => ImageResponse(generated));
        using var http = new HttpClient(handler);

        await CreateRenamer(http).RenameAsync(project.Request, CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        AssertImageRequest(handler.Requests[0], original, PosterGenerationTemplates.ErasePrompt.Replace("{ratio}", "4:5"));
        GetPrompt(handler.Requests[0]).Should().NotContain(Title);
        using var image = Image.Load<Rgba32>(project.OutputPath);
        (image.Width, image.Height).Should().Be((600, 750));
        image[0, 0].Should().Be(new Rgba32(20, 20, 20));
        Enumerable.Range(image.Height / 2, image.Height / 2)
            .Any(y => Enumerable.Range(0, image.Width).Any(x => image[x, y].R > 180)).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reference_mode_only_saves_a_candidate_that_passes_the_difference_gate(bool secondCandidateDiffers)
    {
        using var project = new PosterProject(PosterReferenceCover.Mode);
        File.WriteAllBytes(project.InputPath, CreateStripedPng(horizontal: false));
        var previous = File.ReadAllBytes(project.OutputPath);
        var original = File.ReadAllBytes(project.InputPath);
        var replacement = CreateStripedPng(horizontal: true);
        using var handler = new RecordingImageHandler((attempt, _) => ImageResponse(attempt == 2 && secondCandidateDiffers ? replacement : original));
        using var http = new HttpClient(handler);

        var generate = () => CreateRenamer(http).RenameAsync(project.Request, CancellationToken.None);
        if (secondCandidateDiffers)
        {
            await generate();
            using var image = Image.Load<Rgba32>(project.OutputPath);
            image[3, 18].Should().Be(image[40, 18]);
            image[3, 18].Should().NotBe(image[3, 7]);
        }
        else
        {
            await generate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*40*");
            File.ReadAllBytes(project.OutputPath).Should().Equal(previous);
        }

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Should().Be(handler.Requests[0]);
        AssertImageRequest(handler.Requests[0], original,
            PosterGenerationTemplates.ReferencePrompt.Replace("{title}", Title).Replace("{ratio}", "3:4"));
    }

    [Fact]
    public async Task Optional_title_verification_uses_its_visible_prompt_and_only_reports_a_mismatch()
    {
        using var project = new PosterProject(PosterGenerationTemplates.DirectMode, verifyTitle: true);
        var generated = CreatePng(96, 128, GeneratedColor);
        using var handler = new RecordingImageHandler((attempt, _) => attempt == 1
            ? ImageResponse(generated)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    choices = new[]
                    {
                        new { message = new { content = """{"detectedTitle":"Another Name","matchesTarget":false,"hasResidualText":false,"reason":"标题不同"}""" } },
                    },
                }),
            });
        using var http = new HttpClient(handler);

        await CreateRenamer(http).RenameAsync(project.Request, CancellationToken.None);

        handler.Requests.Select(call => call.Path).Should().Equal("/v1/images/generations", "/v1/chat/completions");
        using var body = JsonDocument.Parse(handler.Requests[1].Body);
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        var prompt = content.EnumerateArray().Single(part => part.GetProperty("type").GetString() == "text");
        prompt.GetProperty("text").GetString().Should().Be(PosterGenerationTemplates.TitleVerificationPrompt.Replace("{title}", Title));
        project.Log.Should().Contain(message => message.Contains("标题校验") && message.Contains("标题不同") && message.Contains("仅报告"));
        using var image = Image.Load<Rgba32>(project.OutputPath);
        image[0, 0].Should().Be(GeneratedColor);
    }

    private static PosterRenamer CreateRenamer(HttpClient http) =>
        new(new TxtProjectInfoParser(), new ExternalProcessRunner(), http, NullLogger<PosterRenamer>.Instance);

    private static void AssertImageRequest(RecordedRequest request, byte[] source, string expectedPrompt)
    {
        request.Path.Should().Be("/v1/images/generations");
        using var body = JsonDocument.Parse(request.Body);
        body.RootElement.GetProperty("prompt").GetString().Should().Be(expectedPrompt);
        body.RootElement.GetProperty("image").GetString().Should().Be($"data:image/png;base64,{Convert.ToBase64String(source)}");
    }

    private static string? GetPrompt(RecordedRequest request)
    {
        using var body = JsonDocument.Parse(request.Body);
        return body.RootElement.GetProperty("prompt").GetString();
    }

    private static HttpResponseMessage TemporaryFailure() => new(HttpStatusCode.ServiceUnavailable)
    {
        Content = new StringContent("Service temporarily unavailable."),
    };

    private static HttpResponseMessage ImageResponse(byte[] png) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { data = new[] { new { b64_json = Convert.ToBase64String(png) } } }),
    };

    private static HttpResponseMessage ModerationFailure(string code) => new(HttpStatusCode.BadRequest)
    {
        Content = JsonContent.Create(new { error = new { code } }),
    };

    private static HttpResponseMessage TitleVerificationResponse(bool matches) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            choices = new[] { new { message = new { content = JsonSerializer.Serialize(new
            {
                detectedTitle = matches ? Title : "Another Name",
                matchesTarget = matches,
                hasResidualText = false,
                reason = matches ? "" : "标题不同",
            }) } } },
        }),
    };

    private static byte[] CreatePng(int width, int height, Rgba32 color)
    {
        using var image = new Image<Rgba32>(width, height, color);
        return EncodePng(image);
    }

    private static byte[] CreateStripedPng(bool horizontal)
    {
        using var image = new Image<Rgba32>(96, 128);
        image.ProcessPixelRows(pixels =>
        {
            for (var y = 0; y < pixels.Height; y++)
            {
                var row = pixels.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var stripe = (horizontal ? y : x) / 7 % 2;
                    var level = (byte)(25 + stripe * 195);
                    row[x] = new Rgba32(level, level, level);
                }
            }
        });
        return EncodePng(image);
    }

    private static byte[] EncodePng(Image<Rgba32> image)
    {
        using var bytes = new MemoryStream();
        image.SaveAsPng(bytes);
        return bytes.ToArray();
    }

    private sealed record RecordedRequest(string Path, string Body, string? Key);

    private sealed class RecordingImageHandler(Func<int, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(path, body, request.Headers.Authorization?.Parameter));
            return respond(Requests.Count, path);
        }
    }

    private sealed class PosterProject : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("poster-visible-prompt-");

        public PosterProject(string mode, bool verifyTitle = false, int sourceWidth = 96, int sourceHeight = 128,
            string? synopsis = null, string configuredSize = "96x128")
        {
            InputPath = Path.Combine(_directory.FullName, "海报原图.png");
            OutputPath = Path.Combine(_directory.FullName, "海报图片.png");
            var configPath = Path.Combine(_directory.FullName, "poster.json");
            File.WriteAllBytes(InputPath, CreatePng(sourceWidth, sourceHeight, new Rgba32(15, 30, 55)));
            File.WriteAllBytes(OutputPath, CreatePng(sourceWidth, sourceHeight, new Rgba32(55, 20, 10)));
            File.WriteAllText(Path.Combine(_directory.FullName, "短剧信息.txt"), $"原剧名：原剧\n新剧名：{Title}\n简介：{synopsis}");
            File.WriteAllText(configPath, JsonSerializer.Serialize(new
            {
                PosterMode = mode,
                ImageModelEndpoint = "https://model.example/v1",
                ImageEditPath = "/images/generations",
                ImageModelId = "test-model",
                ImageModelApiKey = "fake-image-key",
                ImageSize = configuredSize,
                DoubaoImageRatio = "3:4",
                PosterLayoutRequestMode = "fast",
                PosterGenerationPrompt = "Legacy hidden generation text",
                PosterGenerationSafeRetryPrompt = "Legacy hidden retry text",
                PosterAllTextErasePrompt = "Legacy hidden erase text",
                PosterTitleVerifyEnabled = verifyTitle,
                PosterTitleVerifyMode = "blocking",
                ChatModelEndpoint = "https://model.example/v1",
                ChatModelId = "test-chat-model",
                ChatModelApiKey = "fake-chat-key",
            }));
            Request = new PosterRenameRequest(_directory.FullName, InputPath, OutputPath, configPath,
                Overwrite: true, Log: Log.Add, UseVisibleImagePrompt: true);
        }

        public string InputPath { get; }
        public string OutputPath { get; }
        public List<string> Log { get; } = [];
        public PosterRenameRequest Request { get; }

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
