using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokLiveActionDetectionServiceTests
{
    [Fact]
    public void ParseModelResponse_accepts_markdown_wrapped_live_action_json()
    {
        var result = TikTokLiveActionDetectionService.ParseModelResponse(
            """
            ```json
            {"classification":"live_action","confidence":0.91,"reason":"多帧均为真实演员和实景摄影"}
            ```
            """,
            "video-fingerprint");

        result.Classification.Should().Be(LiveActionClassification.LiveAction);
        result.Confidence.Should().BeApproximately(0.91, 0.0001);
        result.Reason.Should().Be("多帧均为真实演员和实景摄影");
        result.VideoFingerprint.Should().Be("video-fingerprint");
    }

    [Theory]
    [InlineData("live_action", 0.79)]
    [InlineData("non_live_action", 0.64)]
    [InlineData("unexpected", 0.99)]
    public void ParseModelResponse_downgrades_low_confidence_or_unknown_results_to_uncertain(
        string classification,
        double confidence)
    {
        var result = TikTokLiveActionDetectionService.ParseModelResponse(
            $$"""{"classification":"{{classification}}","confidence":{{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"reason":"证据不足"}""",
            "fingerprint");

        result.Classification.Should().Be(LiveActionClassification.Uncertain);
    }

    [Fact]
    public void ParseModelResponse_keeps_confident_animation_as_non_live_action()
    {
        var result = TikTokLiveActionDetectionService.ParseModelResponse(
            """{"classification":"non_live_action","confidence":0.88,"reason":"画面为三维动画渲染"}""",
            "fingerprint");

        result.Classification.Should().Be(LiveActionClassification.NonLiveAction);
    }
}
