using FluentAssertions;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class ClientSettingsNetworkPreferenceTests
{
    [Fact]
    public void Network_preference_defaults_to_system_and_survives_clone()
    {
        new ClientSettings().NetworkAddressPreference.Should().Be("system");

        var clone = new ClientSettings
        {
            NetworkAddressPreference = "ipv4",
            PosterLayoutRequestMode = "fast",
        }.Clone();

        clone.NetworkAddressPreference.Should().Be("ipv4");
        clone.PosterLayoutRequestMode.Should().Be("fast");
    }

    [Theory]
    [InlineData(" IPv4 ", "ipv4")]
    [InlineData("IPV6", "ipv6")]
    [InlineData("", "system")]
    [InlineData("unknown", "system")]
    public void Store_normalizes_network_preference(string input, string expected)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"client-settings-network-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(tempDir, "app.db");
        Directory.CreateDirectory(tempDir);
        try
        {
            ClientSettingsStore.Save(new ClientSettings { NetworkAddressPreference = input }, databasePath);

            ClientSettingsStore.Load(databasePath).NetworkAddressPreference.Should().Be(expected);
        }
        finally
        {
            ShortDrama.Infrastructure.Networking.PreferredNetworkHttp.Preference =
                ShortDrama.Infrastructure.Networking.NetworkAddressPreference.System;
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); }
            catch (IOException) { }
        }
    }
}
