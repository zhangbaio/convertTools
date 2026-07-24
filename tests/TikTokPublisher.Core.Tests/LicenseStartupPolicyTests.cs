using FluentAssertions;
using TikTokPublisher.Core.Licensing;

namespace TikTokPublisher.Core.Tests;

public sealed class LicenseStartupPolicyTests
{
    [Fact]
    public void Decide_PromptsForLogin_WhenNoLocalStateExists()
    {
        LicenseStartupPolicy.Decide(null)
            .Should().Be(LicenseStartupAction.PromptLogin);
    }

    [Fact]
    public void Decide_PromptsForLogin_WhenLocalStateIsNotActivated()
    {
        LicenseStartupPolicy.Decide(new LicenseState
        {
            LicenseKey = "account@example.test",
            MachineId = "machine-a",
        }).Should().Be(LicenseStartupAction.PromptLogin);
    }

    [Fact]
    public void Decide_VerifiesOnline_WhenActivatedStateExists()
    {
        LicenseStartupPolicy.Decide(ActivatedState())
            .Should().Be(LicenseStartupAction.VerifyExistingState);
    }

    [Fact]
    public void Decide_VerifiesOnline_WhenExistingStateIsExpired()
    {
        var state = ActivatedState();
        state.ExpiresAt = "2020-01-01T00:00:00Z";

        LicenseStartupPolicy.Decide(state)
            .Should().Be(LicenseStartupAction.VerifyExistingState);
    }

    private static LicenseState ActivatedState() => new()
    {
        LicenseKey = "account@example.test",
        MachineId = "machine-a",
        Token = "token",
    };
}
