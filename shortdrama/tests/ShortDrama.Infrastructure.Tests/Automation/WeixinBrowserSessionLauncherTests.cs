using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation.Weixin;
using ShortDrama.Infrastructure.Automation.Weixin.Pages;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class WeixinBrowserSessionLauncherTests
{
    [Fact]
    public async Task LoadLaunchConfigAsync_Should_Use_Config_When_ConfigPath_Is_Provided()
    {
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        var configPath = Path.Combine(projectDir, "weixin-channel-config.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "base_url": "https://channels.weixin.qq.com/custom",
              "auth_file": "./runtime/custom-auth.json",
              "browser": {
                "user_data_dir": "./runtime/custom-profile"
              }
            }
            """);

        var launcher = CreateLauncher();

        var config = await launcher.LoadLaunchConfigAsync(configPath, projectDir, CancellationToken.None);

        config.BaseUrl.Should().Be("https://channels.weixin.qq.com/custom");
        config.AuthFilePath.Should().Be(Path.GetFullPath(Path.Combine(projectDir, "runtime", "custom-auth.json")));
        config.Browser.UserDataDirectory.Should().Be(Path.GetFullPath(Path.Combine(projectDir, "runtime", "custom-profile")));
    }

    [Fact]
    public async Task LoadLaunchConfigAsync_Should_Fallback_To_Defaults_When_Config_Is_Missing()
    {
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        var launcher = CreateLauncher();

        var config = await launcher.LoadLaunchConfigAsync(null, projectDir, CancellationToken.None);

        config.ConfigPath.Should().BeNull();
        config.ConfigDirectory.Should().Be(Path.GetFullPath(projectDir));
        config.BaseUrl.Should().Be("https://channels.weixin.qq.com");
        config.AuthFilePath.Should().NotBeNullOrWhiteSpace();
        config.Browser.UserDataDirectory.Should().NotBeNullOrWhiteSpace();
    }

    private static WeixinBrowserSessionLauncher CreateLauncher()
    {
        return new WeixinBrowserSessionLauncher(
            new WeixinAutomationConfigLoader(),
            new FakeWeixinAuthStateService(),
            new WeixinBrowserRuntimeService(),
            new WeixinHomePage());
    }

    private sealed class FakeWeixinAuthStateService : ShortDrama.Core.Interfaces.IWeixinAuthStateService
    {
        public Task<WeixinAuthStateInfo> ResolveAsync(WeixinAutomationConfig config, CancellationToken cancellationToken)
        {
            return Task.FromResult(new WeixinAuthStateInfo(
                config.AuthFilePath,
                Exists: false,
                IsValidJson: false,
                CookiesCount: 0,
                OriginsCount: 0,
                Message: "unused"));
        }
    }
}
