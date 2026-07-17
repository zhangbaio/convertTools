using FluentAssertions;
using ShortDrama.Infrastructure.Automation;
using System.Buffers.Binary;
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
        var expected = BuildCookie();
        var bytes = Encoding.Latin1.GetBytes(
            $"noise {expected}; passport_csrf_token=csrf");

        var cookie = HongguoMemoryReaderService.ExtractFanqieCookie(bytes);

        cookie.Should().Be(expected);
    }

    [Fact]
    public void ExtractFanqieCookie_Should_Discard_Aardio_Trailing_Control_Bytes()
    {
        var expected = BuildCookie();
        var bytes = Encoding.Latin1.GetBytes($"prefix {expected}\0\b\u0003\u00ffmetadata suffix");

        var cookie = HongguoMemoryReaderService.ExtractFanqieCookie(bytes);

        cookie.Should().Be(expected);
    }

    [Fact]
    public void ExtractFanqieCookie_Should_Prefer_Aardio_Declared_Value()
    {
        var decoy = BuildCookie("11111", 'b', 'a');
        var expected = BuildCookie("67890", 'c', 'd');
        var valueBytes = Encoding.ASCII.GetBytes(expected)
            .Concat(new byte[] { 0, 0xff, 0x08, 0x03 })
            .Concat(Encoding.ASCII.GetBytes("binary metadata"))
            .ToArray();
        var declaredLength = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(declaredLength, (uint)valueBytes.Length);
        var bytes = Encoding.ASCII.GetBytes($"prefix {decoy} suffix")
            .Concat(Encoding.ASCII.GetBytes("cookie\0\b\u0004"))
            .Concat(declaredLength)
            .Concat(valueBytes)
            .ToArray();

        var cookie = HongguoMemoryReaderService.ExtractFanqieCookie(bytes);

        cookie.Should().Be(expected);
    }

    [Fact]
    public void NormalizeFanqieCookie_Should_Repair_Already_Persisted_Dirty_Value()
    {
        var expected = BuildCookie();
        var dirty = $"{expected}\b\u0003\u0004metadata";

        var cookie = HongguoMemoryReaderService.NormalizeFanqieCookie(dirty);

        cookie.Should().Be(expected);
    }

    [Fact]
    public void NormalizeFanqieCookie_Should_Accept_Required_Fields_In_Any_Order()
    {
        var installId = "12345";
        var ttreq = $"1${new string('b', 32)}";
        var odinTt = new string('a', 160);
        var reordered = $"odin_tt={odinTt}; install_id={installId}; ttreq={ttreq}";

        var cookie = HongguoMemoryReaderService.NormalizeFanqieCookie(reordered);

        cookie.Should().Be($"install_id={installId}; ttreq={ttreq}; odin_tt={odinTt}");
    }

    [Fact]
    public void NormalizeFanqieCookie_Should_Accept_Long_Cookie()
    {
        var expected = BuildCookie();
        var longOptionalFields = string.Join(
            "; ",
            Enumerable.Range(0, 80).Select(index => $"optional_{index}={new string('x', 24)}"));
        var longCookie = expected.Replace("; odin_tt=", $"; {longOptionalFields}; odin_tt=", StringComparison.Ordinal);

        var cookie = HongguoMemoryReaderService.NormalizeFanqieCookie(longCookie);

        cookie.Should().Be(expected);
    }

    [Fact]
    public void NormalizeFanqieCookie_Should_Reject_Missing_Required_Field()
    {
        var cookie = HongguoMemoryReaderService.NormalizeFanqieCookie(
            $"install_id=12345; odin_tt={new string('a', 160)}");

        cookie.Should().BeNull();
    }

    private static string BuildCookie(
        string installId = "12345",
        char ttreqCharacter = 'b',
        char odinTtCharacter = 'a') =>
        $"install_id={installId}; ttreq=1${new string(ttreqCharacter, 32)}; " +
        $"odin_tt={new string(odinTtCharacter, 160)}";
}
