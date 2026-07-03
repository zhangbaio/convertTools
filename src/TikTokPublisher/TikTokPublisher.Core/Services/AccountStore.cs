using System.Text.Json;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 账号档案读写。切换账号时只更新 active id，不做 Python 那种「全量 profile JSON + 平铺 settings」同步。
/// </summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly List<TikTokAccountProfile> _accounts = new();
    private string _activeAccountId = "";
    private bool _syncing;

    /// <summary>保存/更新/删除账号后自动写回 Python <c>tiktok_uploader.db</c>。</summary>
    public bool AutoSyncToPythonDatabase { get; set; } = true;

    /// <summary>启动时从 Python DB 合并导入（Python 字段覆盖同 ID 本地档案）。</summary>
    public bool AutoImportFromPythonOnLoad { get; set; } = true;

    public IReadOnlyList<TikTokAccountProfile> Accounts => _accounts;
    public string ActiveAccountId => _activeAccountId;

    public TikTokAccountProfile? ActiveAccount =>
        _accounts.FirstOrDefault(a => a.Id == _activeAccountId) ?? _accounts.FirstOrDefault();

    public void Load()
    {
        _accounts.Clear();
        _activeAccountId = "";
        Directory.CreateDirectory(AppPaths.DataRoot);

        try
        {
            if (File.Exists(AppPaths.AccountsFile))
            {
                var list = JsonSerializer.Deserialize<List<TikTokAccountProfile>>(File.ReadAllText(AppPaths.AccountsFile), JsonOptions);
                if (list != null) _accounts.AddRange(list.Where(a => !string.IsNullOrWhiteSpace(a.Id)));
            }
        }
        catch
        {
            // 损坏档案从空列表恢复
        }

        try
        {
            if (File.Exists(AppPaths.ActiveAccountFile))
            {
                var active = JsonSerializer.Deserialize<ActiveAccountPointer>(
                    File.ReadAllText(AppPaths.ActiveAccountFile), JsonOptions);
                _activeAccountId = (active?.ActiveAccountId ?? "").Trim();
            }
        }
        catch
        {
            // 忽略
        }

        if (_accounts.Count == 0)
        {
            var defaultAccount = CreateProfileSkeleton("default", "默认账号");
            _accounts.Add(defaultAccount);
        }

        if (string.IsNullOrWhiteSpace(_activeAccountId)
            || _accounts.All(a => a.Id != _activeAccountId))
            _activeAccountId = _accounts[0].Id;

        foreach (var account in _accounts)
            EnsureProfileDirs(account);

        TryImportFromPythonOnLoad();
    }

    private void TryImportFromPythonOnLoad()
    {
        if (!AutoImportFromPythonOnLoad || !PythonAccountDatabaseSync.DatabaseExists())
            return;
        try
        {
            ImportFromPythonDatabase(merge: true, syncBack: false);
        }
        catch
        {
            // Python DB 损坏时不阻断 C# 启动
        }
    }

    public TikTokAccountProfile Add(string name)
    {
        var id = "acct-" + Guid.NewGuid().ToString("N")[..8];
        var account = CreateProfileSkeleton(id, string.IsNullOrWhiteSpace(name) ? id : name.Trim());
        _accounts.Add(account);
        SaveAccounts();
        return account;
    }

    public void Remove(TikTokAccountProfile account)
    {
        _accounts.Remove(account);
        if (_activeAccountId == account.Id)
            _activeAccountId = _accounts.FirstOrDefault()?.Id ?? "";
        SaveAccounts(syncPython: false);
        SaveActivePointer();

        try
        {
            if (Directory.Exists(account.ProfileDir))
                Directory.Delete(account.ProfileDir, recursive: true);
        }
        catch
        {
            // 浏览器占用时会失败，不影响档案删除
        }
    }

    public void Update(TikTokAccountProfile account)
    {
        account.UpdatedAt = DateTimeOffset.Now.ToString("o");
        EnsureProfileDirs(account);
        SaveAccounts();
    }

    public bool Rename(string accountId, string newName)
    {
        var name = (newName ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return false;
        var account = _accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null) return false;
        account.Name = name;
        account.UpdatedAt = DateTimeOffset.Now.ToString("o");
        SaveAccounts();
        return true;
    }

    public void SetActive(string accountId)
    {
        if (_accounts.All(a => a.Id != accountId)) return;
        _activeAccountId = accountId;
        SaveActivePointer();
    }

    public TikTokAccountProfile? FindByNameOrId(string nameOrId)
    {
        var key = (nameOrId ?? "").Trim();
        if (string.IsNullOrEmpty(key)) return null;
        return _accounts.FirstOrDefault(a => a.Id == key)
               ?? _accounts.FirstOrDefault(a => string.Equals(a.Name, key, StringComparison.OrdinalIgnoreCase))
               ?? _accounts.FirstOrDefault(a => string.Equals(a.DisplayName, key, StringComparison.OrdinalIgnoreCase));
    }

    public sealed class PythonImportResult
    {
        public int Imported { get; init; }
        public int Updated { get; init; }
        public int Skipped { get; init; }
        public int Exported { get; init; }
        public string ActiveProfileId { get; init; } = "";
        public string Source { get; init; } = "";
        public string Message { get; init; } = "";
    }

    public PythonImportResult SyncWithPythonDatabase(string? databasePath = null, bool merge = true)
    {
        var import = ImportFromPythonDatabase(databasePath, merge: merge, syncBack: false);
        var export = TryExportToPythonDatabase(databasePath);
        return new PythonImportResult
        {
            Imported = import.Imported,
            Updated = import.Updated,
            ActiveProfileId = import.ActiveProfileId,
            Source = import.Source,
            Exported = export?.Exported ?? 0,
            Message = export is null
                ? import.Message
                : $"账号双向同步：导入 {import.Imported} / 更新 {import.Updated} / 导出 {export.Exported}",
        };
    }

    /// <summary>从 Python <c>tiktok_uploader.db</c> 合并导入账号；同 ID 更新字段，保留已有 ProfileDir。</summary>
    public PythonImportResult ImportFromPythonDatabase(
        string? databasePath = null,
        bool merge = true,
        bool syncBack = true)
    {
        var bundle = PythonProfileImporter.Load(databasePath);
        var imported = 0;
        var updated = 0;

        if (!merge)
        {
            _accounts.Clear();
            imported = bundle.Profiles.Count;
            _accounts.AddRange(bundle.Profiles);
        }
        else
        {
            foreach (var incoming in bundle.Profiles)
            {
                var existing = _accounts.FirstOrDefault(a => a.Id == incoming.Id);
                if (existing is null)
                {
                    EnsureProfileDirs(incoming);
                    _accounts.Add(incoming);
                    imported++;
                }
                else
                {
                    var preservedDir = existing.ProfileDir;
                    TikTokAccountProfileMapper.ApplyToExisting(existing, incoming);
                    if (!string.IsNullOrWhiteSpace(preservedDir))
                        existing.ProfileDir = preservedDir;
                    EnsureProfileDirs(existing);
                    updated++;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(bundle.ActiveProfileId)
            && _accounts.Any(a => a.Id == bundle.ActiveProfileId))
            _activeAccountId = bundle.ActiveProfileId;

        foreach (var account in _accounts)
            EnsureProfileDirs(account);

        SaveAccounts(syncPython: false);
        SaveActivePointer(syncPython: false);

        if (syncBack)
            TryExportToPythonDatabase(databasePath);

        return new PythonImportResult
        {
            Imported = imported,
            Updated = updated,
            ActiveProfileId = _activeAccountId,
            Source = bundle.SourceDescription,
            Message = $"已从 Python 导入 {imported} 个、更新 {updated} 个账号（来源：{bundle.SourceDescription}）",
        };
    }

    public PythonAccountDatabaseSync.SyncResult? TryExportToPythonDatabase(string? databasePath = null)
    {
        if (!AutoSyncToPythonDatabase || _syncing || _accounts.Count == 0)
            return null;
        try
        {
            _syncing = true;
            return PythonAccountDatabaseSync.ExportProfiles(_accounts, _activeAccountId, databasePath);
        }
        catch
        {
            return null;
        }
        finally
        {
            _syncing = false;
        }
    }
    private void SaveAccounts(bool syncPython = true)
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        File.WriteAllText(AppPaths.AccountsFile, JsonSerializer.Serialize(_accounts, JsonOptions));
        if (syncPython)
            TryExportToPythonDatabase();
    }

    private void SaveActivePointer(bool syncPython = true)
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        var payload = new ActiveAccountPointer { ActiveAccountId = _activeAccountId };
        File.WriteAllText(AppPaths.ActiveAccountFile, JsonSerializer.Serialize(payload, JsonOptions));
        if (syncPython)
            TryExportToPythonDatabase();
    }

    private static TikTokAccountProfile CreateProfileSkeleton(string id, string name)
    {
        var now = DateTimeOffset.Now.ToString("o");
        return new TikTokAccountProfile
        {
            Id = id,
            Name = name,
            CreatedAt = now,
            UpdatedAt = now,
            ProfileDir = AppPaths.ProfileDirFor(id),
            TiktokStorageStatePath = AppPaths.DefaultStorageStatePath(id),
            TiktokSeriesUrl = TikTokUrls.DefaultSeriesDraftUrl,
        };
    }

    private static void EnsureProfileDirs(TikTokAccountProfile account)
    {
        if (string.IsNullOrWhiteSpace(account.ProfileDir))
            account.ProfileDir = AppPaths.ProfileDirFor(account.Id);
        Directory.CreateDirectory(account.ProfileDir);
        if (string.IsNullOrWhiteSpace(account.TiktokStorageStatePath))
            account.TiktokStorageStatePath = AppPaths.DefaultStorageStatePath(account.Id);
    }

    private sealed class ActiveAccountPointer
    {
        public string ActiveAccountId { get; set; } = "";
    }
}
