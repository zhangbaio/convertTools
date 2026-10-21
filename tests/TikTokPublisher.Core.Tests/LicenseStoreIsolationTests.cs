using FluentAssertions;
using TikTokPublisher.Core.Licensing;

namespace TikTokPublisher.Core.Tests;

public sealed class LicenseStoreIsolationTests
{
    [Fact]
    public void UseStateDirectory_KeepsLoginStateOutOfTheMainAppFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"registration-pdf-license-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var previous = LicenseStore.StateDirectory;
        try
        {
            LicenseStore.UseStateDirectory(directory);
            LicenseStore.Save(new LicenseState
            {
                LicenseKey = "registration-pdf-only",
                AccountUsername = "pdf-user",
                MachineId = "machine",
                Token = "token",
                BoundLicenseKey = "PDF-CODE",
                BoundLicenseToken = "code-token",
            });

            File.Exists(Path.Combine(directory, "license_state.bin")).Should().BeTrue();
            var loaded = LicenseStore.Load();
            loaded.AccountUsername.Should().Be("pdf-user");
            loaded.BoundLicenseKey.Should().Be("PDF-CODE");
            loaded.BoundLicenseToken.Should().Be("code-token");
        }
        finally
        {
            LicenseStore.UseStateDirectory(previous.EndsWith(".tiktok_publisher", StringComparison.OrdinalIgnoreCase)
                ? null
                : previous);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
