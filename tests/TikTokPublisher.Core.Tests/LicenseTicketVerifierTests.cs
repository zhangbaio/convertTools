using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Licensing;

namespace TikTokPublisher.Core.Tests;

public sealed class LicenseTicketVerifierTests
{
    [Fact]
    public void VerifyWithPublicKey_AcceptsPythonCryptographyTicketVector()
    {
        const string publicKey =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEaxfR8uEsQkf4vOblY6RA8ncDfYEt6zOg9KE5RdiYwpZP40Li/hp/m47n60p8D54WK84zV2sxXs7LtkBoN79R9Q==";
        const string ticket =
            "v1.python-vector.eyJhY2NvdW50X2V4cGlyZXNfYXQiOiIyMDMxLTAxLTAxVDAwOjAwOjAwKzAwOjAwIiwiYXBwX25hbWUiOiLkupHluIbliafpm4blt6XlnYoiLCJhcHBfdmVyc2lvbiI6IjAuMS4wIiwiZWRpdGlvbiI6InBybyIsImVtYWlsIjoicHl0aG9uQGV4YW1wbGUudGVzdCIsImlzc3VlZF9hdCI6IjIwMzAtMDEtMDFUMDA6MDA6MDArMDA6MDAiLCJrZXlfaWQiOiJweXRob24tdmVjdG9yIiwibGljZW5zZWUiOiJweXRob24tdXNlciIsIm1hY2hpbmVfaWQiOiJweXRob24tbWFjaGluZSIsIm5vdF9hZnRlciI6IjIwMzAtMDEtMDFUMDE6MTA6MDArMDA6MDAiLCJzdWJqZWN0IjoicHl0aG9uLXVzZXIiLCJ0b2tlbl9zaGEyNTYiOiIzMzA5MmU2Y2M5OTUyNDk1ZWY0M2ExOTBmZTU5NjgwOWM5ZmE2NWE1M2VmY2VlOGE2NzhmNjVlNzVlOTgzN2NmIiwidmVyaWZpZWRfYXQiOiIyMDMwLTAxLTAxVDAwOjAwOjAwKzAwOjAwIiwidmVyc2lvbiI6MX0.MEYCIQC7btVMWg_061Ol1m6WiHa1kEVdwdaEiE-9PwdEZExYJQIhAJKKwFI6z9kJLCCbo7k3JCFMR3GMF2VBX_ipLWvvyXWV";

        var claims = LicenseTicketVerifier.VerifyWithPublicKey(
            ticket,
            "python-machine",
            "python-token",
            LicenseAuthService.AppName,
            LicenseAuthService.AppVersion,
            publicKey,
            DateTimeOffset.Parse("2030-01-01T00:30:00+00:00"));

        claims.Subject.Should().Be("python-user");
    }

    [Fact]
    public void VerifyWithPublicKey_AcceptsValidBoundTicket()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        const string machineId = "machine-123";
        const string token = "token-value";
        var ticket = CreateTicket(key, machineId, token, now);

        var claims = LicenseTicketVerifier.VerifyWithPublicKey(
            ticket,
            machineId,
            token,
            LicenseAuthService.AppName,
            LicenseAuthService.AppVersion,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            now);

        claims.Subject.Should().Be("signed-user");
        claims.MachineId.Should().Be(machineId);
        claims.Edition.Should().Be("pro");
    }

    [Fact]
    public void VerifyWithPublicKey_RejectsTamperedPayload()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var ticket = CreateTicket(key, "machine-123", "token-value", now);
        var parts = ticket.Split('.');
        parts[2] = $"{(parts[2][0] == 'A' ? 'B' : 'A')}{parts[2][1..]}";

        var action = () => LicenseTicketVerifier.VerifyWithPublicKey(
            string.Join('.', parts),
            "machine-123",
            "token-value",
            LicenseAuthService.AppName,
            LicenseAuthService.AppVersion,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            now);

        action.Should().Throw<LicenseRejectedException>();
    }

    [Theory]
    [InlineData("different-machine", "token-value")]
    [InlineData("machine-123", "different-token")]
    public void VerifyWithPublicKey_RejectsBindingMismatch(string machineId, string token)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var ticket = CreateTicket(key, "machine-123", "token-value", now);

        var action = () => LicenseTicketVerifier.VerifyWithPublicKey(
            ticket,
            machineId,
            token,
            LicenseAuthService.AppName,
            LicenseAuthService.AppVersion,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            now);

        action.Should().Throw<LicenseRejectedException>();
    }

    [Fact]
    public void VerifyWithPublicKey_RejectsExpiredTicket()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var ticket = CreateTicket(key, "machine-123", "token-value", issuedAt);

        var action = () => LicenseTicketVerifier.VerifyWithPublicKey(
            ticket,
            "machine-123",
            "token-value",
            LicenseAuthService.AppName,
            LicenseAuthService.AppVersion,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            DateTimeOffset.UtcNow);

        action.Should().Throw<LicenseRejectedException>();
    }

    private static string CreateTicket(
        ECDsa key,
        string machineId,
        string token,
        DateTimeOffset issuedAt)
    {
        const string keyId = "test-key";
        var payload = new SortedDictionary<string, object?>
        {
            ["account_expires_at"] = issuedAt.AddDays(1).ToString("O"),
            ["app_name"] = LicenseAuthService.AppName,
            ["app_version"] = LicenseAuthService.AppVersion,
            ["edition"] = "pro",
            ["email"] = "signed-user@example.test",
            ["issued_at"] = issuedAt.ToString("O"),
            ["key_id"] = keyId,
            ["licensee"] = "signed-user",
            ["machine_id"] = machineId,
            ["not_after"] = issuedAt.AddMinutes(70).ToString("O"),
            ["subject"] = "signed-user",
            ["token_sha256"] = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant(),
            ["verified_at"] = issuedAt.ToString("O"),
            ["version"] = 1,
        };
        var payloadSegment = EncodeBase64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signedValue = $"v1.{keyId}.{payloadSegment}";
        var signature = key.SignData(
            Encoding.ASCII.GetBytes(signedValue),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        return $"{signedValue}.{EncodeBase64Url(signature)}";
    }

    private static string EncodeBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
