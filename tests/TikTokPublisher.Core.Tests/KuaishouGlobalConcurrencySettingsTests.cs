using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class KuaishouGlobalConcurrencySettingsTests
{
    [Fact]
    public void DefaultsToFourAndPreservesPreviouslyEffectiveCapacity()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "settings.db");
            Assert.Equal(4, ClientSettingsStore.Load(path).KuaishouGlobalProjectConcurrency);
            ClientSettingsStore.Save(new ClientSettings { TiktokGlobalProjectConcurrency = 7 }, path);

            var settings = ClientSettingsStore.Load(path);
            Assert.Equal(7, settings.KuaishouGlobalProjectConcurrency);
            settings.TiktokGlobalProjectConcurrency = 9;
            ClientSettingsStore.Save(settings, path);
            var saved = ClientSettingsStore.Load(path);
            Assert.Equal(9, saved.TiktokGlobalProjectConcurrency);
            Assert.Equal(7, saved.KuaishouGlobalProjectConcurrency);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }
}
