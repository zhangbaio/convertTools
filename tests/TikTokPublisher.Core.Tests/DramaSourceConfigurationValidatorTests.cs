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
}
