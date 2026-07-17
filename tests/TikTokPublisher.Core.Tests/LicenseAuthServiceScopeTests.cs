using FluentAssertions;
using TikTokPublisher.Core.Licensing;

namespace TikTokPublisher.Core.Tests;

public sealed class LicenseAuthServiceScopeTests
{
    [Fact]
    public void IsSameLoginScope_AcceptsSharedCanonicalIdentity()
    {
        var previous = State("https://manage.example/", "machine-a", "old-name", "same@example.test");
        var current = State("https://MANAGE.example", "MACHINE-A", "new-name", "same@example.test");

        LicenseAuthService.IsSameLoginScope(previous, current).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://other.example", "machine-a", "user-a", "a@example.test")]
    [InlineData("https://manage.example", "machine-b", "user-a", "a@example.test")]
    [InlineData("https://manage.example", "machine-a", "user-b", "b@example.test")]
    public void IsSameLoginScope_RejectsDifferentOwnerServerOrMachine(
        string serverUrl,
        string machineId,
        string username,
        string email)
    {
        var previous = State("https://manage.example", "machine-a", "user-a", "a@example.test");
        var current = State(serverUrl, machineId, username, email);

        LicenseAuthService.IsSameLoginScope(previous, current).Should().BeFalse();
    }

    private static LicenseState State(
        string serverUrl,
        string machineId,
        string username,
        string email) => new()
    {
        ServerUrl = serverUrl,
        MachineId = machineId,
        AccountUsername = username,
        Email = email,
        LicenseKey = username,
        Token = "token",
    };
}
