using FluentAssertions;
using ShortDrama.Desktop.Services;
using System.Text;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Desktop;

public sealed class HongguoMemoryReaderServiceTests
{
    [Fact]
    public void ExtractDeviceId_Should_Find_Hongguo_DeviceId()
    {
        var bytes = Encoding.Latin1.GetBytes("prefix HG0123456789ABCDEF suffix");

        var deviceId = HongguoMemoryReaderService.ExtractDeviceId(bytes);

        deviceId.Should().Be("HG0123456789ABCDEF");
    }

    [Fact]
    public void ExtractFanqieCookie_Should_Find_Runtime_Cookie()
    {
        var bytes = Encoding.Latin1.GetBytes(
            "noise install_id=12345; ttreq=abc; odin_tt=token-value; passport_csrf_token=csrf");

        var cookie = HongguoMemoryReaderService.ExtractFanqieCookie(bytes);

        cookie.Should().Be("install_id=12345; ttreq=abc; odin_tt=token-value; passport_csrf_token=csrf");
    }
}
