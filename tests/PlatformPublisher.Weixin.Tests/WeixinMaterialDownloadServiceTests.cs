using PlatformPublisher.Weixin.Publishing;
using Xunit;

namespace PlatformPublisher.Weixin.Tests;

public sealed class WeixinMaterialDownloadServiceTests
{
    [Fact]
    public void Parses_system_highlight_and_post_list_payloads()
    {
        var highlight = WeixinMaterialDownloadService.ParsePayloadItems(
            "{\"data\":{\"highlightVideoList\":[{\"exportId\":\"h1\"}]}}",
            "data", "highlightVideoList");
        var posts = WeixinMaterialDownloadService.ParsePayloadItems(
            "{\"data\":{\"list\":[{\"objectId\":\"p1\"},{\"objectId\":\"p2\"}]}}",
            "data", "list");

        Assert.Single(highlight);
        Assert.Equal(2, posts.Count);
    }

    [Fact]
    public void Sanitizes_download_directory_names()
    {
        Assert.Equal("剧名-第一季", WeixinMaterialDownloadService.SafeName("剧名:第一季"));
        Assert.Equal("素材", WeixinMaterialDownloadService.SafeName("   "));
    }
}
