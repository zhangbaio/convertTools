using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class ClientSettingsNetworkPreferenceTests
{
    [Theory]
    [InlineData("ipv4")]
    [InlineData("ipv6")]
    public void Saving_settings_removes_the_legacy_network_preference(string preference)
    {
        var directory = Directory.CreateTempSubdirectory("client-settings-network-");
        var databasePath = Path.Combine(directory.FullName, "app.db");
        try
        {
            AppSettingStore.SaveJson(
                ClientSettingsStore.SettingsKey,
                new Dictionary<string, string> { ["network_address_preference"] = preference },
                databasePath);

            ClientSettingsStore.Save(ClientSettingsStore.Load(databasePath), databasePath);

            AppSettingStore.TryLoadJson<Dictionary<string, JsonElement>>(
                ClientSettingsStore.SettingsKey, out var saved, databasePath).Should().BeTrue();
            saved!.Should().NotContainKey("network_address_preference");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }
}
