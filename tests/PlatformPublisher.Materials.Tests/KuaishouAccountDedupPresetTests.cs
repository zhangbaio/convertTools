using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Persistence;
using TikTokPublisher.Core.Models;
using Xunit;

namespace PlatformPublisher.Materials.Tests;

public sealed class KuaishouAccountDedupPresetTests
{
    [Fact]
    public void AccountPresetsAreIndependentAndSavedPresetsTakePriorityOverLegacyTemplates()
    {
        var root = Directory.CreateTempSubdirectory();
        var database = new PlatformDatabase(Path.Combine(root.FullName, "app.db"));
        try
        {
            var legacy = VideoDedupPreset.CreateDefault();
            legacy.Name = "旧方案";
            legacy.TrimStartSeconds = 2;
            new JsonSettingStore(database).Save("kuaishou.video-dedup.templates",
                new Dictionary<string, VideoDedupPreset> { ["legacy"] = legacy });

            Assert.Equal(legacy.Fingerprint(), KuaishouVideoDedup.LoadPreset("account-a", "legacy", database).Fingerprint());
            var personal = KuaishouVideoDedup.LoadPreset("account-a", "legacy", database);
            personal.TrimStartSeconds = 4;
            KuaishouVideoDedup.SavePreset("account-a", personal, database);

            var enterprise = KuaishouVideoDedup.LoadPreset("account-b", null, database);
            Assert.Equal(0, enterprise.TrimStartSeconds);
            enterprise.TrimStartSeconds = 1;
            KuaishouVideoDedup.SavePreset("account-b", enterprise, database);

            Assert.Equal(4, KuaishouVideoDedup.LoadPreset("account-a", "legacy", database).TrimStartSeconds);
            Assert.Equal(1, KuaishouVideoDedup.LoadPreset("account-b", "legacy", database).TrimStartSeconds);
            Assert.Equal(2, KuaishouVideoDedup.LoadPreset("account-c", "legacy", database).TrimStartSeconds);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }
}
