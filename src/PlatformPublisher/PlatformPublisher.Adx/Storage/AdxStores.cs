using System.Text;
using System.Text.Json;
using PlatformPublisher.Adx.Models;
using PlatformPublisher.Adx.Security;

namespace PlatformPublisher.Adx.Storage;

public sealed class AdxSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path;

    public AdxSettingsStore(string path) => _path = Path.GetFullPath(path);

    public AdxSettings Load()
    {
        try
        {
            return (JsonSerializer.Deserialize<AdxSettings>(File.ReadAllText(_path), JsonOptions) ?? new AdxSettings()).Normalize();
        }
        catch { return new AdxSettings(); }
    }

    public void Save(AdxSettings settings) => AtomicWrite(_path, JsonSerializer.Serialize(settings.Normalize(), JsonOptions));

    internal static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}

public sealed class AdxCredentialStore
{
    private readonly string _path;
    private readonly IAdxDataProtector _protector;

    public AdxCredentialStore(string path, IAdxDataProtector protector)
    {
        _path = Path.GetFullPath(path);
        _protector = protector;
    }

    public bool IsConfigured => File.Exists(_path) && new FileInfo(_path).Length > 0;

    public void Save(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("ADX 密码不能为空。", nameof(password));
        var encrypted = _protector.Protect(Encoding.UTF8.GetBytes(password));
        AdxSettingsStore.AtomicWrite(_path, Convert.ToBase64String(encrypted));
    }

    public string Load()
    {
        if (!IsConfigured) return string.Empty;
        try { return Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(File.ReadAllText(_path)))); }
        catch (Exception ex) { throw new InvalidOperationException("ADX 密码解密失败，请重新保存。", ex); }
    }
}

public sealed class AdxSessionStore
{
    private sealed record SessionEnvelope(string Identity, DateTimeOffset LastVerifiedAt, string StorageState);
    private readonly string _path;
    private readonly IAdxDataProtector _protector;

    public AdxSessionStore(string path, IAdxDataProtector protector)
    {
        _path = Path.GetFullPath(path);
        _protector = protector;
    }

    public (string StorageState, DateTimeOffset LastVerifiedAt)? Load(string identity)
    {
        try
        {
            var plain = _protector.Unprotect(Convert.FromBase64String(File.ReadAllText(_path)));
            var value = JsonSerializer.Deserialize<SessionEnvelope>(plain);
            return value is not null && string.Equals(value.Identity, identity, StringComparison.Ordinal)
                ? (value.StorageState, value.LastVerifiedAt)
                : null;
        }
        catch { return null; }
    }

    public void Save(string identity, string storageState)
    {
        var value = new SessionEnvelope(identity, DateTimeOffset.UtcNow, storageState);
        var encrypted = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(value));
        AdxSettingsStore.AtomicWrite(_path, Convert.ToBase64String(encrypted));
    }

    public void Clear() { if (File.Exists(_path)) File.Delete(_path); }
}
