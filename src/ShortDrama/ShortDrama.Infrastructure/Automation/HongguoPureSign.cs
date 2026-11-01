using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>
/// 红果搜索和剧集列表的进程内签名。
/// 算法源自 jackotom/hongguo-drama src/native/app-source.js（GPL-3.0），
/// 经 N3urda/hongguoTV、woshishiq1/drpys 传到 sunnysky123/hongguo-web 的 pure-sign.js。
/// </summary>
internal static class HongguoPureSign
{
    internal sealed record MedusaEntropy(
        byte[] MessageRand,
        int EnvLaunch,
        int EnvPid,
        int Now,
        int Random,
        int XmRand,
        string Nonce,
        long Started);

    private const string AuditHost = "api5-normal-sinfonlinea.fqnovel.com";
    private static readonly (string Key, string Value)[] BaseKeys =
    [
        ("aid", "8662"),
        ("app_name", "novelread"),
        ("version_code", "72232"),
        ("version_name", "7.2.2.32"),
        ("channel", "audit"),
        ("device_platform", "android"),
        ("device_type", "audit-windows"),
        ("os_version", "12"),
        ("update_version_code", "72232"),
    ];

    internal const string ImageShrink =
        "W3siaW1hZ2VfdHlwZSI6MywiaW1hZ2Vfd2lkdGgiOjkwMCwic2hyaW5rX3R5cGUiOjN9LHsiaW1hZ2VfdHlwZSI6NCwiaW1hZ2Vfd2lkdGgiOjU0LCJzaHJpbmtfdHlwZSI6NH1d";

    private static readonly string ProcessNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static readonly long ProcessStarted = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    internal static string LoadDeviceId(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("device_id", out var value))
                {
                    var saved = value.GetString() ?? "";
                    if (saved.Length is >= 10 and <= 20 && saved.All(char.IsDigit))
                        return saved;
                }
            }
        }
        catch
        {
            // 损坏的本地身份重新生成。
        }

        var deviceId = Random.Shared.NextInt64(1_000_000_000_000_000, 10_000_000_000_000_000).ToString();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, "{\"device_id\":\"" + deviceId + "\"}");
        File.Move(tmp, path, true);
        return deviceId;
    }

    internal static string DefaultDevicePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TikTokPublisher", "hongguoapp-device.json");

    internal static (string Url, long Rticket) BuildUrl(string path, IEnumerable<KeyValuePair<string, string>>? extra, string deviceId, long? rticket = null)
    {
        var ticket = rticket ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var parts = new List<string>();
        foreach (var (key, value) in BaseKeys)
            parts.Add(key + "=" + Enc(value));
        parts.Add("device_id=" + Enc(deviceId));
        if (extra is not null)
        {
            foreach (var pair in extra)
                parts.Add(pair.Key + "=" + Enc(pair.Value));
        }
        parts.Add("_rticket=" + ticket);
        return ($"https://{AuditHost}{path}?{string.Join("&", parts)}", ticket);
    }

    internal static byte[] EpisodesBody(string seriesId)
    {
        var json = "{\"biz_param\":{\"detail_page_version\":0,\"disable_digg_stat\":false,"
                   + "\"image_shrink_datas_str\":\"" + ImageShrink + "\","
                   + "\"need_all_video_definition\":false,\"need_mp4_align\":false,"
                   + "\"screen_width_px\":\"900\",\"source\":7,\"use_os_player\":false,\"use_server_dns\":false},"
                   + "\"dr_scene\":\"preload\",\"series_id\":\"" + seriesId + "\"}";
        return Encoding.UTF8.GetBytes(json);
    }

    internal static Dictionary<string, string> SessionHeaders() => new()
    {
        ["user-agent"] = "hongguo-web/1.0",
        ["x-tt-store-region"] = "cn-sh",
        ["x-tt-store-region-src"] = "uid",
        ["passport-sdk-version"] = "5051452",
        ["sdk-version"] = "2",
    };

    internal static int BranchOf(string url, byte[]? body, int khronos)
    {
        var bodyMd5 = body is { Length: > 0 } ? Md5(body) : new byte[16];
        var iv = GetIv(GetIv(GetIv(0x20230928, HongguoSm3.Hash(Encoding.UTF8.GetBytes(QueryOf(url)))), bodyMd5), Le32(khronos));
        var low = iv & 15;
        return low - ((low * 171) >> 9) * 3;
    }

    internal static Dictionary<string, string> Sign(string url, byte[]? body, int? khronos = null, int? gorgonRandom = null, int? heliosRandom = null, MedusaEntropy? entropy = null)
    {
        var ts = khronos ?? (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var query = QueryOf(url);
        var gorgon = gorgonRandom ?? Random.Shared.Next(65536);
        var helios = heliosRandom ?? BinaryPrimitives.ReadInt32BigEndian(RandomNumberGenerator.GetBytes(4));
        return new Dictionary<string, string>
        {
            ["x-gorgon"] = XGorgon(query, body, ts, gorgon),
            ["x-khronos"] = ts.ToString(),
            ["x-ladon"] = Convert.ToBase64String(Be32(ts)),
            ["x-argus"] = Convert.ToBase64String(Le32(ts)),
            ["x-helios"] = HeliosWith(ts, helios),
            ["x-medusa"] = BuildMedusa(url, body, ts, ParamOf(query, "device_id", "0"), ParamOf(query, "version_name", "7.1.3.32"), entropy),
        };
    }

    internal static string XGorgon(string query, byte[]? body, int timestamp, int random)
    {
        var q4 = Md5(Encoding.UTF8.GetBytes(query)).AsSpan(0, 4).ToArray();
        var b4 = body is { Length: > 0 } ? Md5(body).AsSpan(0, 4).ToArray() : new byte[4];
        var blob = Cat(q4, b4, new byte[4], Le32(67503104), Be32(timestamp));
        var key = new[] { 0x4A, 0x40, 0x16, (random >> 8) & 255, 0x47, 0x6C, 0x01, random & 255 };
        var cipher = Rc4Gorgon(blob, key);
        for (var i = 0; i < cipher.Length; i++)
        {
            var value = Rotl8(cipher[i]);
            var next = i + 1 < cipher.Length ? cipher[i + 1] : cipher[0];
            cipher[i] = (byte)((~(ReverseBits((next ^ value) & 255)) ^ 20) & 255);
        }
        return Convert.ToHexString(Cat(new byte[] { 0x84, 0x04, (byte)(random & 255), (byte)((random >> 8) & 255), 0x40, 0x01 }, cipher)).ToLowerInvariant();
    }

    internal static string HeliosWith(int khronos, int random)
    {
        var digest = Md5(Cat(Le32(random), "8662"u8.ToArray()));
        var ascii = Encoding.ASCII.GetBytes(Convert.ToHexString(digest).ToLowerInvariant());
        var words = new ulong[4];
        for (var i = 0; i < 4; i++)
            words[i] = BinaryPrimitives.ReadUInt64LittleEndian(ascii.AsSpan(i * 8));
        var queue = new Queue<ulong>();
        queue.Enqueue(words[2]);
        queue.Enqueue(words[3]);
        var b0 = words[0];
        var b8 = words[1];
        var table = new ulong[35];
        table[0] = words[0];
        for (var i = 0; i < 34; i++)
        {
            var x8 = unchecked(Rotr64(b8, 8) + b0);
            x8 ^= (ulong)i;
            queue.Enqueue(x8);
            x8 ^= Rotr64(b0, 61);
            table[i + 1] = x8;
            b0 = x8;
            b8 = queue.Dequeue();
        }
        var text = Encoding.UTF8.GetBytes(khronos + "-1588093228-8662");
        var padLen = (text.Length + 1 + 15) / 16 * 16;
        var pad = new byte[padLen];
        Array.Fill(pad, (byte)((16 - (text.Length % 16)) & 255));
        text.CopyTo(pad, 0);
        var output = new List<byte> { };
        output.AddRange(Le32(random));
        for (var at = 0; at < pad.Length; at += 16)
        {
            var a = BinaryPrimitives.ReadUInt64LittleEndian(pad.AsSpan(at));
            var b = BinaryPrimitives.ReadUInt64LittleEndian(pad.AsSpan(at + 8));
            for (var i = 0; i < 34; i++)
            {
                b = table[i] ^ unchecked(a + Rotr64(b, 8));
                a = b ^ Rotr64(a, 61);
            }
            var block = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(block, a);
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(8), b);
            output.AddRange(block);
        }
        return Convert.ToBase64String(output.ToArray());
    }

    internal static byte[] HashF13(byte[] querySm3, byte[] bodyMd5, byte[] tsBytes, int khronos)
    {
        var iv = GetIv(GetIv(GetIv(0x20230928, querySm3), bodyMd5), tsBytes);
        var low = iv & 15;
        var ivV0 = (low * 171) >> 9;
        var branch = low - ivV0 * 3;
        if (branch == 2)
            return Branch2F13(iv, querySm3, bodyMd5, tsBytes, khronos);
        if (branch != 0)
            return Cat(querySm3.AsSpan(0, 16).ToArray(), bodyMd5, Le32(SumMd5(Cat(querySm3, bodyMd5))));

        int[] ivV1List = [unchecked((int)0xC4A78580), unchecked((int)0xB3C0FD39), unchecked((int)0xC58C5686), unchecked((int)0xC9AA3BA7), unchecked((int)0xF5A7ADF2), unchecked((int)0x963C2ED1)];
        var ivV1 = ivV1List[ivV0];
        var countV1 = (ivV1 + khronos + 1) & 255;
        var countV2 = unchecked(ivV1 + khronos);
        int[] tt02 =
        [
            unchecked((int)0xEBB64FAF), 0x07AADCC2, unchecked((int)0xCF3187BF), unchecked((int)0xE01138FF), 0x6D0BFCFF, 0x5A30A3BE, unchecked((int)0xB41AD638), 0x34180EB8,
            unchecked((int)0xF233EB6F), unchecked((int)0xB1A584CC), unchecked((int)0xCCC30DC7), 0x47D1DB51, unchecked((int)0xD55653DE), 0x70A84FA1, 0x57473C12, unchecked((int)0xF76F0288),
            0x2C077F0A, unchecked((int)0xDA0DCAD0), unchecked((int)0xFBB86F6C), unchecked((int)0xFDC4CF00), 0x688A020D, unchecked((int)0xE676C6A6), unchecked((int)0x8CD6338B), 0x1A3C8D0E,
            unchecked((int)0xCCE8B06B), 0x6AD0ED0B, unchecked((int)0xA0522717), unchecked((int)0xDC71AC83), 0x2285DB71, unchecked((int)0xD5B4DDA6), 0x736F8650, 0x6560306C,
            0x617CE2A6, unchecked((int)0xE423417E), 0x0A40E143, 0x544E4032, unchecked((int)0x88DFFB2A), 0x716C1AE0, 0x4C467A88, 0x05B23BB3,
            unchecked((int)0xE1D0B866), unchecked((int)0xBAA3DCB8), unchecked((int)0xAE3374D3), unchecked((int)0xC3381A50), 0x1702F75B, unchecked((int)0xFE6DA368), unchecked((int)0xF0B4CF48), 0x4E0FFBB8,
            0x72AAD10D, 0x26C53A3D, unchecked((int)0xF2BCE0F6), unchecked((int)0xB4557581), 0x4A257FDD, unchecked((int)0x8C3182A2), unchecked((int)0xAB0B3B86), 0x3D5DFB14,
            0x4F103634, unchecked((int)0xD37B52D7), 0x444EFF16, unchecked((int)0xEB0A33D1), 0x6CA86F6E, 0x0284BA7, 0x08387CFA, 0x5FB37586,
        ];
        var tt = new int[tt02.Length];
        for (var i = 0; i < tt.Length; i++)
            tt[i] = Ror(tt02[i], countV1);
        var n0 = (countV2 + 2) & 7;
        int[] seed = [0xFA, 0x45, 0x61, 0xD7];
        var pad = new byte[seed.Length];
        for (var i = 0; i < seed.Length; i++)
            pad[i] = (byte)(Ashr(seed[i] | Shl(seed[i], 8), n0) & 255);
        var data = Cat(querySm3, bodyMd5, tsBytes, pad, Convert.FromHexString("00000000000001a0"));
        var di = new int[data.Length / 4 + 112 + 16];
        var diLen = 0;
        for (var i = 0; i + 3 < data.Length; i += 4)
            di[diLen++] = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(i));
        var di0 = di[0];
        for (var i = 0; i < 112; i++)
        {
            var di1 = di[i + 1];
            var di14 = di[i + 14];
            var r1 = Rotl(di1, 14) ^ Rotl(di1, 25) ^ ShrU(di1, 3);
            var r2 = Rotl(di14, 13) ^ Rotl(di14, 15) ^ ShrU(di14, 10);
            di[diLen++] = unchecked(di0 + di[i + 9] + r1 + r2);
        }
        int[] initRaw = [0x7ABA4FC8, 0x67166507, 0x6403FA00, 0x340F512F, 984304912, unchecked((int)3005047866), unchecked((int)2874125293), unchecked((int)2152413264)];
        var init = new int[8];
        for (var i = 0; i < 8; i++)
            init[i] = Ror(initRaw[i], countV2 & 31);
        int[][] chosenTable =
        [
            [101, 5, 7, 6, 3, 2, 1, 0, 5, 4, 3],
            [96, 0, 6, 7, 5, 3, 2, 1, 5, 4, 4],
            [96, 7, 6, 2, 1, 4, 0, 5, 4, 3, 5],
            [99, 3, 6, 2, 4, 5, 1, 0, 0, 7, 6],
            [96, 0, 5, 6, 7, 3, 1, 2, 5, 4, 4],
            [100, 2, 0, 3, 5, 4, 6, 7, 2, 1, 5],
        ];
        var chosen = chosenTable[ivV0];
        var state = (int[])init.Clone();
        for (var i = 0; i < chosen[0]; i++)
        {
            var baseIndex = unchecked(ivV1 + i);
            var n0v = di[baseIndex & 127];
            var n1 = ((state[chosen[3]] ^ state[chosen[4]]) & state[chosen[1]]) ^ state[chosen[3]];
            var n2 = Rotl(state[chosen[1]], 26) ^ Rotl(state[chosen[1]], 21) ^ Rotl(state[chosen[1]], 7);
            var n4 = unchecked(n0v + n1 + n2 + tt[baseIndex & 63] + state[chosen[5]]);
            var n5 = Rotl(state[chosen[2]], 30) ^ Rotl(state[chosen[2]], 19) ^ Rotl(state[chosen[2]], 10);
            var n6 = (state[chosen[2]] & state[chosen[6]]) | ((state[chosen[2]] | state[chosen[6]]) & state[chosen[7]]);
            var n7 = unchecked(n5 + n6);
            var old = state[chosen[9]];
            var last = state[^1];
            for (var k = state.Length - 1; k >= 1; k--)
                state[k] = state[k - 1];
            state[0] = last;
            state[chosen[10]] = unchecked(n7 + n4);
            state[chosen[8]] = unchecked(old + n4);
        }
        var ret = new byte[32];
        for (var i = 0; i < 8; i++)
            BinaryPrimitives.WriteInt32BigEndian(ret.AsSpan(i * 4), unchecked(state[i] + init[i]));
        for (var i = 0; i < 16; i++)
            ret[i] ^= ret[i + 16];
        return Cat(ret.AsSpan(0, 16).ToArray(), Le32(SumMd5(ret.AsSpan(0, 16).ToArray())));
    }

    private static byte[] Branch2F13(int iv, byte[] querySm3, byte[] bodyMd5, byte[] tsBytes, int khronos)
    {
        var v = (iv & 13) * 86;
        var ivV0 = ((v >>> 15) & 255) + ((v >>> 8) & 255);
        int[] table = [unchecked((int)0x8980F29B), unchecked((int)0xEB549C7F), unchecked((int)0xB08726DB), unchecked((int)0xD40CB5E6), unchecked((int)0xE8F559E4)];
        var n1 = table[ivV0];
        var countV1 = (n1 + khronos + 1) & 255;
        var countV2 = unchecked(n1 + khronos);
        var shift = (countV2 + 5) & 7;
        int[] seed = [0x84, 0x96, 0x77, 0x9D, 0xD4, 0x15, 0x0B, 0xF8];
        var pad = new byte[8];
        for (var i = 0; i < 8; i++)
            pad[i] = (byte)(Ashr(seed[i] | Shl(seed[i], 8), shift) & 255);
        var blob = Cat(querySm3, bodyMd5, tsBytes, pad, Convert.FromHexString("a0010000"));
        var orders = Branch2Orders.AsSpan(ivV0 << 6, 64).ToArray();
        return Md5SumV3(blob, countV2, orders, countV1);
    }

    private static byte[] Md5SumV3(byte[] message, int countV2, byte[] orders, int countV1)
    {
        var sv = new int[Branch2Sv.Length];
        for (var i = 0; i < sv.Length; i++)
            sv[i] = Ror(Branch2Sv[i], countV1);
        int[] start = [Ror(0x79E0F2FB, countV2), Ror(unchecked((int)0xC8B52570), countV2), Ror(unchecked((int)0xEBC2F8CD), countV2), Ror(0x7C104D93, countV2)];
        var endCount = (countV2 + 6) & 255;
        int[] end = [Ror(0x19BE4866, endCount), Ror(unchecked((int)0xE85986B4), endCount), Ror(unchecked((int)0xE19B326E), endCount), Ror(0x71D1D7D4, endCount)];
        var words = new int[16];
        for (var i = 0; i < 16; i++)
            words[i] = BinaryPrimitives.ReadInt32LittleEndian(message.AsSpan(i * 4));
        int[] shifts0 = [7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22];
        int[] shifts1 = [5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20];
        int[] shifts2 = [4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23];
        int[] shifts3 = [6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21];
        int[][] shifts = [shifts0, shifts1, shifts2, shifts3];
        var state = (int[])start.Clone();
        for (var kind = 0; kind < 4; kind++)
        {
            var offset = kind * 16;
            for (var i = 0; i < 16; i++)
            {
                var phase = i & 3;
                var mm = words[orders[offset + i]];
                var cc = sv[offset + i];
                if (phase == 0) state[0] = Md5V3Step(kind, state[0], state[1], state[2], state[3], mm, shifts[kind][i], cc);
                else if (phase == 1) state[3] = Md5V3Step(kind, state[3], state[0], state[1], state[2], mm, shifts[kind][i], cc);
                else if (phase == 2) state[2] = Md5V3Step(kind, state[2], state[3], state[0], state[1], mm, shifts[kind][i], cc);
                else state[1] = Md5V3Step(kind, state[1], state[2], state[3], state[0], mm, shifts[kind][i], cc);
            }
        }
        var ret = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(ret.AsSpan(0), unchecked(start[0] + state[0]) ^ end[0]);
        BinaryPrimitives.WriteInt32LittleEndian(ret.AsSpan(4), unchecked(start[1] + state[1]) ^ end[1]);
        BinaryPrimitives.WriteInt32LittleEndian(ret.AsSpan(8), unchecked(start[2] + state[2]) ^ end[2]);
        BinaryPrimitives.WriteInt32LittleEndian(ret.AsSpan(12), unchecked(start[3] + state[3]) ^ end[3]);
        return Cat(ret, Le32(SumMd5(ret)));
    }

    private static int Md5V3Step(int kind, int a, int b, int c, int d, int m, int shift, int constant)
    {
        var f = kind switch
        {
            0 => (b & c) | (~b & d),
            1 => (b & d) | (c & ~d),
            2 => b ^ c ^ d,
            _ => c ^ (b | ~d),
        };
        return unchecked(b + Rotl(unchecked(a + f + m + constant), shift));
    }

    private static string BuildMedusa(string url, byte[]? body, int khronos, string deviceId, string version, MedusaEntropy? entropy)
    {
        var bodyMd5 = body is { Length: > 0 } ? Md5(body) : new byte[16];
        var query = QueryOf(url);
        var querySm3 = HongguoSm3.Hash(Encoding.UTF8.GetBytes(query));
        var ts = Le32(khronos);
        var queryBodyTs = HashF13(querySm3, bodyMd5, ts, khronos);
        byte[] messageRand;
        int envLaunch, envPid, now, random, xmRand;
        string nonce;
        long started;
        if (entropy is null)
        {
            messageRand = RandomNumberGenerator.GetBytes(4);
            envLaunch = 100 + Random.Shared.Next(21);
            envPid = 10001 + Random.Shared.Next(2000);
            now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            random = BinaryPrimitives.ReadInt32BigEndian(RandomNumberGenerator.GetBytes(4));
            xmRand = BinaryPrimitives.ReadInt32BigEndian(RandomNumberGenerator.GetBytes(4));
            nonce = ProcessNonce;
            started = ProcessStarted;
        }
        else
        {
            messageRand = entropy.MessageRand;
            envLaunch = entropy.EnvLaunch;
            envPid = entropy.EnvPid;
            now = entropy.Now;
            random = entropy.Random;
            xmRand = entropy.XmRand;
            nonce = entropy.Nonce;
            started = entropy.Started;
        }
        var messageRandU = (long)(uint)BinaryPrimitives.ReadInt32LittleEndian(messageRand);
        var dev = DeviceProto(deviceId, nonce, started);
        var env = Proto(
            (1, envLaunch, "sint"), (2, 146331399L, "sint"), (3, 146331396L, "sint"),
            (5, 7L, "sint"), (6, "v04.06.04.03-bugfix", "string"), (7, envPid, "sint"),
            (12, dev, "message"),
            (13, Proto((1, now, "sint"), (2, -2L, "sint"), (4, 200L, "sint")), "message"),
            (14, version, "string"));
        var queryHash = HongguoSm3.Hash(Cat(Encoding.UTF8.GetBytes(query), bodyMd5, "none"u8.ToArray()));
        var nested = Proto((1, 111L, "sint"), (2, 10L, "sint"), (3, 694367L, "sint"), (5, 586952199L, "sint"));
        var message = Proto(
            (1, MedusaMagic16, "bytes"), (2, 3L, "sint"), (3, messageRandU, "sint"),
            (4, "8662", "string"), (5, deviceId, "string"), (6, "1588093228", "string"), (7, version, "string"),
            (8, "v04.06.04-ml-android", "string"), (9, 67503104L, "sint"),
            (10, MedusaMagic8, "bytes"), (12, khronos, "sint"),
            (13, queryBodyTs, "bytes"), (14, querySm3.AsSpan(0, 6).ToArray(), "bytes"),
            (15, nested, "message"), (16, nonce, "string"), (17, khronos, "sint"),
            (19, queryHash, "bytes"), (20, "none", "string"), (21, 312L, "sint"),
            (23, env, "message"),
            (24, "{\"cmr\":16777216,\"cmr2\":16777216,\"un_h\":1879194040,\"vpn\":0,\"kd\":0,\"fkd\":3672518972,\"pd\":-1872573247,\"dyn\":\"\",\"do\":0,\"tk\":true}", "string"));
        var (digest, seed) = KeyHash(MedusaSignKey, random);
        var transformed = Cat(MedusaMagic8, Xmxor(message, digest));
        Array.Reverse(transformed);
        for (var i = 0; i < transformed.Length; i++)
            transformed[i] = (byte)(transformed[i] ^ seed[(~i) & 3]);
        var check = ((querySm3[0] & 63) << 14) | 0x18000001 | ((queryBodyTs[0] & 63) << 8);
        var packed = Cat(new byte[] { 0x35 }, Le32(xmRand), Le32(check), transformed, new[] { (byte)((random >> 16) & 255), (byte)((random >> 24) & 255) });
        var encrypted = new AesV3(MedusaAesKey, khronos).Encrypt(packed, MedusaAesIv);
        var versionBytes = Convert.FromHexString("03000000f7e85ffad7d7dc3bd62ac87057cf6118");
        var prefix = new byte[20];
        for (var i = 0; i < 20; i += 4)
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(i), BinaryPrimitives.ReadInt32LittleEndian(versionBytes.AsSpan(i)) ^ khronos);
        return Convert.ToBase64String(Cat(prefix, new[] { (byte)(random & 255), (byte)((random >> 8) & 255), (byte)0, (byte)1 }, encrypted));
    }

    private static byte[] DeviceProto(string deviceId, string nonce, long started) => Proto(
        (1, 1L, "sint"), (2, 2L, "sint"), (3, "8662", "string"), (4, deviceId, "string"),
        (5, nonce, "string"), (6, "!noperm!", "string"), (7, -888888L, "sint"), (8, -888888L, "sint"),
        (9, 3L, "sint"), (10, -888888L, "sint"), (11, "!notset!", "string"),
        (12, "Asia/Shanghai,8", "string"), (13, "zh_CN", "string"), (14, 4L, "sint"),
        (16, 0f, "float"), (17, 0f, "float"), (18, 0f, "float"), (19, 0f, "float"),
        (20, 0f, "float"), (21, 0f, "float"), (22, "16", "string"), (23, 41L, "sint"),
        (24, 36L, "sint"), (25, started, "sint"), (26, started, "sint"),
        (27, started, "sint"), (28, started, "sint"), (29, -1L, "sint"),
        (30, "25053RT47C", "string"), (31, "Redmi", "string"), (32, "25053RT47C", "string"), (33, "25053RT47C", "string"),
        (34, "Xiaomi", "string"), (35, "Redmi", "string"), (36, "Redmi", "string"), (38, 31L, "sint"));

    private static (byte[] Hash, byte[] Seed) KeyHash(byte[] signKey, int random)
    {
        var digest = HongguoSm3.Hash(Cat(signKey, Le32(random), signKey));
        var d1 = (random >> 16) & 255;
        var d2 = ~((Shl(d1, 11) | ShrU(random, 24)) ^ Ashr(d1, 5) ^ d1);
        return (digest, Le32(d2));
    }

    private static byte[] Xmxor(byte[] data, byte[] key)
    {
        var encoded = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            var at = (i * 4) & 28;
            var d0 = key[at];
            var d1 = key[at + 1];
            var d2 = Rotl8(data[i]) + d0;
            d2 = (~d2) ^ d1;
            d2 = Rotl8_3(d2 & 255);
            d2 = (d2 + d1) & 255;
            d2 = (d2 ^ d0) & 255;
            encoded[data.Length - i - 1] = (byte)((~d2) & 255);
        }
        var last = (encoded[^1] ^ encoded[^2]) & 255;
        var first = encoded[0];
        encoded[0] = (byte)(((~last) + first) & 255);
        encoded[1] = (byte)(((encoded[0] ^ encoded[^1] ^ 254) + encoded[1]) & 255);
        encoded[2] = (byte)((encoded[2] + (((last - first) ^ Rotl8_3(encoded[1])) ^ 2)) & 255);
        for (var i = 0; i < encoded.Length - 4; i++)
        {
            var temp = Rotl8_3(encoded[i + 2]) ^ encoded[i + 1] ^ (i + 3);
            encoded[i + 3] = (byte)(((~temp) + encoded[i + 3]) & 255);
        }
        encoded[^1] = (byte)((encoded[^1] ^ encoded[^2]) & 255);
        var total = 0;
        for (var i = 0; i < encoded.Length - 1; i++)
            total += encoded[i + 1];
        encoded[0] = (byte)(((encoded[0] ^ encoded[1]) + total) & 255);
        return encoded;
    }

    private static int GetIv(int iv, byte[] data)
    {
        var value = iv;
        for (var i = 0; i < data.Length; i++)
        {
            var d = data[i] & 255;
            value = (i & 1) == 0
                ? ShrU(value, 4) ^ value ^ Shl(value, 6) ^ d
                : ~(ShrU(value, 7) ^ value ^ (d | Shl(value, 12)));
        }
        return value;
    }

    private static int SumMd5(byte[] data)
    {
        var check = 0x20220420;
        for (var i = 0; i < 12; i++)
        {
            var temp = (i & 1) == 0 ? ShrU(check, 3) ^ check : ShrU(check, 5) ^ check;
            check = (i & 1) == 0 ? (data[i] & 255) ^ Shl(check, 7) : (data[i] & 255) | Shl(check, 11);
            if ((i & 1) != 0)
                check = ~check;
            check ^= temp;
        }
        return (check | 4) ^ 0x1000000;
    }

    private static byte[] Rc4Gorgon(byte[] data, int[] key)
    {
        var s = new int[256];
        for (var i = 0; i < 256; i++) s[i] = i;
        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 255;
            s[i] = s[j];
        }
        var cipher = new byte[data.Length];
        var x = 0;
        j = 0;
        for (var k = 0; k < data.Length; k++)
        {
            x += 1;
            var sx = s[x];
            j += sx;
            var y = s[j & 255];
            s[x] = y;
            cipher[k] = (byte)(data[k] ^ s[(y + y) & 255]);
        }
        return cipher;
    }

    private static byte[] Proto(params (int Tag, object Value, string Type)[] fields)
    {
        var parts = new List<byte>();
        foreach (var (tag, value, type) in fields)
            parts.AddRange(ProtoField(tag, value, type));
        return parts.ToArray();
    }

    private static byte[] ProtoField(int tag, object value, string type)
    {
        if (type == "string" && value is string s && s.Length == 0)
            return [];
        if (type is "bytes" or "message")
        {
            var body = (byte[])value;
            return Cat(Varint((ulong)((tag << 3) | 2)), Varint((ulong)body.Length), body);
        }
        if (type == "string")
        {
            var body = Encoding.UTF8.GetBytes((string)value);
            return Cat(Varint((ulong)((tag << 3) | 2)), Varint((ulong)body.Length), body);
        }
        if (type == "float")
        {
            var body = new byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(body, (float)value);
            return Cat(Varint((ulong)((tag << 3) | 5)), body);
        }
        var number = value switch
        {
            long n => n,
            int n => n,
            _ => Convert.ToInt64(value),
        };
        var payload = type == "sint" ? Zigzag(number) : (ulong)number;
        return Cat(Varint((ulong)(tag << 3)), Varint(payload));
    }

    private static ulong Zigzag(long value) => value < 0 ? (ulong)(-value * 2 - 1) : (ulong)value * 2;

    private static byte[] Varint(ulong n)
    {
        var bytes = new List<byte>();
        while (n > 127)
        {
            bytes.Add((byte)((n & 127) | 128));
            n >>= 7;
        }
        bytes.Add((byte)n);
        return bytes.ToArray();
    }

    private static int Rotl(int x, int n)
    {
        var k = n & 31;
        return Shl(x, k) | ShrU(x, (32 - k) & 31);
    }

    private static int Ror(int x, int n)
    {
        var k = n & 31;
        return ShrU(x, k) | Shl(x, (32 - k) & 31);
    }

    private static int Shl(int n, int k) => (int)((uint)n << (k & 31));
    private static int ShrU(int n, int k) => (int)((uint)n >> (k & 31));
    private static int Ashr(int n, int k) => n >> (k & 31);
    private static int Rotl8(int x) => ((x << 4) | ((x & 255) >> 4)) & 255;
    private static int Rotl8_3(int x) => ((x << 3) | ((x & 255) >> 5)) & 255;

    private static int ReverseBits(int value)
    {
        var r = 0;
        for (var i = 0; i < 8; i++)
            r = (r << 1) | ((value >> i) & 1);
        return r & 255;
    }

    private static ulong Rotr64(ulong x, int n)
    {
        n &= 63;
        return n == 0 ? x : (x >> n) | (x << (64 - n));
    }

    private static byte[] Le32(int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        return b;
    }

    private static byte[] Be32(int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        return b;
    }

    private static byte[] Md5(byte[]? data) => MD5.HashData(data ?? []);

    private static byte[] Cat(params byte[][] parts)
    {
        var len = 0;
        foreach (var part in parts)
            len += part.Length;
        var output = new byte[len];
        var at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(output, at);
            at += part.Length;
        }
        return output;
    }

    private static string QueryOf(string url)
    {
        var i = url.IndexOf('?');
        return i < 0 ? "" : url[(i + 1)..];
    }

    private static string ParamOf(string query, string key, string fallback)
    {
        foreach (var kv in query.Split('&'))
        {
            var eq = kv.IndexOf('=');
            if (eq <= 0 || kv[..eq] != key)
                continue;
            var value = kv[(eq + 1)..];
            if (value.Length > 0)
                return value;
        }
        return fallback;
    }

    private static string Enc(string value)
    {
        return Uri.EscapeDataString(value)
            .Replace("%21", "!", StringComparison.Ordinal)
            .Replace("%2A", "*", StringComparison.Ordinal)
            .Replace("%27", "'", StringComparison.Ordinal)
            .Replace("%28", "(", StringComparison.Ordinal)
            .Replace("%29", ")", StringComparison.Ordinal);
    }

    private static readonly int[] Branch2Sv =
    [
        unchecked((int)0xA7AEFE20), 0x7149F1D6, 0x47E4CA07, unchecked((int)0xE9B58F67), unchecked((int)0x93B924DE), unchecked((int)0xC614D0F5), 0x38AFE0EF, unchecked((int)0xB2BBAD73),
        unchecked((int)0xE24444C3), unchecked((int)0x9D3AEC9B), unchecked((int)0xDF7B37E4), unchecked((int)0xD8B16D40), unchecked((int)0xF8AC31B8), 0x76B9A90B, 0x31D833EE, unchecked((int)0x953FCE64),
        0x353595A4, 0x4609C13B, 0x36925008, unchecked((int)0x8C6D0925), 0x5DF5C177, 0x1CFBF52B, unchecked((int)0x8A4FA7F0), 0x114CA35E,
        unchecked((int)0x8193F984), 0x7A7A8733, 0x316AB4D5, 0x3C20CFC9, unchecked((int)0xA6D84453), 0x3A18500C, 0x798EC47A, unchecked((int)0x97A76B28),
        0x66C4FF96, 0x51716443, 0x0DD2FC3B, unchecked((int)0xB5696DA7), unchecked((int)0xBBEB3AC5), 0x5C53D204, unchecked((int)0xD32608CE), 0x7279B9EC,
        unchecked((int)0xF4188ECF), unchecked((int)0xF7D793DB), 0x332CC491, unchecked((int)0xAB76AE15), unchecked((int)0x9BEBE727), 0x18A01384, 0x5BE9F8A7, 0x5F90A754,
        0x39B663C0, 0x36673C83, 0x7C92F514, unchecked((int)0x9D7D94D7), unchecked((int)0xE2E8D9AA), 0x5F7E9EA9, 0x7ABD4551, 0x569E05DA,
        0x40A25632, 0x3DF5A9A5, unchecked((int)0xBAB37D80), 0x454286DC, 0x3F5D4E78, 0x03D7B75D, unchecked((int)0xB1FE4AF7), unchecked((int)0xA5AB26A3),
    ];

    private static readonly byte[] Branch2Orders = Convert.FromHexString(
        "0f0704000908030a060b050d0e010c02" +
        "0f05080c0009020103070e060b0a0d04" +
        "060500070c000a04080f010b0d09020e" +
        "060b02050403080101070a000d0c090e" +
        "0d070e0f0b0208030c0509010004060a" +
        "0d0902060f0b0a040807000c0503010e" +
        "0c090f07060f030e020d0405010b0a00" +
        "0c050a090e0802040407030f01060b00" +
        "0b0f0408020a0700090d06010e03050c" +
        "0b060a0508020c03070f0e090d000104" +
        "0906080f050800040a0b030d01020c0e" +
        "090d0c0604070a03030f00080105020e" +
        "01000d0f090a0b0e0402080703060c05" +
        "01080a0c0f0905060b000304020e070d" +
        "04080f000c0f0e0d0a01060207090503" +
        "040205080d0b0a0606000e0f070c0903" +
        "0a08040f000b01060d0c0709030e0502" +
        "0a070b050f00020e0108030d0c060904" +
        "0d070f08050f06040b0a0e0c09000203" +
        "0d0c020704010b0e0e08060f09050003");

    private static readonly byte[] MedusaSignKey = Convert.FromHexString("8ebdfa3806ecc5cee79423e6029ed82540bc2218bb7eaef71cb691f7aa8aa2f5");
    private static readonly byte[] MedusaAesKey = Convert.FromHexString("f1593376766ea98d34f31b057a9d5be4");
    private static readonly byte[] MedusaAesIv = Convert.FromHexString("1fe109a4125283f418de9e051a969e12");
    private static readonly byte[] MedusaMagic16 = Convert.FromHexString("f7e85ffad7d7dc3bd62ac87057cf6118");
    private static readonly byte[] MedusaMagic8 = Convert.FromHexString("4001000000000000");

    private static readonly byte[] Sbox = Convert.FromBase64String(
        "+n0Ia5xZs0sEXznQOEqRmQBnpiCf9U2Ccybu3xhmgzOAAxn72f6uqqmwUsYL83klTni0NqxdGieeiNu9PGPsSRXBMB/cuFbUbM3KCUPINaPvHvSW0vwOcnuUhNHqRVpiAj/TEoE0K91+5ijypUYTATsh9mE3KSoN7Yyvv51cuyR2D3XkU4nhmI2xmmVwT1RMWKtub4sjxAcRDLrPoKSO2AU9FLLadMPX577Wf95IFj6FkKFVt3dCIsmGUC4X+WQxLJvxbRxEaOPpqJOXyzJX6+Vxaq3AzMfF/WAdoi1Hp+JRaV56zgpBtpWP97mH4DoGEIq1+FvV8LyS/3wvwugbQOwb2r26mJEMsiuDQTRn+wrYdrVGBVlhI3WQhyrjUBVMrLF5667llUcEaPCGPVGLD8qO5LlO8hKCvA7V9+8oJc9bXelqVQLhM76T5/WtnT45JKji+hdX0HoNCDDWuKON/QeaxB5uImSX0h2wv0VmP2zd2yeApxHcpsVS+MC2yFwAc2B7oBkTqsk1SEvTpM2fmfMQREBUfin0Bh+iq6EvPPavhWI2IX9e3yAas7Tm/3KEj2UmlFp36kN4x0rMLBRrxuh0U/zUHM4xcAMYjJY4MonxOl/X+alpt2M3WMI7w3HLnpIBigtNiJu7T21v4P6lSd5WFgntnMEt7oF92XzRLkJbTcGmXepE/UVOG6E/0YnhfS+q26utWcuxzpooyeD2cDlK1/8w9d28VzsRjbLuALbmGlp8+d7EzS6Au7lMpZ+ECMZvQmzwJ+eLOpxR+2chdUExp8ogQyq3v9l68rX4jCwjg0+PYKAEEzcU4wHFY2ZcdIHfWL1okD3SszT0GZMyKdZJrg1L2AeerB4tC0C4crp2EHGo5FYdSP7lwkeR2ocmnR+Ia8CYviUJlzOjhRZef9xuVOn3qcjow3fQgivsAmKKkg4+sA8F8/GWeDiGNhg8JM8KtFPMYWWkx5TVFX5t73kiNRJqjlIGVXtGZFCV4gzt0xcDopuZ6xz8r9Rzafpf9ywev8jh8592gHFIqpStZPuJxmDDMrNN0uBE3V+oscdoIzTJbRJ/t+sVvqnReJOgDJKk10fjisJwqyZBmnmn2BSFj8BvVtCMEbkuPOKdzw7eA11GPs04Qw8zWtkaZWwiO/wwpojqN6K0jY5RnNZA7vn4hPSul+nKCkVnVwQvg1zVxcSCtqORmB9KrJaBbssbCQivGJVJfVTt+hYxOtq4ZvWl8f4QAQZ0zGPffCgl9s6yT4vlvIdpu4YhBwA25wtQWZsc6GJYGWHyvSdeuh3mmUI9DSq13Fsp8C1MU3tqc04/df9LoTUXVXI5INOw/e8C7Hd+5CvbkMEFnnrUUmskEw==");

    private sealed class AesV3
    {
        private readonly byte[] _box;
        private readonly int[] _con2;
        private readonly int[] _order;
        private readonly byte[][] _keys;

        public AesV3(byte[] key, int khronos)
        {
            var word = khronos & 3;
            _box = Sbox.AsSpan(word * 256, 256).ToArray();
            int[][] con2Table = [[1, 0, 2, 3], [2, 0, 3, 1], [0, 1, 3, 2], [1, 0, 2, 3]];
            int[][] orderTable =
            [
                [0, 9, 14, 11, 4, 13, 2, 7, 8, 1, 6, 15, 12, 5, 10, 3],
                [0, 9, 14, 15, 4, 13, 2, 7, 8, 1, 6, 3, 12, 5, 10, 11],
                [0, 9, 14, 7, 4, 13, 2, 11, 8, 1, 6, 3, 12, 5, 10, 15],
                [0, 9, 14, 11, 4, 13, 2, 7, 8, 1, 6, 15, 12, 5, 10, 3],
            ];
            _con2 = con2Table[word];
            _order = orderTable[word];
            var init = new[] { unchecked((int)0xCA025DDC), unchecked((int)0x823DC546), unchecked((int)0xC9420583), unchecked((int)0xC298225F) }[word];
            var initial = new byte[16];
            for (var i = 0; i < 4; i++)
                BinaryPrimitives.WriteInt32LittleEndian(initial.AsSpan(i * 4), init);
            var expanded = new byte[48];
            for (var i = 0; i < 16; i++)
                expanded[i] = (byte)(initial[i] ^ key[i]);
            var rounds = 8;
            for (var i = 4; i < 12; i++)
            {
                var at = 4 * (i - 1);
                var a = expanded[at];
                var b = expanded[at + 1];
                var c = expanded[at + 2];
                var d = expanded[at + 3];
                if ((i & 3) == 0)
                {
                    var t = (byte)(((uint)init >> (rounds & 24) ^ _box[b]) & 255);
                    b = _box[c];
                    c = _box[d];
                    d = _box[a];
                    a = t;
                }
                rounds += 2;
                expanded[at + 4] = (byte)(a ^ expanded[at - 12]);
                expanded[at + 5] = (byte)(b ^ expanded[at - 11]);
                expanded[at + 6] = (byte)(c ^ expanded[at - 10]);
                expanded[at + 7] = (byte)(d ^ expanded[at - 9]);
            }
            _keys = new byte[12][];
            for (var i = 0; i < 12; i++)
                _keys[i] = expanded.AsSpan(i * 4, 4).ToArray();
        }

        public byte[] Encrypt(byte[] data, byte[] iv)
        {
            var plaintext = new byte[32];
            for (var i = 0; i < 31; i++)
            {
                var at = i * 8;
                var n0 = (Src(data, at) >> 4) & 2;
                var n1 = n0 | (Src(data, at + 1) & 64);
                var n2 = n1 | ((Src(data, at + 2) >> 2) & 1);
                var n3 = n2 | ((Src(data, at + 3) << 3) & 128);
                var n4 = n3 | ((Src(data, at + 4) >> 1) & 4);
                var n5 = n4 | ((Src(data, at + 5) << 3) & 16);
                var n6 = n5 | ((Src(data, at + 6) << 5) & 32);
                plaintext[i] = (byte)(n6 | ((Src(data, at + 7) >> 4) & 8));
            }
            plaintext[31] = 1;
            var blocks = new List<byte[]>();
            var previous = iv.ToArray();
            for (var i = 0; i < plaintext.Length; i += 16)
            {
                var mixed = new byte[16];
                for (var j = 0; j < 16; j++)
                    mixed[j] = (byte)(plaintext[i + j] ^ previous[j]);
                var value = Block(mixed);
                blocks.Add(value);
                previous = value;
            }
            var key = blocks.SelectMany(item => item).ToArray();
            var output = data.ToArray();
            for (var i = 0; i < 31; i++)
            {
                var at = i * 8;
                var k = key[i];
                Put(output, at, (Src(output, at) & 0xDF) | ((k << 4) & 32));
                Put(output, at + 1, (Src(output, at + 1) & 0xBF) | (k & 64));
                Put(output, at + 2, (Src(output, at + 2) & 0xFB) | ((k << 2) & 4));
                Put(output, at + 3, (Src(output, at + 3) & 0xEF) | ((k >> 3) & 16));
                Put(output, at + 4, (Src(output, at + 4) & 0xF7) | ((k + k) & 8));
                Put(output, at + 5, (Src(output, at + 5) & 0xFD) | ((k >> 3) & 2));
                Put(output, at + 6, (Src(output, at + 6) & 0xFE) | ((k >> 5) & 1));
                Put(output, at + 7, (Src(output, at + 7) & 0x7F) | ((k << 4) & 128));
            }
            return Cat(new[] { key[^1] }, output);
        }

        private byte[] Block(byte[] value)
        {
            var rows = Matrix(value);
            AddCon(rows, SubKeys(0));
            for (var i = 1; i < 3; i++)
            {
                Sub(rows);
                Shift(rows);
                if (i == 1)
                {
                    ShiftCon(rows);
                    Mix(rows);
                }
                AddCon(rows, SubKeys(i * 4));
            }
            Add(rows, SubKeys(4));
            return Flat(rows);
        }

        private byte[][] SubKeys(int from)
        {
            var rows = new byte[4][];
            for (var i = 0; i < 4; i++)
                rows[i] = _keys[from + i].ToArray();
            return rows;
        }

        private void Add(byte[][] rows, byte[][] key)
        {
            for (var i = 0; i < 4; i++)
                for (var j = 0; j < 4; j++)
                    rows[i][j] ^= key[i][j];
        }

        private void AddCon(byte[][] rows, byte[][] ks)
        {
            for (var i = 0; i < 4; i++)
                for (var j = 0; j < 4; j++)
                    rows[i][j] ^= ks[i][_con2[j]];
        }

        private void Sub(byte[][] rows)
        {
            for (var i = 0; i < 4; i++)
                for (var j = 0; j < 4; j++)
                    rows[i][j] = _box[rows[i][j]];
            var old = rows.Select(row => row.ToArray()).ToArray();
            for (var i = 0; i < 4; i++)
                rows[i] = old[_con2[i]];
        }

        private void Shift(byte[][] rows)
        {
            var flat = Flat(rows);
            for (var i = 0; i < 16; i++)
                rows[i / 4][i % 4] = flat[_order[i]];
        }

        private void ShiftCon(byte[][] rows)
        {
            var old = rows.Select(row => row.ToArray()).ToArray();
            for (var i = 0; i < 4; i++)
                for (var j = 0; j < 4; j++)
                    rows[i][j] = old[i][_con2[j]];
        }

        private static void Mix(byte[][] rows)
        {
            for (var i = 0; i < 4; i++)
            {
                var t = (byte)(rows[0][i] ^ rows[1][i] ^ rows[2][i] ^ rows[3][i]);
                var u = rows[0][i];
                rows[0][i] ^= (byte)(t ^ Xt((byte)(rows[0][i] ^ rows[1][i])));
                rows[1][i] ^= (byte)(t ^ Xt((byte)(rows[1][i] ^ rows[2][i])));
                rows[2][i] ^= (byte)(t ^ Xt((byte)(rows[2][i] ^ rows[3][i])));
                rows[3][i] ^= (byte)(t ^ Xt((byte)(rows[3][i] ^ u)));
            }
        }

        private static byte Xt(byte x) => (byte)(((x << 1) ^ ((x & 128) != 0 ? 0x1B : 0)) & 255);
        private static byte[][] Matrix(byte[] data) => Enumerable.Range(0, 4).Select(i => data.AsSpan(i * 4, 4).ToArray()).ToArray();
        private static byte[] Flat(byte[][] rows) => rows.SelectMany(row => row).ToArray();
        private static int Src(byte[] data, int idx) => idx >= 0 && idx < data.Length ? data[idx] : 0;
        private static void Put(byte[] data, int idx, int value)
        {
            if (idx >= 0 && idx < data.Length)
                data[idx] = (byte)value;
        }
    }
}
