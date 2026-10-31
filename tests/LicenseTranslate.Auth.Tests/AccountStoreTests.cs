using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LicenseTranslate.Auth;
using LicenseTranslate.Core;
using Xunit;

namespace LicenseTranslate.Auth.Tests;

public class AccountStoreTests
{
    [Fact]
    public void Session_stays_inside_the_program_directory_and_is_encrypted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "license-translate-" + Guid.NewGuid().ToString("N"));
        var store = new AccountStore(directory);
        store.Save(new AccountSession
        {
            ServerUrl = "https://auth.example",
            Account = "owner",
            Password = "secret-password",
            Token = "token",
            MachineId = "ABC",
        });

        var raw = File.ReadAllText(store.FilePath, Encoding.Latin1);
        Assert.DoesNotContain("secret-password", raw);
        Assert.Equal(Path.Combine(directory, "account.bin"), store.FilePath);
        var loaded = store.Load();
        Assert.Equal("secret-password", loaded.Password);
        Assert.Equal("owner", loaded.Account);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Login_rejects_a_response_without_a_signed_ticket()
    {
        var handler = new StubHandler("""{"ok":false,"message":"账号或密码错误"}""", HttpStatusCode.Unauthorized);
        var client = new AccountAuthClient(handler);
        var error = await Assert.ThrowsAsync<AccountRejectedException>(() =>
            client.LoginAsync("https://auth.example", "owner", "secret"));
        Assert.Contains("账号或密码错误", error.Message);
        Assert.Contains("/tt/account/login", handler.RequestUri);
    }

    [Fact]
    public void Ticket_must_name_this_product()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = AccountTicketVerifier.Verify(Ticket(key, AccountProduct.AppName), "MACHINE", "token-1", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        Assert.Equal("owner", identity.Subject);
        Assert.Throws<AccountRejectedException>(() =>
            AccountTicketVerifier.Verify(Ticket(key, "云帆多平台发布助手"), "MACHINE", "token-1", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
    }

    [Fact]
    public void Pdf_is_written_without_libreoffice()
    {
        var path = Path.Combine(Path.GetTempPath(), "license-translate-" + Guid.NewGuid().ToString("N") + ".pdf");
        BusinessLicensePdfWriter.Write(path, new BusinessLicenseDraft
        {
            CreditCode = "91110111MAKAEG7T43",
            EnglishName = "Beijing Xingzhuo Technology Co., Ltd.",
            EnglishType = "Limited Liability Company (Wholly Owned by Natural Person)",
            EnglishScope = "General items: software development.",
            EnglishPersonRole = "Legal Representative",
            EnglishPersonName = "Huang Xing",
        });
        var header = File.ReadAllText(path, Encoding.ASCII)[..8];
        Assert.Equal("%PDF-1.4", header);
        File.Delete(path);
    }

    private static string Ticket(ECDsa key, string appName)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["version"] = 1,
            ["key_id"] = "test",
            ["subject"] = "owner",
            ["email"] = "owner@example.com",
            ["machine_id"] = "MACHINE",
            ["token_sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("token-1"))).ToLowerInvariant(),
            ["app_name"] = appName,
            ["app_version"] = AccountProduct.AppVersion,
            ["issued_at"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
            ["not_after"] = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O"),
            ["account_expires_at"] = DateTimeOffset.UtcNow.AddDays(10).ToString("O"),
            ["verified_at"] = DateTimeOffset.UtcNow.ToString("O"),
        });
        var payloadPart = Base64Url(Encoding.UTF8.GetBytes(payload));
        var signed = Encoding.ASCII.GetBytes($"v1.test.{payloadPart}");
        var signature = Base64Url(key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        return $"v1.test.{payloadPart}.{signature}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class StubHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
