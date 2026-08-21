using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShortDrama.Infrastructure.Automation;

public static class HongguoHighCrypto
{
    public const string ApiBase = "https://m.iusc.cc/api/hbr/client/v1";
    public const string AppId = "hongguo_high_bitrate_desktop";
    public const string ClientVersion = "2.1.6";
    public const string RegistryKey = @"Software\HongGuoHighDownloader";
    public const string BookPrefix = "hghigh:";
    public const string EpisodePrefix = "hghigh_ep:";
    public const int ProtocolV2 = 2;
    public const int ProtocolStartup = 1;
    public const uint Ecs2Magic = 0x32534345;
    public const uint Ecs1Magic = 0x31534345;
    public const string StartupAlg = "AES-256-GCM+HMAC-SHA256";
    public const string StartupKid = "desktop-v1";
    public const string StartupRiskLevel = "bootstrap";
    public const string LetterSignDomain = "device-proof-sign-v1";
    public const int FanqieContentTypeComic = 1004;
    public const string FanqieDirectoryAid = "1967";

    public static readonly string[] LetterRequiredKeys = ["a", "b", "c", "d", "e", "f", "g", "h", "u", "v"];
    public static readonly string[] StartupRequiredKeys =
        ["alg", "app_id", "data", "iv", "nonce", "sign", "tag", "ts", "v"];

    public static readonly HashSet<string> AuthPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/auth/login",
        "/auth/register",
        "/auth/code",
        "/auth/password/reset",
        "/auth/device/refresh",
        "/auth/logout"
    };

    public static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static readonly byte[] DpapiEntropy = Encoding.ASCII.GetBytes(AppId);

    public static byte[] GzipStoreJson(JsonNode node)
    {
        var raw = Encoding.UTF8.GetBytes(node.ToJsonString(CompactJson));
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.NoCompression, leaveOpen: true))
        {
            gzip.Write(raw, 0, raw.Length);
        }

        var packed = output.ToArray();
        if (packed.Length >= 10)
        {
            packed[8] = 0;
            packed[9] = 255;
        }

        return packed;
    }

    public static string ToBase64Url(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string? text)
    {
        var cleaned = string.Concat((text ?? string.Empty).Where(ch => !char.IsWhiteSpace(ch)));
        if (cleaned.Length == 0)
        {
            return [];
        }

        var padded = cleaned.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        return Convert.FromBase64String(padded);
    }

    public static string GenerateDeviceId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    public static ECDsa ParseCngPrivateKey(byte[] blob)
    {
        if (blob.Length < 104)
        {
            throw new HongguoHighException("ECDSA 私钥 blob 长度不足");
        }

        var magic = BitConverter.ToUInt32(blob, 0);
        if (magic != Ecs2Magic)
        {
            throw new HongguoHighException($"ECDSA 私钥 magic 异常：0x{magic:x}");
        }

        var cbKey = BitConverter.ToInt32(blob, 4);
        if (cbKey != 32)
        {
            throw new HongguoHighException($"ECDSA cbKey 异常：{cbKey}");
        }

        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = blob[8..40],
                Y = blob[40..72]
            },
            D = blob[72..104]
        };
        var ecdsa = ECDsa.Create(parameters);
        var actual = ecdsa.ExportParameters(includePrivateParameters: false);
        if (!actual.Q.X!.SequenceEqual(parameters.Q.X!) || !actual.Q.Y!.SequenceEqual(parameters.Q.Y!))
        {
            throw new HongguoHighException("ECDSA 私钥与公钥点不匹配");
        }

        return ecdsa;
    }

    public static ECDsa ParseDeviceKeyText(string text)
    {
        var lines = (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            byte[] blob;
            try
            {
                blob = FromBase64Url(line);
            }
            catch (FormatException)
            {
                continue;
            }

            if (blob.Length >= 8 && BitConverter.ToUInt32(blob, 0) == Ecs2Magic)
            {
                return ParseCngPrivateKey(blob);
            }
        }

        var joined = new List<byte>();
        foreach (var line in lines)
        {
            try
            {
                joined.AddRange(FromBase64Url(line));
            }
            catch (FormatException)
            {
                // Ignore non-key lines in the DeviceKey text blob.
            }
        }

        var raw = joined.ToArray();
        var magic = BitConverter.GetBytes(Ecs2Magic);
        var index = IndexOf(raw, magic);
        if (index >= 0 && index + 104 <= raw.Length)
        {
            return ParseCngPrivateKey(raw[index..(index + 104)]);
        }

        throw new HongguoHighException("未在 DeviceKey 中找到 ECS2 私钥");
    }

    public static byte[] PackEcs2(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: true);
        var blob = new byte[104];
        BitConverter.GetBytes(Ecs2Magic).CopyTo(blob, 0);
        BitConverter.GetBytes(32).CopyTo(blob, 4);
        parameters.Q.X!.CopyTo(blob, 8);
        parameters.Q.Y!.CopyTo(blob, 40);
        parameters.D!.CopyTo(blob, 72);
        return blob;
    }

    public static byte[] Ecs1PublicBlob(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var blob = new byte[72];
        BitConverter.GetBytes(Ecs1Magic).CopyTo(blob, 0);
        BitConverter.GetBytes(32).CopyTo(blob, 4);
        parameters.Q.X!.CopyTo(blob, 8);
        parameters.Q.Y!.CopyTo(blob, 40);
        return blob;
    }

    public static byte[] SignP1363(ECDsa key, byte[] message) =>
        key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) AesGcmEncrypt(byte[] plain, byte[] key)
    {
        var aesKey = key.Length is 16 or 32 ? key : SHA256.HashData(key);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(aesKey, 16);
        gcm.Encrypt(nonce, plain, ciphertext, tag);
        return (nonce, ciphertext, tag);
    }

    public static byte[] StartupSignMessage(
        string method,
        string path,
        int ts,
        string nonceB64,
        string ivB64,
        string dataB64,
        string tagB64) =>
        Encoding.UTF8.GetBytes(string.Join("\n",
        [
            (method ?? "").ToUpperInvariant(),
            path ?? "",
            AppId,
            "win",
            ClientVersion,
            "",
            "",
            "",
            StartupRiskLevel,
            StartupKid,
            ts.ToString(),
            nonceB64,
            ivB64,
            dataB64,
            tagB64
        ]));

    public static string LetterCanonical(
        string method,
        string path,
        string deviceId,
        string token,
        string sessionId,
        string riskLevel,
        int ts,
        string eB64,
        string fB64,
        string gB64,
        string hB64,
        string flowId,
        int seq,
        string payloadHash) =>
        string.Join("\n",
        [
            (method ?? "").ToUpperInvariant(),
            path ?? "",
            AppId,
            "win",
            ClientVersion,
            deviceId,
            token,
            sessionId,
            riskLevel,
            "",
            ts.ToString(),
            eB64,
            fB64,
            gB64,
            hB64,
            flowId,
            seq.ToString(),
            payloadHash
        ]);

    public static (byte[] EncKey, byte[] SignKey) DeriveStartupKeys(string encMasterB64, string signMasterB64)
    {
        var encKey = SHA256.HashData(Encoding.UTF8.GetBytes(encMasterB64 + "|enc|" + StartupKid));
        var signKey = SHA256.HashData(Encoding.UTF8.GetBytes(signMasterB64 + "|sign|" + StartupKid));
        return (encKey, signKey);
    }

    public static byte[] DeriveSessionAesKey(string sessionKeyB64, string sessionKeyId, string sessionId)
    {
        var kid = string.IsNullOrWhiteSpace(sessionKeyId) ? "session-v1" : sessionKeyId;
        return SHA256.HashData(Encoding.UTF8.GetBytes(sessionKeyB64 + "|enc|" + kid + "|" + sessionId));
    }

    public static byte[] DeriveSessionSignKey(
        string sessionKeyB64,
        string sessionKeyId,
        string sessionId,
        string deviceId)
    {
        var kid = string.IsNullOrWhiteSpace(sessionKeyId) ? "session-v1" : sessionKeyId;
        return SHA256.HashData(Encoding.UTF8.GetBytes(
            sessionKeyB64 + "|proof-v2|" + kid + "|" + sessionId + "|" + deviceId + "|"));
    }

    public static JsonObject BuildStartupInner(
        HongguoHighDevice device,
        string path,
        JsonObject data,
        string deviceProof)
    {
        var pubB64 = ToBase64Url(Ecs1PublicBlob(device.PrivateKey));
        var param = new JsonObject
        {
            ["deviceId"] = device.DeviceId,
            ["device_id"] = device.DeviceId,
            ["deviceProof"] = deviceProof,
            ["device_proof"] = deviceProof,
            ["deviceProofVersion"] = "device-proof-v2",
            ["device_proof_version"] = "device-proof-v2",
            ["devicePublicKey"] = pubB64,
            ["device_public_key"] = pubB64
        };
        MergeObject(param, data);
        return new JsonObject
        {
            ["appId"] = AppId,
            ["app_id"] = AppId,
            ["clientVersion"] = ClientVersion,
            ["name"] = path,
            ["param"] = param,
            ["path"] = path,
            ["payload"] = param.DeepClone(),
            ["platform"] = "win",
            ["version"] = ClientVersion
        };
    }

    public static JsonObject BuildBusinessInner(
        HongguoHighDevice device,
        HongguoHighSession session,
        string path,
        JsonObject data,
        string deviceProof)
    {
        var pubB64 = ToBase64Url(Ecs1PublicBlob(device.PrivateKey));
        var flowId = string.IsNullOrWhiteSpace(session.FlowId)
            ? ToBase64Url(RandomNumberGenerator.GetBytes(18))[..24]
            : session.FlowId;
        var inner = new JsonObject
        {
            ["app_id"] = AppId,
            ["device_id"] = device.DeviceId,
            ["deviceId"] = device.DeviceId,
            ["deviceProof"] = deviceProof,
            ["device_proof"] = deviceProof,
            ["deviceProofVersion"] = "device-proof-v2",
            ["device_proof_version"] = "device-proof-v2",
            ["devicePublicKey"] = pubB64,
            ["device_public_key"] = pubB64,
            ["flow_id"] = flowId,
            ["lease_present"] = !string.IsNullOrWhiteSpace(session.AccessToken),
            ["name"] = path,
            ["param"] = data.DeepClone(),
            ["path"] = path,
            ["payload"] = data.DeepClone(),
            ["platform"] = "win",
            ["request_seq"] = session.RequestSeq + 1,
            ["risk_findings"] = new JsonArray(),
            ["risk_level"] = "normal",
            ["risk_score"] = 0,
            ["version"] = ClientVersion,
            ["clientVersion"] = ClientVersion,
            ["client_version"] = ClientVersion
        };
        if (!string.IsNullOrWhiteSpace(session.SessionId))
        {
            inner["session_id"] = session.SessionId;
        }

        foreach (var property in data)
        {
            if (property.Key is "path" or "name")
            {
                continue;
            }

            inner[property.Key] = property.Value?.DeepClone();
        }

        return inner;
    }

    public static JsonObject BuildStartupEnvelope(
        JsonObject inner,
        string method,
        string path,
        byte[] encKey,
        byte[] signKey)
    {
        var ts = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var raw = Encoding.UTF8.GetBytes(inner.ToJsonString(CompactJson));
        var (nonce, ciphertext, tag) = AesGcmEncrypt(raw, encKey);
        var dataB64 = ToBase64Url(ciphertext);
        var ivB64 = ToBase64Url(nonce);
        var tagB64 = ToBase64Url(tag);
        var nonceB64 = ToBase64Url(RandomNumberGenerator.GetBytes(16));
        var message = StartupSignMessage(method, path, ts, nonceB64, ivB64, dataB64, tagB64);
        var sign = Convert.ToHexString(HMACSHA256.HashData(signKey, message)).ToLowerInvariant();
        return new JsonObject
        {
            ["alg"] = StartupAlg,
            ["app_id"] = AppId,
            ["data"] = dataB64,
            ["iv"] = ivB64,
            ["key_id"] = StartupKid,
            ["kid"] = StartupKid,
            ["nonce"] = nonceB64,
            ["platform"] = "win",
            ["risk_level"] = StartupRiskLevel,
            ["sign"] = sign,
            ["tag"] = tagB64,
            ["time"] = ts,
            ["ts"] = ts,
            ["v"] = ProtocolStartup,
            ["version"] = ClientVersion
        };
    }

    public static JsonObject BuildLetterEnvelope(
        HongguoHighDevice device,
        HongguoHighSession session,
        JsonObject inner,
        string method,
        string path)
    {
        var seq = session.NextSeq();
        var ts = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var raw = Encoding.UTF8.GetBytes(inner.ToJsonString(CompactJson));
        var aesKey = DeriveSessionAesKey(session.SessionKeyB64, session.SessionKeyId, session.SessionId);
        var (gcmNonce, ciphertext, tag) = AesGcmEncrypt(raw, aesKey);
        var eB64 = ToBase64Url(RandomNumberGenerator.GetBytes(16));
        var fB64 = ToBase64Url(gcmNonce);
        var gB64 = ToBase64Url(ciphertext);
        var hB64 = ToBase64Url(tag);
        var sessionId = string.IsNullOrWhiteSpace(session.SessionId)
            ? ToBase64Url(RandomNumberGenerator.GetBytes(24))
            : session.SessionId;
        var bearer = TrimBearer(session.AccessToken);
        var riskLevel = inner["risk_level"]?.GetValue<string>() ?? "normal";
        var riskScore = inner["risk_score"]?.GetValue<int>() ?? 0;
        var flowId = inner["flow_id"]?.GetValue<string>()
                     ?? (string.IsNullOrWhiteSpace(session.FlowId)
                         ? ToBase64Url(RandomNumberGenerator.GetBytes(18))[..24]
                         : session.FlowId);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gB64 + "." + hB64)))
            .ToLowerInvariant();
        var canonical = LetterCanonical(
            method, path, device.DeviceId, bearer, sessionId, riskLevel, ts,
            eB64, fB64, gB64, hB64, flowId, seq, payloadHash);
        var signKey = DeriveSessionSignKey(session.SessionKeyB64, session.SessionKeyId, session.SessionId, device.DeviceId);
        var rHex = Convert.ToHexString(HMACSHA256.HashData(signKey, Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        var signMessage = Encoding.UTF8.GetBytes(LetterSignDomain + "\n" + canonical + "\n" + rHex);
        var env = new JsonObject
        {
            ["a"] = AppId,
            ["b"] = "win",
            ["c"] = ClientVersion,
            ["d"] = ts,
            ["e"] = eB64,
            ["f"] = fB64,
            ["g"] = gB64,
            ["h"] = hB64,
            ["i"] = device.DeviceId,
            ["l"] = sessionId,
            ["m"] = riskLevel,
            ["n"] = riskScore,
            ["o"] = flowId,
            ["p"] = ToBase64Url(SignP1363(device.PrivateKey, signMessage)),
            ["r"] = rHex,
            ["s"] = seq,
            ["u"] = string.IsNullOrWhiteSpace(session.SessionKeyId) ? "session-v1" : session.SessionKeyId,
            ["v"] = ProtocolV2
        };
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            env["j"] = bearer;
        }

        return env;
    }

    public static JsonObject BatchParseEpisodePayload(string videoId, int episodeNumber)
    {
        var title = episodeNumber > 0 ? $"第{episodeNumber}集" : videoId;
        return new JsonObject
        {
            ["episodeId"] = videoId,
            ["episode_id"] = videoId,
            ["videoId"] = videoId,
            ["video_id"] = videoId,
            ["episodeNumber"] = episodeNumber,
            ["episode_number"] = episodeNumber,
            ["title"] = title,
            ["contentType"] = FanqieContentTypeComic,
            ["content_type"] = FanqieContentTypeComic
        };
    }

    public static string NormalizeQuality(string? quality)
    {
        var text = (quality ?? "").Trim().ToLowerInvariant().Replace("p+", "", StringComparison.Ordinal);
        if (text.Contains("2160", StringComparison.Ordinal) || text.Contains("4k", StringComparison.Ordinal))
        {
            return "2160";
        }

        if (text.Contains("1080", StringComparison.Ordinal))
        {
            return "1080";
        }

        if (text.Contains("720", StringComparison.Ordinal))
        {
            return "720";
        }

        if (text.Contains("480", StringComparison.Ordinal))
        {
            return "480";
        }

        var digits = new string(text.Where(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(digits) ? "1080" : digits;
    }

    public static string TrimBearer(string? token)
    {
        var value = (token ?? "").Trim();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? value[7..].Trim()
            : value;
    }

    public static string EncodeEpisodeId(string bookId, int episodeNumber, string videoId) =>
        $"{EpisodePrefix}{StripBookPrefix(bookId)}|{episodeNumber}|{videoId}";

    public static bool TryDecodeEpisodeId(string value, out string bookId, out int episodeNumber, out string videoId)
    {
        bookId = "";
        episodeNumber = 0;
        videoId = "";
        var text = value.StartsWith(EpisodePrefix, StringComparison.OrdinalIgnoreCase)
            ? value[EpisodePrefix.Length..]
            : value;
        var parts = text.Split('|', 3);
        if (parts.Length != 3 || !int.TryParse(parts[1], out episodeNumber))
        {
            return false;
        }

        bookId = parts[0];
        videoId = parts[2];
        return !string.IsNullOrWhiteSpace(bookId) && !string.IsNullOrWhiteSpace(videoId);
    }

    public static string EnsureBookPrefix(string? bookId)
    {
        var text = (bookId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        return text.StartsWith(BookPrefix, StringComparison.OrdinalIgnoreCase) ? text : BookPrefix + text;
    }

    public static string StripBookPrefix(string? bookId)
    {
        var text = (bookId ?? "").Trim();
        return text.StartsWith(BookPrefix, StringComparison.OrdinalIgnoreCase)
            ? text[BookPrefix.Length..]
            : text;
    }

    private static void MergeObject(JsonObject target, JsonObject source)
    {
        foreach (var property in source)
        {
            target[property.Key] = property.Value?.DeepClone();
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return i;
            }
        }

        return -1;
    }
}

public sealed class HongguoHighDevice : IDisposable
{
    public HongguoHighDevice(string deviceId, ECDsa privateKey)
    {
        DeviceId = deviceId;
        PrivateKey = privateKey;
    }

    public string DeviceId { get; }
    public ECDsa PrivateKey { get; }

    public void Dispose() => PrivateKey.Dispose();
}

public sealed class HongguoHighSession
{
    public string AccessToken { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string SessionKeyB64 { get; set; } = "";
    public string SessionKeyId { get; set; } = "session-v1";
    public string FlowId { get; set; } = "";
    public string Account { get; set; } = "";
    public string BoundDeviceId { get; set; } = "";
    public int RequestSeq { get; set; }

    public int NextSeq() => ++RequestSeq;

    public void Clear()
    {
        AccessToken = "";
        SessionId = "";
        SessionKeyB64 = "";
        SessionKeyId = "session-v1";
        FlowId = "";
        Account = "";
        BoundDeviceId = "";
        RequestSeq = 0;
    }
}
