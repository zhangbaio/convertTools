using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public sealed class DramaSourceConfigurationValidatorTests
{
    [Fact]
    public void HgnewReportsEveryMissingRequiredField()
    {
        var status = DramaSourceConfigurationValidator.Check(new ClientSettings
        {
            DramaSourceChain = "hgnew",
        });

        Assert.False(status.IsConfigured);
        Assert.Contains("账号", status.Message);
        Assert.Contains("密码", status.Message);
        Assert.Contains("UDID/DeviceId", status.Message);
    }

    [Fact]
    public void HgnewIsConfiguredWhenRequiredFieldsExist()
    {
        var status = DramaSourceConfigurationValidator.Check(new ClientSettings
        {
            DramaSourceChain = "hgnew",
            HgnewAccount = "account",
            HgnewPassword = "password",
            HgnewUdid = "device",
        });

        Assert.True(status.IsConfigured);
        Assert.Contains("已配置", status.Message);
    }

    [Fact]
    public void LocalSourceRequiresServiceAddress()
    {
        var status = DramaSourceConfigurationValidator.Check(new ClientSettings
        {
            DramaSourceChain = "hglocal",
        });

        Assert.False(status.IsConfigured);
        Assert.Contains("本地服务地址", status.Message);
    }

    [Fact]
    public void DownloaderSourceRequiresAddressAndAllowsAutomaticLocalKeyDiscovery()
    {
        var missing = DramaSourceConfigurationValidator.Check(new ClientSettings
        {
            DramaSourceChain = "downloader",
            DownloaderApiBaseUrl = "",
        });
        Assert.False(missing.IsConfigured);
        Assert.Contains("下载器地址", missing.Message);

        var configured = DramaSourceConfigurationValidator.Check(new ClientSettings
        {
            DramaSourceChain = "downloader",
            DownloaderApiBaseUrl = "http://127.0.0.1:17891",
            DownloaderApiKey = ""
        });
        Assert.True(configured.IsConfigured);
    }

    [Theory]
    [InlineData("hgnew")]
    [InlineData("mapleleaf")]
    [InlineData("downloader")]
    [InlineData("unknown")]
    [InlineData(null)]
    public void RetiredAndUnknownChainsBecomePlaylet(string? chain)
    {
        Assert.Equal("playlet", VisibleDramaSource.Normalize(chain));
        Assert.Equal(chain is "hgnew" or "mapleleaf" or "downloader", VisibleDramaSource.IsRetired(chain));
    }

    [Fact]
    public void VisibleChainsStaySelectable()
    {
        Assert.Equal("hghigh", VisibleDramaSource.Normalize("hghigh"));
        Assert.Equal("hglocal", VisibleDramaSource.Normalize("hglocal"));
        Assert.Equal("pikachu", VisibleDramaSource.Normalize("pikachu"));
        Assert.Equal("playlet", VisibleDramaSource.Normalize("playlet"));
        Assert.Equal("dramabox", VisibleDramaSource.Normalize("dramabox"));
        Assert.Equal("foreign", VisibleDramaSource.Normalize("foreign"));
    }

    [Fact]
    public void ForeignSourceRequiresServerAndDevice()
    {
        var missing = DramaSourceConfigurationValidator.Check(new ClientSettings
        {
            DramaSourceChain = "foreign",
            ForeignServerUrl = "https://startvlog.cn/start-prod-api",
        });
        Assert.False(missing.IsConfigured);
        Assert.Contains("设备号", missing.Message);

        var configured = DramaSourceConfigurationValidator.Check(new ClientSettings
        {
            DramaSourceChain = "foreign",
            ForeignServerUrl = "https://startvlog.cn/start-prod-api",
            ForeignDeviceId = "DC8D94CB6E7122F92E",
        });
        Assert.True(configured.IsConfigured);
        Assert.Contains("海外短剧下载器", configured.Message);
    }

    [Fact]
    public void PlayletSourceRequiresServerAndDevice()
    {
        var missing = DramaSourceConfigurationValidator.Check(new ClientSettings
        {
            DramaSourceChain = "playlet",
            PlayletServerUrl = "https://startvlog.cn/start-prod-api",
        });
        Assert.False(missing.IsConfigured);
        Assert.Contains("设备号", missing.Message);

        var configured = DramaSourceConfigurationValidator.Check(new ClientSettings
        {
            DramaSourceChain = "playlet",
            PlayletServerUrl = "https://startvlog.cn/start-prod-api",
            PlayletDeviceId = "DC8D94CB6E7122F92E",
        });
        Assert.True(configured.IsConfigured);
        Assert.Contains("红果短剧下载器", configured.Message);
    }
}
