using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using ShortDrama.Infrastructure.Automation;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class HongguoCdnDecryptorTests
{
    [Fact]
    public void UnprotectSampleEntries_Renames_Encv_And_Enca_To_Original_Format()
    {
        var videoEntry = Box("encv", Concat(new byte[78], Box("sinf", Box("frma", "hvc1"u8.ToArray()))));
        var audioEntry = Box("enca", Concat(new byte[28], Box("sinf", Box("frma", "mp4a"u8.ToArray()))));
        var videoStsd = Box("stsd", Concat(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }, videoEntry));
        var audioStsd = Box("stsd", Concat(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }, audioEntry));
        var videoTrak = Box("trak", Box("mdia", Box("minf", Box("stbl", videoStsd))));
        var audioTrak = Box("trak", Box("mdia", Box("minf", Box("stbl", audioStsd))));
        var data = Box("moov", Concat(videoTrak, audioTrak));

        var changed = HongguoCdnDecryptor.UnprotectSampleEntries(data);

        changed.Should().Be(2);
        Encoding.ASCII.GetString(data).Should().NotContain("encv");
        Encoding.ASCII.GetString(data).Should().NotContain("enca");
        CountAscii(data, "hvc1").Should().Be(2);
        CountAscii(data, "mp4a").Should().Be(2);
    }

    private static int CountAscii(byte[] data, string token)
    {
        var needle = Encoding.ASCII.GetBytes(token);
        var count = 0;
        for (var index = 0; index + needle.Length <= data.Length; index++)
        {
            if (data.AsSpan(index, needle.Length).SequenceEqual(needle))
                count++;
        }
        return count;
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
        payload.CopyTo(result, 8);
        return result;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var length = parts.Sum(part => part.Length);
        var result = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
