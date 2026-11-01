using System.Buffers.Binary;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>SM3。搜索和剧集签名的 x-medusa 依赖它。</summary>
internal static class HongguoSm3
{
    private static readonly uint[] Iv =
    [
        0x7380166f, 0x4914b2b9, 0x172442d7, 0xda8a0600,
        0xa96f30bc, 0x163138aa, 0xe38dee4d, 0xb0fb0e4e
    ];

    public static byte[] Hash(ReadOnlySpan<byte> data)
    {
        var bitLen = (ulong)data.Length * 8;
        var paddedLen = ((data.Length + 1 + 8 + 63) / 64) * 64;
        var buf = new byte[paddedLen];
        data.CopyTo(buf);
        buf[data.Length] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(paddedLen - 8), bitLen);

        var v = (uint[])Iv.Clone();
        var w = new uint[68];
        var w1 = new uint[64];
        for (var off = 0; off < paddedLen; off += 64)
        {
            for (var i = 0; i < 16; i++)
                w[i] = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(off + i * 4));
            for (var i = 16; i < 68; i++)
            {
                var x = w[i - 16] ^ w[i - 9] ^ Rotl(w[i - 3], 15);
                w[i] = P1(x) ^ Rotl(w[i - 13], 7) ^ w[i - 6];
            }
            for (var i = 0; i < 64; i++)
                w1[i] = w[i] ^ w[i + 4];

            uint a = v[0], b = v[1], c = v[2], d = v[3], e = v[4], f = v[5], g = v[6], h = v[7];
            for (var j = 0; j < 64; j++)
            {
                var tj = j < 16 ? 0x79cc4519u : 0x7a879d8au;
                var ss1 = Rotl(Rotl(a, 12) + e + Rotl(tj, j), 7);
                var ss2 = ss1 ^ Rotl(a, 12);
                var tt1 = (j < 16 ? a ^ b ^ c : (a & b) | (a & c) | (b & c)) + d + ss2 + w1[j];
                var tt2 = (j < 16 ? e ^ f ^ g : (e & f) | (~e & g)) + h + ss1 + w[j];
                d = c;
                c = Rotl(b, 9);
                b = a;
                a = tt1;
                h = g;
                g = Rotl(f, 19);
                f = e;
                e = P0(tt2);
            }
            v[0] ^= a; v[1] ^= b; v[2] ^= c; v[3] ^= d;
            v[4] ^= e; v[5] ^= f; v[6] ^= g; v[7] ^= h;
        }

        var outBytes = new byte[32];
        for (var i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt32BigEndian(outBytes.AsSpan(i * 4), v[i]);
        return outBytes;
    }

    private static uint Rotl(uint x, int n)
    {
        n &= 31;
        return (x << n) | (x >> (32 - n));
    }

    private static uint P0(uint x) => x ^ Rotl(x, 9) ^ Rotl(x, 17);
    private static uint P1(uint x) => x ^ Rotl(x, 15) ^ Rotl(x, 23);
}
