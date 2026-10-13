using System.Net;
using FluentAssertions;
using ShortDrama.Infrastructure.Networking;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Networking;

public sealed class PreferredNetworkHttpTests
{
    private static readonly IPAddress Ipv6 = IPAddress.Parse("2406:d440:104:e715:0:ea35:c4f7:6e2");
    private static readonly IPAddress Ipv4 = IPAddress.Parse("180.184.47.154");

    [Fact]
    public void OrderAddresses_puts_ipv4_first_and_keeps_ipv6_as_fallback()
    {
        PreferredNetworkHttp.OrderAddresses([Ipv6, Ipv4], NetworkAddressPreference.Ipv4)
            .Should().Equal(Ipv4, Ipv6);
    }

    [Fact]
    public void OrderAddresses_puts_ipv6_first_and_keeps_ipv4_as_fallback()
    {
        PreferredNetworkHttp.OrderAddresses([Ipv4, Ipv6], NetworkAddressPreference.Ipv6)
            .Should().Equal(Ipv6, Ipv4);
    }

    [Theory]
    [InlineData(null, NetworkAddressPreference.System)]
    [InlineData("", NetworkAddressPreference.System)]
    [InlineData("SYSTEM", NetworkAddressPreference.System)]
    [InlineData("ipv4", NetworkAddressPreference.Ipv4)]
    [InlineData(" IPv6 ", NetworkAddressPreference.Ipv6)]
    [InlineData("unknown", NetworkAddressPreference.System)]
    public void Parse_defaults_to_system(string? value, NetworkAddressPreference expected)
    {
        PreferredNetworkHttp.Parse(value).Should().Be(expected);
    }
}
