using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class DeletedCopyrightProofProjectRecoveryServiceTests
{
    [Fact]
    public void Source_unavailable_failure_allows_tiktok_video_fallback()
    {
        var result = DeletedCopyrightProofProjectRecoveryService.SourceUnavailable(
            "原片源恢复失败：没有可下载的剧集。");

        result.Ok.Should().BeFalse();
        result.CanFallbackToPublishedVideo.Should().BeTrue();
    }

    [Fact]
    public void Ordinary_recovery_failure_does_not_allow_unsafe_fallback()
    {
        var result = new DeletedCopyrightProofProjectRecoveryResult(
            false,
            "当前队列存在同名冲突");

        result.CanFallbackToPublishedVideo.Should().BeFalse();
    }
}
