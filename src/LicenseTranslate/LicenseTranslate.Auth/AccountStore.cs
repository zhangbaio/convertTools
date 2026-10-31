using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LicenseTranslate.Auth;

public sealed class AccountStore
{
    private static readonly byte[] Magic = "LICENSE-TRANSLATE-ACCOUNT-V1\n"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public AccountStore(string directory)
    {
        DirectoryPath = directory;
    }

    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "account.bin");

    public AccountSession Load()
    {
        if (!File.Exists(FilePath))
            return new AccountSession();
        try
        {
            var bytes = File.ReadAllBytes(FilePath);
            if (bytes.Length <= Magic.Length || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                return new AccountSession();
            var payload = ProtectedData.Unprotect(bytes[Magic.Length..], Magic, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<AccountSession>(payload, JsonOptions) ?? new AccountSession();
        }
        catch (CryptographicException)
        {
            return new AccountSession();
        }
        catch (JsonException)
        {
            return new AccountSession();
        }
    }

    public void Save(AccountSession session)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
        var protectedBytes = ProtectedData.Protect(json, Magic, DataProtectionScope.CurrentUser);
        using var stream = new MemoryStream();
        stream.Write(Magic);
        stream.Write(protectedBytes);
        File.WriteAllBytes(FilePath, stream.ToArray());
    }

    public void Clear()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }
}
