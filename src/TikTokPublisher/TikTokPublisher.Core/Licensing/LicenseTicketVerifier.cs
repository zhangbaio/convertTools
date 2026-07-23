using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TikTokPublisher.Core.Licensing;

internal sealed record LicenseTicketClaims(
    string Subject,
    string Email,
    string MachineId,
    string Edition,
    string Licensee,
    string AccountExpiresAt,
    string VerifiedAt,
    DateTimeOffset IssuedAt,
    DateTimeOffset NotAfter);

internal static class LicenseTicketVerifier
{
    private const string TicketVersion = "v1";
    private static readonly TimeSpan AllowedClockSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumTicketLifetime = TimeSpan.FromHours(2);

    private static readonly IReadOnlyDictionary<string, string> TrustedPublicKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a43f27492ae80346"] =
                "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEFixpIG9bqYuwW4M1Ssp5YxIY/J0Q2vLYgubgCrTbINWhnXdfO4C6tM19ahuGlwGAkP1CVu7nDjzCt5xJVctvYQ==",
            ["7f1d7347dcf4a710"] =
                "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEyYetc3ijKZOC2C3lKtr+Cg8B57eH5n8Xvkp0Y90jN78cRkR9ON7TFY+xEPlmbIjLUYldGODTy83hjMpPtc8ExA==",
        };

    public static LicenseTicketClaims VerifyFresh(
        string ticket,
        string expectedMachineId,
        string token,
        string expectedAppName,
        string expectedAppVersion,
        DateTimeOffset? now = null)
    {
        ticket ??= "";
        var parts = ticket.Split('.');
        if (parts.Length != 4 || !string.Equals(parts[0], TicketVersion, StringComparison.Ordinal))
            throw new LicenseRejectedException("授权服务器未返回有效的签名票据");

        var keyId = parts[1];
        if (!TrustedPublicKeys.TryGetValue(keyId, out var publicKey))
            throw new LicenseRejectedException("授权票据使用了客户端不信任的签名密钥");

        return VerifyWithPublicKey(
            ticket,
            expectedMachineId,
            token,
            expectedAppName,
            expectedAppVersion,
            publicKey,
            now);
    }

    internal static LicenseTicketClaims VerifyWithPublicKey(
        string ticket,
        string expectedMachineId,
        string token,
        string expectedAppName,
        string expectedAppVersion,
        string publicKeyBase64,
        DateTimeOffset? now = null)
    {
        try
        {
            var parts = (ticket ?? "").Split('.');
            if (parts.Length != 4 || !string.Equals(parts[0], TicketVersion, StringComparison.Ordinal))
                throw new LicenseRejectedException("授权票据格式无效");

            var signedValue = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}.{parts[2]}");
            var signature = DecodeBase64Url(parts[3]);
            var publicKeyBytes = Convert.FromBase64String(publicKeyBase64);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
            if (!ecdsa.VerifyData(
                    signedValue,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
                throw new LicenseRejectedException("授权票据签名无效");

            using var document = JsonDocument.Parse(DecodeBase64Url(parts[2]));
            var root = document.RootElement;
            if (ReadInt(root, "version") != 1
                || !string.Equals(ReadString(root, "key_id"), parts[1], StringComparison.Ordinal))
                throw new LicenseRejectedException("授权票据版本或密钥标识无效");

            var subject = ReadRequiredString(root, "subject");
            var machineId = ReadRequiredString(root, "machine_id");
            var tokenHash = ReadRequiredString(root, "token_sha256");
            var appName = ReadRequiredString(root, "app_name");
            var appVersion = ReadRequiredString(root, "app_version");
            if (!string.Equals(machineId, expectedMachineId, StringComparison.OrdinalIgnoreCase))
                throw new LicenseRejectedException("授权票据与当前设备不匹配");
            if (!string.Equals(tokenHash, HashToken(token), StringComparison.OrdinalIgnoreCase))
                throw new LicenseRejectedException("授权票据与登录凭证不匹配");
            if (!string.Equals(appName, expectedAppName, StringComparison.Ordinal)
                || !string.Equals(appVersion, expectedAppVersion, StringComparison.Ordinal))
                throw new LicenseRejectedException("授权票据与当前客户端版本不匹配");

            var issuedAt = ReadRequiredDate(root, "issued_at");
            var notAfter = ReadRequiredDate(root, "not_after");
            var current = now ?? DateTimeOffset.UtcNow;
            if (issuedAt > current.Add(AllowedClockSkew))
                throw new LicenseRejectedException("授权票据签发时间无效");
            if (notAfter <= current)
                throw new LicenseRejectedException("授权票据已过期");
            if (notAfter <= issuedAt || notAfter - issuedAt > MaximumTicketLifetime)
                throw new LicenseRejectedException("授权票据有效期无效");

            var accountExpiresAt = ReadString(root, "account_expires_at");
            if (!string.IsNullOrWhiteSpace(accountExpiresAt)
                && (!DateTimeOffset.TryParse(accountExpiresAt, out var accountExpiry)
                    || accountExpiry <= current))
                throw new LicenseRejectedException("软件授权已过期");

            return new LicenseTicketClaims(
                subject,
                ReadString(root, "email"),
                machineId,
                ReadString(root, "edition"),
                ReadString(root, "licensee"),
                accountExpiresAt,
                ReadRequiredString(root, "verified_at"),
                issuedAt,
                notAfter);
        }
        catch (LicenseRejectedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
        {
            throw new LicenseRejectedException($"授权票据无法验证：{ex.Message}");
        }
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? ""))).ToLowerInvariant();

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Convert.FromBase64String(normalized);
    }

    private static string ReadRequiredString(JsonElement root, string name)
    {
        var value = ReadString(root, name);
        return value.Length > 0
            ? value
            : throw new LicenseRejectedException($"授权票据缺少字段：{name}");
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static DateTimeOffset ReadRequiredDate(JsonElement root, string name)
    {
        var text = ReadRequiredString(root, name);
        return DateTimeOffset.TryParse(text, out var result)
            ? result
            : throw new LicenseRejectedException($"授权票据时间字段无效：{name}");
    }
}
