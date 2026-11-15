using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TikTokPublisher.Ui.Services.TikTok;

/// <summary>剧集中心点播接口（ApplyUploadInner / CommitUploadInner）的 AWS4-HMAC-SHA256 签名，凭证来自上传授权 STS。</summary>
internal static class TikTokVodSigner
{
    public const string Region = "ap-singapore-1";
    public const string Service = "vod";

    public sealed record Credentials(string AccessKeyId, string SecretAccessKey, string SessionToken);

    public static void Sign(
        HttpRequestMessage request,
        Credentials credentials,
        string? body,
        DateTime utcNow,
        string service = Service)
    {
        var amzDate = utcNow.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var authorization = BuildAuthorization(
            request.Method.Method, request.RequestUri!, amzDate, credentials, body, service);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-security-token", credentials.SessionToken);
        if (body is not null)
            request.Headers.TryAddWithoutValidation("x-amz-content-sha256", Sha256Hex(body));
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
    }

    public static string BuildAuthorization(
        string method,
        Uri uri,
        string amzDate,
        Credentials credentials,
        string? body,
        string service = Service)
    {
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-amz-date"] = amzDate,
            ["x-amz-security-token"] = credentials.SessionToken,
        };
        if (body is not null)
            headers["x-amz-content-sha256"] = Sha256Hex(body);

        var canonicalHeaders = string.Concat(headers.Select(pair => $"{pair.Key}:{pair.Value}\n"));
        var signedHeaders = string.Join(";", headers.Keys);
        var canonicalRequest = string.Join("\n",
            method.ToUpperInvariant(),
            string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath,
            CanonicalQuery(uri.Query),
            canonicalHeaders,
            signedHeaders,
            Sha256Hex(body ?? ""));

        var date = amzDate[..8];
        var scope = $"{date}/{Region}/{service}/aws4_request";
        var stringToSign = string.Join("\n", "AWS4-HMAC-SHA256", amzDate, scope, Sha256Hex(canonicalRequest));

        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + credentials.SecretAccessKey), date);
        key = Hmac(key, Region);
        key = Hmac(key, service);
        key = Hmac(key, "aws4_request");
        var signature = Convert.ToHexString(Hmac(key, stringToSign)).ToLowerInvariant();
        return $"AWS4-HMAC-SHA256 Credential={credentials.AccessKeyId}/{scope}, " +
               $"SignedHeaders={signedHeaders}, Signature={signature}";
    }

    internal static string CanonicalQuery(string query)
    {
        var text = query.TrimStart('?');
        if (text.Length == 0)
            return "";

        return string.Join("&", text
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var index = part.IndexOf('=');
                var name = Uri.UnescapeDataString((index < 0 ? part : part[..index]).Replace('+', ' '));
                var value = index < 0 ? "" : Uri.UnescapeDataString(part[(index + 1)..].Replace('+', ' '));
                return (name, value);
            })
            .OrderBy(pair => pair.name, StringComparer.Ordinal)
            .ThenBy(pair => pair.value, StringComparer.Ordinal)
            .Select(pair => $"{Uri.EscapeDataString(pair.name)}={Uri.EscapeDataString(pair.value)}"));
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static byte[] Hmac(byte[] key, string text) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text));
}

/// <summary>TOS 分片上传要求的 CRC32（IEEE），以小写十六进制写入 <c>content-crc32</c>。</summary>
internal static class TikTokCrc32
{
    private static readonly uint[] Table = BuildTable();

    public static string Hex(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return (crc ^ 0xFFFFFFFFu).ToString("x8", CultureInfo.InvariantCulture);
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[index] = value;
        }

        return table;
    }
}
