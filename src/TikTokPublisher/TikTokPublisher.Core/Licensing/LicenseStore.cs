using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TikTokPublisher.Core.Licensing;

public static class LicenseStore
{
    private static readonly byte[] StateMagic = "TUC-ACCOUNT-STATE-V1\n"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static string StateDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tiktok_uploader_client");

    public static string StatePath => Path.Combine(StateDirectory, "account_state.bin");
    public static string LegacyStatePath => Path.Combine(StateDirectory, "license.json");

    public static LicenseState Load()
    {
        if (File.Exists(StatePath))
        {
            try
            {
                return ReadEncrypted(File.ReadAllBytes(StatePath));
            }
            catch
            {
                return new LicenseState();
            }
        }

        if (File.Exists(LegacyStatePath))
        {
            try
            {
                return JsonSerializer.Deserialize<LicenseState>(File.ReadAllText(LegacyStatePath), JsonOptions)
                       ?? new LicenseState();
            }
            catch
            {
                return new LicenseState();
            }
        }

        return new LicenseState();
    }

    public static void Save(LicenseState state)
    {
        Directory.CreateDirectory(StateDirectory);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        var payload = Encoding.UTF8.GetBytes(json);
        var protectedBytes = DpapiProtect(payload);
        using var stream = new MemoryStream();
        stream.Write(StateMagic);
        stream.Write(protectedBytes);
        File.WriteAllBytes(StatePath, stream.ToArray());
    }

    public static void Clear() => File.Delete(StatePath);

    public static string MaskLicenseKey(string licenseKey)
    {
        var text = (licenseKey ?? "").Trim();
        return text.Length <= 8 ? text : $"{text[..4]}****{text[^4..]}";
    }

    private static LicenseState ReadEncrypted(byte[] raw)
    {
        if (raw.Length <= StateMagic.Length || !raw.AsSpan(0, StateMagic.Length).SequenceEqual(StateMagic))
            return new LicenseState();
        var payload = DpapiUnprotect(raw.AsSpan(StateMagic.Length).ToArray());
        var state = JsonSerializer.Deserialize<LicenseState>(Encoding.UTF8.GetString(payload), JsonOptions)
                    ?? new LicenseState();
        if (string.IsNullOrWhiteSpace(state.LicenseKeyMasked) && !string.IsNullOrWhiteSpace(state.LicenseKey))
            state.LicenseKeyMasked = MaskLicenseKey(state.LicenseKey);
        return state;
    }

    private static byte[] DpapiProtect(byte[] data)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return data;
        return ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
    }

    private static byte[] DpapiUnprotect(byte[] data)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return data;
        return ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
    }
}
