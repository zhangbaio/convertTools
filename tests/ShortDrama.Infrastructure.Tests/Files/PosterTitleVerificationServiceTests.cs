using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ShortDrama.Infrastructure.Files;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Files;

public sealed class PosterTitleVerificationServiceTests
{
    [Fact]
    public async Task VerifyAsync_rejects_when_vision_model_detects_no_title()
    {
        var result = await VerifyAsync(new
        {
            detectedTitle = "",
            matchesTarget = false,
            containsTraditional = false,
            containsVariant = false,
            usesArtisticStyle = false,
            usesAggressiveDecorations = false,
            hasResidualText = false,
            residualText = "",
            reason = "未检测到主标题文字",
        });

        result.Ok.Should().BeFalse();
        result.IsInconclusive.Should().BeTrue();
        result.Reason.Should().Contain("不能确认标题正确");
    }

    [Fact]
    public async Task VerifyAsync_uses_local_exact_match_when_model_match_flag_is_false()
    {
        var result = await VerifyAsync(new
        {
            detectedTitle = "八零年代林场携手创富兴家",
            matchesTarget = false,
            containsTraditional = false,
            containsVariant = false,
            usesArtisticStyle = false,
            usesAggressiveDecorations = false,
            hasResidualText = false,
            residualText = "",
            reason = "",
        });

        result.Ok.Should().BeTrue();
        result.IsInconclusive.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyAsync_ignores_wrapping_book_title_marks_in_transcription()
    {
        var result = await VerifyAsync(new
        {
            detectedTitle = "《八零年代林场携手创富兴家》",
            matchesTarget = true,
            containsTraditional = false,
            containsVariant = false,
            usesArtisticStyle = false,
            usesAggressiveDecorations = false,
            hasResidualText = false,
            residualText = "",
            reason = "",
        });

        result.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_rejects_an_exact_title_when_old_subtitle_text_remains()
    {
        var result = await VerifyAsync(new
        {
            detectedTitle = "八零年代林场携手创富兴家",
            matchesTarget = true,
            containsTraditional = false,
            containsVariant = false,
            usesArtisticStyle = false,
            usesAggressiveDecorations = false,
            hasResidualText = true,
            residualText = "第三季",
            reason = "标题正确但有残留季数",
        });

        result.Ok.Should().BeFalse();
        result.IsInconclusive.Should().BeFalse();
        result.Reason.Should().Contain("第三季");
    }

    [Fact]
    public async Task VerifyAsync_marks_http_failure_as_inconclusive()
    {
        var result = await VerifyAsync(new { }, HttpStatusCode.TooManyRequests);

        result.Ok.Should().BeFalse();
        result.IsInconclusive.Should().BeTrue();
        result.Reason.Should().Contain("429");
    }

    private static async Task<PosterTitleVerifyResult> VerifyAsync(
        object verificationPayload,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"poster-verify-{Guid.NewGuid():N}.png");
        try
        {
            using (var image = new Image<Rgba32>(600, 900, Color.White))
                await image.SaveAsPngAsync(imagePath);

            var visionJson = JsonSerializer.Serialize(verificationPayload);
            var apiResponse = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new { message = new { content = visionJson } },
                },
            });
            using var httpClient = new HttpClient(new StubHandler(apiResponse, statusCode));
            var service = new PosterTitleVerificationService(httpClient);
            var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ChatModelEndpoint"] = "https://example.invalid/v1",
                ["ChatModelId"] = "vision-test",
                ["ChatModelApiKey"] = "test-key",
            };
            var layout = new PosterTitleLayout(
                X: 0.15f,
                Y: 0.70f,
                Width: 0.70f,
                Height: 0.15f,
                FontScale: 0.08f,
                TextColor: new Rgba32(246, 232, 90),
                BackgroundColor: new Rgba32(26, 26, 26),
                BackgroundOpacity: 0,
                Align: HorizontalAlignment.Center);

            return await service.VerifyAsync(
                config,
                imagePath,
                "八零年代林场携手创富兴家",
                layout,
                CancellationToken.None);
        }
        finally
        {
            if (File.Exists(imagePath))
                File.Delete(imagePath);
        }
    }

    private sealed class StubHandler(string responseBody, HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
    }
}
