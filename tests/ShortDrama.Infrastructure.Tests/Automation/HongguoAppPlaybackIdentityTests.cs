using ShortDrama.Infrastructure.Automation;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class HongguoAppPlaybackIdentityTests
{
    [Fact]
    public void CompletePairIsNineteenDigitNumbers()
    {
        Assert.True(HongguoAppPlaybackIdentity.IsComplete("3234259167708285450", "1475708354050263501"));
    }

    [Fact]
    public void EmptyOrPartialPairIsNotComplete()
    {
        Assert.False(HongguoAppPlaybackIdentity.IsComplete("", ""));
        Assert.False(HongguoAppPlaybackIdentity.IsComplete("3234259167708285450", ""));
        Assert.False(HongguoAppPlaybackIdentity.IsComplete("0000000000000000000", "1475708354050263501"));
        Assert.False(HongguoAppPlaybackIdentity.IsComplete("DC9C8D5A9B50A4E1D0", "1475708354050263501"));
    }
}
