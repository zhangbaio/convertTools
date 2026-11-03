using System.Text.Json;
using PlatformPublisher.Common.Models;
using Xunit;
using PlatformPublisher.Kuaishou.Publishing;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouExportedConfigMergerTests
{
    [Fact]
    public void Empty_exported_token_keeps_the_existing_token()
    {
        using var settings = JsonDocument.Parse("""
            {
              "kuaishou_app_id": "app-new",
              "kuaishou_access_token": ""
            }
            """);
        using var profile = JsonDocument.Parse("""
            {
              "id": "profile-1",
              "name": "张莉",
              "kuaishou_personal_username": "zhang",
              "kuaishou_personal_last_workspace": "E:\\kuaishou"
            }
            """);
        var config = new KuaishouPersonalConfig
        {
            AccessToken = "keep-me",
            AppId = "app-old",
        };

        KuaishouExportedConfigMerger.Apply(
            settings.RootElement,
            profile.RootElement,
            config,
            PublishPlatform.KuaishouPersonalRevenue);

        Assert.Equal("keep-me", config.AccessToken);
        Assert.Equal("app-new", config.AppId);
        Assert.Equal(@"E:\kuaishou", config.LastWorkspace);
        Assert.Equal("zhang", config.Username);
    }

    [Fact]
    public void Package_rejects_a_file_from_another_app()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"app":"other","settings":{}}""");
            var error = Assert.Throws<InvalidOperationException>(() => KuaishouSettingsExportPackage.Open(path));
            Assert.Contains("微信工具", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
