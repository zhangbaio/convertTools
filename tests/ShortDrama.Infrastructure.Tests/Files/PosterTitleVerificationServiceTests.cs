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
        result.HasResidualText.Should().BeTrue();
        result.ResidualText.Should().Be("第三季");
        result.Reason.Should().Contain("第三季");
    }

    [Fact]
    public async Task VerifyAsync_rejects_residual_transcription_when_model_flag_is_false()
    {
        var result = await VerifyAsync(new
        {
            detectedTitle = "八零年代林场携手创富兴家",
            matchesTarget = true,
            containsTraditional = false,
            containsVariant = false,
            usesArtisticStyle = false,
            usesAggressiveDecorations = false,
            hasResidualText = false,
            residualText = "东方槿、作者老鸭哥",
            reason = "",
        });

        result.Ok.Should().BeFalse();
        result.IsInconclusive.Should().BeFalse();
        result.HasResidualText.Should().BeTrue();
        result.ResidualText.Should().Be("东方槿、作者老鸭哥");
    }

    [Fact]
    public void MergeResidualTextEvidence_keeps_cropped_residual_when_full_image_check_passes()
    {
        var cropped = new PosterTitleVerifyResult(
            false,
            "八零年代林场携手创富兴家",
            "检测到残留人物名",
            HasResidualText: true,
            ResidualText: "东方槿");
        var fullImage = new PosterTitleVerifyResult(
            true,
            "八零年代林场携手创富兴家",
            "");

        var result = PosterRenamer.MergeResidualTextEvidence(cropped, fullImage);

        result.Ok.Should().BeFalse();
        result.IsInconclusive.Should().BeFalse();
        result.HasResidualText.Should().BeTrue();
        result.ResidualText.Should().Be("东方槿");
        result.Reason.Should().Contain("残留人物名");
    }

    [Fact]
    public void MergeResidualTextEvidence_keeps_full_image_residual_when_crop_passes()
    {
        var cropped = new PosterTitleVerifyResult(
            true,
            "八零年代林场携手创富兴家",
            "");
        var fullImage = new PosterTitleVerifyResult(
            false,
            "八零年代林场携手创富兴家",
            "检测到底部作者说明",
            HasResidualText: true,
            ResidualText: "作者老鸭哥");

        var result = PosterRenamer.MergeResidualTextEvidence(cropped, fullImage);

        result.Ok.Should().BeFalse();
        result.HasResidualText.Should().BeTrue();
        result.ResidualText.Should().Be("作者老鸭哥");
    }

    [Fact]
    public void MergeResidualTextEvidence_accepts_full_image_pass_when_crop_only_misses_title()
    {
        var cropped = PosterTitleVerifyResult.Inconclusive("标题裁剪未检测到文字");
        var fullImage = new PosterTitleVerifyResult(
            true,
            "八零年代林场携手创富兴家",
            "");

        var result = PosterRenamer.MergeResidualTextEvidence(cropped, fullImage);

        result.Should().Be(fullImage);
    }

    [Theory]
    [InlineData("东方槿、南宫嫣然、周允儿")]
    [InlineData("改编自番茄小说《逍遥邪少，仙子请自重》作者 老鸭哥")]
    public async Task VerifyAsync_rejects_character_names_and_author_credits_outside_the_target_title(string residualText)
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
            residualText,
            reason = "目标标题正确但全图仍有其他文字",
        });

        result.Ok.Should().BeFalse();
        result.HasResidualText.Should().BeTrue();
        result.ResidualText.Should().Be(residualText);
        result.Reason.Should().Contain(residualText);
    }

    [Fact]
    public async Task VerifyAsync_default_prompt_requires_full_image_non_target_text_detection()
    {
        var capturedPrompt = string.Empty;

        await VerifyAsync(
            new
            {
                detectedTitle = "八零年代林场携手创富兴家",
                matchesTarget = true,
                containsTraditional = false,
                containsVariant = false,
                usesArtisticStyle = false,
                usesAggressiveDecorations = false,
                hasResidualText = false,
                residualText = "",
                reason = "",
            },
            onPrompt: prompt => capturedPrompt = prompt);

        capturedPrompt.Should().Contain("人物或角色姓名");
        capturedPrompt.Should().Contain("作者");
        capturedPrompt.Should().Contain("改编或来源说明");
        capturedPrompt.Should().Contain("水印");
        capturedPrompt.Should().Contain("唯一允许出现");
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
        HttpStatusCode statusCode = HttpStatusCode.OK,
        Action<string>? onPrompt = null)
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
            using var httpClient = new HttpClient(new StubHandler(apiResponse, statusCode, onPrompt));
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

    private sealed class StubHandler(
        string responseBody,
        HttpStatusCode statusCode,
        Action<string>? onPrompt = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (onPrompt is not null && request.Content is not null)
            {
                var requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(requestBody);
                var prompt = document.RootElement
                    .GetProperty("messages")[0]
                    .GetProperty("content")[0]
                    .GetProperty("text")
                    .GetString() ?? string.Empty;
                onPrompt(prompt);
            }

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
