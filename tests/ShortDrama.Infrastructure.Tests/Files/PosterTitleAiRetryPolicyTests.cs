using FluentAssertions;
using ShortDrama.Infrastructure.Files;
using SixLabors.ImageSharp;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Files;

public sealed class PosterTitleAiRetryPolicyTests
{
    [Fact]
    public void ResolveRetryCount_Should_Default_To_One()
    {
        PosterTitleAiRetryPolicy.ResolveRetryCount(new Dictionary<string, string>())
            .Should().Be(1);
        PosterTitleAiRetryPolicy.ResolveRetryCount(new Dictionary<string, string>
            {
                ["PosterTitleVerifyAiRetryCount"] = "invalid",
            })
            .Should().Be(1);
    }

    [Theory]
    [InlineData("-1", 0)]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("3", 3)]
    [InlineData("9", 3)]
    public void ResolveRetryCount_Should_Clamp_To_Supported_Range(string configured, int expected)
    {
        var config = new Dictionary<string, string>
        {
            ["PosterTitleVerifyAiRetryCount"] = configured,
        };

        PosterTitleAiRetryPolicy.ResolveRetryCount(config).Should().Be(expected);
    }

    [Fact]
    public void ShouldRetry_Should_Accept_Semantic_Title_Failure()
    {
        PosterTitleAiRetryPolicy.ShouldRetry(
                new PosterTitleVerifyResult(false, "八零年代林场", "识别标题与目标不一致"))
            .Should().BeTrue();
        PosterTitleAiRetryPolicy.ShouldRetry(
                PosterTitleVerifyResult.Fail("检测到繁体字或残留文字"))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("标题校验接口失败：连接超时")]
    [InlineData("配置缺少必填字段: ChatModelApiKey")]
    [InlineData("标题校验未返回合法 JSON")]
    [InlineData("HTTP 429 Too Many Requests")]
    public void ShouldRetry_Should_Reject_Verifier_Infrastructure_Failure(string reason)
    {
        PosterTitleAiRetryPolicy.ShouldRetry(PosterTitleVerifyResult.Fail(reason))
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldRetry_Should_Reject_Inconclusive_Verification()
    {
        PosterTitleAiRetryPolicy.ShouldRetry(
                PosterTitleVerifyResult.Inconclusive("未检测到主标题文字"))
            .Should().BeFalse();
    }

    [Fact]
    public void BuildTitleCharacterSequence_Should_Keep_Unicode_Characters_Whole()
    {
        PosterTitleAiRetryPolicy.BuildTitleCharacterSequence("八𠀀剧")
            .Should().Be("八 / 𠀀 / 剧");
    }

    [Theory]
    [InlineData("一二三四五六七", "一二三四五六七")]
    [InlineData("一二三四五六七八九十甲乙", "一二三四五六\n七八九十甲乙")]
    [InlineData("一二三四五六七八九十甲乙丙丁戊己", "一二三四五六\n七八九十甲\n乙丙丁戊己")]
    public void FormatTitleForPrompt_Should_Create_Balanced_Lines(string title, string expected)
    {
        PosterTitleAiRetryPolicy.FormatTitleForPrompt(title).Should().Be(expected);
    }

    [Fact]
    public void ComputeCropRectangle_Should_Add_Context_Padding()
    {
        PosterTitleAiRetryPolicy.ComputeCropRectangle(
                imageWidth: 600,
                imageHeight: 858,
                normalizedX: 0.2f,
                normalizedY: 0.7f,
                normalizedWidth: 0.6f,
                normalizedHeight: 0.1f)
            .Should().Be(new Rectangle(69, 552, 462, 182));
    }

    [Fact]
    public void ComputeCropRectangle_Should_Stay_Inside_Image_At_Edge()
    {
        PosterTitleAiRetryPolicy.ComputeCropRectangle(
                imageWidth: 100,
                imageHeight: 200,
                normalizedX: 0,
                normalizedY: 0,
                normalizedWidth: 0.2f,
                normalizedHeight: 0.1f)
            .Should().Be(new Rectangle(0, 0, 44, 44));
    }
}
