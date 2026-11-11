using FluentAssertions;
using TikTokPublisher.Core.Updating;

namespace TikTokPublisher.Core.Tests;

public sealed class ClientUpdateFeedTests
{
    [Fact]
    public void Feed_url_uses_the_tiktok_release_prefix()
    {
        ClientUpdateFeed.BuildFeedUrl("https://kuaishou2.oss-cn-beijing.aliyuncs.com/", "/client-releases/tiktok/")
            .Should().Be("https://kuaishou2.oss-cn-beijing.aliyuncs.com/client-releases/tiktok/");
    }

    [Theory]
    [InlineData("3.23.0", "3.24.0", true)]
    [InlineData("3.24.0", "3.24.0", false)]
    [InlineData("3.24.0", "3.23.0", false)]
    [InlineData("", "3.24.0", true)]
    public void Newer_server_version_is_detected(string current, string remote, bool newer)
    {
        ClientUpdateFeed.IsNewerVersion(current, remote).Should().Be(newer);
    }

    [Fact]
    public void Restart_waits_until_the_queue_is_idle()
    {
        ClientUpdateRuntime.ShouldPromptRestart(downloaded: true, queueRunning: true).Should().BeFalse();
        ClientUpdateRuntime.ShouldPromptRestart(downloaded: true, queueRunning: false).Should().BeTrue();
        ClientUpdateRuntime.ShouldPromptRestart(downloaded: false, queueRunning: false).Should().BeFalse();
    }

    [Fact]
    public void Missing_release_does_not_become_an_offer()
    {
        ClientUpdateFeed.Parse("""{"available":false}""").Should().BeNull();
        var offer = ClientUpdateFeed.Parse(
            """{"available":true,"version":"3.24.0","notes":"修复日志","feedUrl":"https://cdn.example/client-releases/tiktok/","setupUrl":"https://cdn.example/setup.exe"}""");
        offer.Should().NotBeNull();
        offer!.Version.Should().Be("3.24.0");
        offer.FeedUrl.Should().EndWith("/client-releases/tiktok/");
        offer.SetupUrl.Should().EndWith("/setup.exe");
    }
}
