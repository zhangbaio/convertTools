using FluentAssertions;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokPlatformSubmitFailureDetectorTests
{
    [Theory]
    [InlineData("操作失败请重试")]
    [InlineData(" 操作失败，请重试 ")]
    [InlineData("操作失败\r\n请重试")]
    public void Detect_recognizes_temporary_platform_submit_failure(string text)
    {
        TikTokPlatformSubmitFailureDetector.Detect(text).Should().NotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("提交成功")]
    [InlineData("当前创建剧集已达上限，请明天再进行操作")]
    public void Detect_ignores_unrelated_feedback(string text)
    {
        TikTokPlatformSubmitFailureDetector.Detect(text).Should().BeNull();
    }

    [Fact]
    public void Retry_message_clearly_attributes_failure_to_platform()
    {
        TikTokPlatformSubmitFailureDetector.BuildRetryMessage("操作失败请重试")
            .Should().Contain("平台端临时异常")
            .And.Contain("稍后重新执行当前项目");
    }
}
