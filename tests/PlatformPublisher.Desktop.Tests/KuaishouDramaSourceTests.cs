using PlatformPublisher.Desktop.Services;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using Xunit;

namespace PlatformPublisher.Desktop.Tests;

public sealed class KuaishouDramaSourceTests
{
    [Fact]
    public void Kuaishou_uses_the_saved_domestic_chain_when_TikTok_selects_an_overseas_account()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kuaishou-drama-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "settings.db");
            var settings = new ClientSettings
            {
                DramaSourceChain = "playlet",
                OverseasDramaSourceChain = "dramabox",
            };
            ClientSettingsStore.Save(settings, database);
            var kuaishou = new YunfanDramaSettingsProvider(database);
            var tikTok = new ClientSettingsDramaSettingsProvider(settings, database);
            tikTok.Replace(VisibleDramaSource.SettingsFor(settings, TikTokAccountProfile.DramaMarketOverseas));

            Assert.Equal("dramabox", tikTok.Get().DramaSourceChain);
            Assert.Equal("playlet", kuaishou.Get().DramaSourceChain);
            Assert.Equal("playlet", DramaSourceConfigurationValidator.Check(kuaishou.LoadClientSettings()).Source);
            settings.DramaSourceChain = "hongguoapp";
            ClientSettingsStore.Save(settings, database);

            Assert.Equal("hongguoapp", kuaishou.Get().DramaSourceChain);
            Assert.Equal("dramabox", tikTok.Get().DramaSourceChain);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Embedded_kuaishou_reads_the_selected_domestic_chain()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kuaishou-host-chain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var yunfanDatabase = Path.Combine(directory, "yunfan.db");
            var tikTokDatabase = Path.Combine(directory, "tiktok.db");
            ClientSettingsStore.Save(new ClientSettings
            {
                DramaSourceChain = "playlet",
                OverseasDramaSourceChain = "dramabox",
            }, yunfanDatabase);
            ClientSettingsStore.Save(new ClientSettings
            {
                DramaSourceChain = "hongguoapp",
                OverseasDramaSourceChain = "dramabox",
                HongguoAppDeviceId = "device-1",
            }, tikTokDatabase);

            var yunfan = new YunfanDramaSettingsProvider(yunfanDatabase);
            var host = new YunfanDramaSettingsProvider(tikTokDatabase);

            Assert.Equal("playlet", yunfan.Get().DramaSourceChain);
            Assert.Equal("hongguoapp", host.Get().DramaSourceChain);
            Assert.Equal("hongguoapp", host.LoadClientSettings().DramaSourceChain);
            Assert.Equal("dramabox", host.LoadClientSettings().OverseasDramaSourceChain);
            Assert.Equal("device-1", host.Get().HongguoAppDeviceId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
