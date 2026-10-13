using System.Text;
using System.Text.Json;
using PlatformPublisher.Adx.Models;
using PlatformPublisher.Adx.Security;
using PlatformPublisher.Persistence;

namespace PlatformPublisher.Adx.Storage;

public sealed class AdxSettingsStore
{
    private const string SettingsKey = "adx.settings";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path;
    private readonly IJsonSettingStore? _databaseStore;

    public AdxSettingsStore(string path) => _path = Path.GetFullPath(path);
    public AdxSettingsStore(IJsonSettingStore databaseStore, string legacyPath)
    {
        _databaseStore = databaseStore;
        _path = Path.GetFullPath(legacyPath);
    }

    public AdxSettings Load()
    {
        if (_databaseStore is not null)
        {
            if (_databaseStore.TryLoad<AdxSettings>(SettingsKey, out var stored) && stored is not null)
                return stored.Normalize();
            var legacy = LoadLegacy();
            _databaseStore.Save(SettingsKey, legacy);
            return legacy;
        }
        return LoadLegacy();
    }

    private AdxSettings LoadLegacy()
    {
        try
        {
            return (JsonSerializer.Deserialize<AdxSettings>(File.ReadAllText(_path), JsonOptions) ?? new AdxSettings()).Normalize();
        }
        catch { return new AdxSettings(); }
    }

    public void Save(AdxSettings settings)
    {
        if (_databaseStore is not null) _databaseStore.Save(SettingsKey, settings.Normalize());
        else AtomicWrite(_path, JsonSerializer.Serialize(settings.Normalize(), JsonOptions));
    }

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
    private const string DatabaseKey = "adx.password";
    private readonly string _path;
    private readonly IAdxDataProtector _protector;
    private readonly ISecureBlobStore? _blobStore;

    public AdxCredentialStore(string path, IAdxDataProtector protector)
    {
        _path = Path.GetFullPath(path);
        _protector = protector;
    }

    public AdxCredentialStore(string legacyPath, IAdxDataProtector protector, ISecureBlobStore blobStore)
        : this(legacyPath, protector) => _blobStore = blobStore;

    public bool IsConfigured => _blobStore?.Contains(DatabaseKey) == true || File.Exists(_path) && new FileInfo(_path).Length > 0;

    public void Save(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("ADX 密码不能为空。", nameof(password));
        var encrypted = _protector.Protect(Encoding.UTF8.GetBytes(password));
        if (_blobStore is not null) _blobStore.Save(DatabaseKey, "adx", encrypted);
        else AdxSettingsStore.AtomicWrite(_path, Convert.ToBase64String(encrypted));
    }

    public string Load()
    {
        if (!IsConfigured) return string.Empty;
        try
        {
            var encrypted = _blobStore?.Load(DatabaseKey, "adx");
            if (encrypted is null && File.Exists(_path))
            {
                encrypted = Convert.FromBase64String(File.ReadAllText(_path));
                _blobStore?.Save(DatabaseKey, "adx", encrypted);
            }
            return encrypted is null ? string.Empty : Encoding.UTF8.GetString(_protector.Unprotect(encrypted));
        }
        catch (Exception ex) { throw new InvalidOperationException("ADX 密码解密失败，请重新保存。", ex); }
    }
}

public sealed class AdxSessionStore
{
    private const string DatabaseKey = "adx.auth-state";
    private sealed record SessionEnvelope(string Identity, DateTimeOffset LastVerifiedAt, string StorageState);
    private readonly string _path;
    private readonly IAdxDataProtector _protector;
    private readonly ISecureBlobStore? _blobStore;

    public AdxSessionStore(string path, IAdxDataProtector protector)
    {
        _path = Path.GetFullPath(path);
        _protector = protector;
    }

    public AdxSessionStore(string legacyPath, IAdxDataProtector protector, ISecureBlobStore blobStore)
        : this(legacyPath, protector) => _blobStore = blobStore;

    public (string StorageState, DateTimeOffset LastVerifiedAt)? Load(string identity)
    {
        try
        {
            var encrypted = _blobStore?.Load(DatabaseKey, identity);
            if (encrypted is null && File.Exists(_path)) encrypted = Convert.FromBase64String(File.ReadAllText(_path));
            if (encrypted is null) return null;
            var plain = _protector.Unprotect(encrypted);
            var value = JsonSerializer.Deserialize<SessionEnvelope>(plain);
            if (value is null || !string.Equals(value.Identity, identity, StringComparison.Ordinal)) return null;
            _blobStore?.Save(DatabaseKey, identity, encrypted);
            return (value.StorageState, value.LastVerifiedAt);
        }
        catch { return null; }
    }

    public void Save(string identity, string storageState)
    {
        var value = new SessionEnvelope(identity, DateTimeOffset.UtcNow, storageState);
        var encrypted = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(value));
        if (_blobStore is not null) _blobStore.Save(DatabaseKey, identity, encrypted);
        else AdxSettingsStore.AtomicWrite(_path, Convert.ToBase64String(encrypted));
    }

    public void Clear() { _blobStore?.Delete(DatabaseKey); if (File.Exists(_path)) File.Delete(_path); }
}
