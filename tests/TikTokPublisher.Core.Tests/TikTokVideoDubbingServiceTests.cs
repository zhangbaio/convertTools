using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokVideoDubbingServiceTests
{
    [Fact]
    public void Volc_translation_can_erase_source_subtitles_during_translation()
    {
        var settings = new ClientSettings
        {
            VideoDubbingProvider = "volc",
            VolcVideoTranslateEraseSourceSubtitle = true,
        };

        TikTokVideoDubbingService.RequiresPriorSubtitleRemoval(settings).Should().BeFalse();
    }

    [Theory]
    [InlineData("volc", false)]
    [InlineData("elevenlabs", true)]
    public void Other_translation_modes_still_require_prior_subtitle_removal(
        string provider,
        bool eraseSourceSubtitle)
    {
        var settings = new ClientSettings
        {
            VideoDubbingProvider = provider,
            VolcVideoTranslateEraseSourceSubtitle = eraseSourceSubtitle,
        };

        TikTokVideoDubbingService.RequiresPriorSubtitleRemoval(settings).Should().BeTrue();
    }

    [Fact]
    public void Volc_upload_concurrency_defaults_to_four()
    {
        new ClientSettings().VolcVideoTranslateUploadConcurrency.Should().Be(4);
    }

    [Theory]
    [InlineData("火山 VOD 返回了无效响应（HTTP 502）：Bad Gateway")]
    [InlineData("火山 VOD 请求失败（HTTP 429）")]
    [InlineData("火山 VOD 请求失败（HTTP 503）")]
    [InlineData("火山 VOD 请求失败（HTTP 504）")]
    public void Transient_volc_gateway_failures_are_retryable(string message)
    {
        TikTokVideoDubbingService.IsTransientVolcFailure(new InvalidOperationException(message))
            .Should().BeTrue();
    }

    [Fact]
    public void Volc_parameter_errors_are_not_retryable()
    {
        TikTokVideoDubbingService.IsTransientVolcFailure(
                new InvalidOperationException("InvalidParameter: FontSize"))
            .Should().BeFalse();
    }
}
