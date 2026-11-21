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
}
