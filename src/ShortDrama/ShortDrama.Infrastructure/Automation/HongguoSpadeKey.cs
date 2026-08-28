using System.Numerics;
using System.Text;

namespace ShortDrama.Infrastructure.Automation;

internal static class HongguoSpadeKey
{
    public static IReadOnlyList<string> UnwrapCandidates(string? spadeABase64)
    {
        if (string.IsNullOrWhiteSpace(spadeABase64))
            return [];

        byte[] spade;
        try
        {
            spade = Convert.FromBase64String(spadeABase64.Trim());
        }
        catch (FormatException)
        {
            return [];
        }

        var keys = new List<string>(2);
        foreach (var flag in new[] { false, true })
        {
            var key = UnwrapV1(spade, flag);
            if (key is { Length: 32 } && key.All(IsLowerHex) && !keys.Contains(key, StringComparer.Ordinal))
                keys.Add(key);
        }
        return keys;
    }

    private static string? UnwrapV1(byte[] spade, bool flag)
    {
        var length = spade.Length;
        if (length < 3)
            return null;
        var typeLengthMarker = spade[0] ^ spade[1] ^ spade[2];
        var typeLength = typeLengthMarker - 0x30;
        if (typeLength < 1)
            return null;
        var workLength = length - typeLengthMarker + 0x2f;
        if (workLength < 1 || 1 + workLength > length || length - typeLength - 2 < 0)
            return null;

        var destination = spade.AsSpan(1, workLength).ToArray();
        var typeBytes = new byte[typeLength];
        var b16 = spade[length - typeLength - 2];
        var b14 = spade[length - typeLength - 1];
        for (var index = 0; index < typeLength; index++)
            typeBytes[index] = (byte)(b14 ^ b16 ^ spade[index + length - typeLength]);
        if (NullTerminatedEquals(typeBytes, "app_v2"u8) || NullTerminatedEquals(typeBytes, "web_v2"u8))
            return null;

        b14 = 0x55;
        b16 = 0xfa;
        for (var index = 0; index < workLength; index++)
        {
            var b6 = destination[index];
            var pop = BitOperations.PopCount((uint)index);
            var b3 = b6;
            var b7 = b14;
            if ((index & 1) != 0)
            {
                b3 = b16;
                b7 = b6;
                b16 = b14;
            }
            var delta = flag ? (int)pop + 0x15 : unchecked((sbyte)(-0x15 - (int)pop));
            destination[index] = unchecked((byte)(delta + (b16 ^ b6)));
            b14 = b7;
            b16 = b3;
        }

        var first = destination[0];
        var tailLength = first switch
        {
            >= (byte)'0' and <= (byte)'9' => first - (byte)'0',
            >= (byte)'a' and <= (byte)'z' => first - 0x57,
            _ => -1
        };
        var end = workLength - tailLength;
        return tailLength >= 0 && end >= 2
            ? Encoding.Latin1.GetString(destination, 1, end - 1)
            : null;
    }

    private static bool NullTerminatedEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        for (var index = 0; index < left.Length; index++)
        {
            var a = left[index];
            var b = index < right.Length ? right[index] : (byte)0;
            if (a != b)
                return false;
            if (a == 0)
                return true;
        }
        return true;
    }

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';
}
