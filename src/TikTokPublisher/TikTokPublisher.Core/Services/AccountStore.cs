using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

/// <summary>账号档案读写（JSON + 独立 profiles 目录）。</summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly List<TikTokAccountProfile> _accounts = new();
    private string _activeAccountId = "";
    private static string AccountSnapshotQuarantineFile =>
        AppPaths.AccountsFile + ".sync-quarantine";

    public IReadOnlyList<TikTokAccountProfile> Accounts => _accounts;
    public string ActiveAccountId => _activeAccountId;
    public bool CanSyncAccountSnapshot { get; private set; } = true;

    public event Action? AccountsChanged;

    public TikTokAccountProfile? ActiveAccount =>
        _accounts.FirstOrDefault(a => a.Id == _activeAccountId) ?? _accounts.FirstOrDefault();

    public void Load()
    {
        _accounts.Clear();
        _activeAccountId = "";
        CanSyncAccountSnapshot = !File.Exists(AccountSnapshotQuarantineFile);
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
            CanSyncAccountSnapshot = false;
            try
            {
                File.WriteAllText(AccountSnapshotQuarantineFile, DateTimeOffset.UtcNow.ToString("O"));
            }
            catch
            {
                // 文件隔离标记失败时仍保留当前进程内的保护。
            }
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
            // 账号数据被安装器重置后，工作目录里的旧队列仍会保留原账号 ID。
            // 固定复用 "default" 会让旧项目静默绑定到一个全新的空账号。
            var defaultAccount = CreateProfileSkeleton(CreateAccountId(), "默认账号");
            _accounts.Add(defaultAccount);
        }

        if (string.IsNullOrWhiteSpace(_activeAccountId)
            || _accounts.All(a => a.Id != _activeAccountId))
            _activeAccountId = _accounts[0].Id;

        var migratedProofConfig = MigrateLegacyProofMaterialConfig(_accounts);
        var migratedArchiveConfig = MigrateLegacyArchiveRootConfig(_accounts);
        foreach (var account in _accounts)
        {
            NormalizeProfileDefaults(account);
            EnsureProfileDirs(account);
        }

        if ((!File.Exists(AppPaths.AccountsFile) || migratedProofConfig || migratedArchiveConfig) &&
            _accounts.Count > 0)
            SaveAccounts();
    }

    public TikTokAccountProfile Add(string name)
    {
        var id = CreateAccountId();
        var account = CreateProfileSkeleton(id, string.IsNullOrWhiteSpace(name) ? id : name.Trim());
        _accounts.Add(account);
        SaveAccounts();
        NotifyAccountsChanged();
        return account;
    }

    internal static string CreateAccountId() =>
        "acct-" + Guid.NewGuid().ToString("N")[..8];

    public void Remove(TikTokAccountProfile account)
    {
        _accounts.Remove(account);
        if (_activeAccountId == account.Id)
            _activeAccountId = _accounts.FirstOrDefault()?.Id ?? "";
        SaveAccounts();
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

        NotifyAccountsChanged();
    }

    public void Update(TikTokAccountProfile account)
    {
        NormalizeProfileDefaults(account);
        account.UpdatedAt = DateTimeOffset.Now.ToString("o");
        EnsureProfileDirs(account);
        SaveAccounts();
        NotifyAccountsChanged();
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
        NotifyAccountsChanged();
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

    /// <summary>用户确认已修复账号文件后，解除快照同步隔离。</summary>
    public bool ConfirmAccountSnapshotRecovery()
    {
        if (!File.Exists(AccountSnapshotQuarantineFile))
            return false;
        File.Delete(AccountSnapshotQuarantineFile);
        CanSyncAccountSnapshot = true;
        NotifyAccountsChanged();
        return true;
    }

    private void SaveAccounts()
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        File.WriteAllText(AppPaths.AccountsFile, JsonSerializer.Serialize(_accounts, JsonOptions));
    }

    private void NotifyAccountsChanged()
    {
        foreach (var handler in AccountsChanged?.GetInvocationList().Cast<Action>() ?? [])
        {
            try { handler(); }
            catch { /* 同步观察者不得破坏本地账号保存。 */ }
        }
    }

    private void SaveActivePointer()
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        var payload = new ActiveAccountPointer { ActiveAccountId = _activeAccountId };
        File.WriteAllText(AppPaths.ActiveAccountFile, JsonSerializer.Serialize(payload, JsonOptions));
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
            TiktokLoginBrowserMode = "embedded",
            TiktokSeriesUrl = TikTokUrls.DefaultSeriesDraftUrl,
            TiktokContractIdMode = TikTokPublishConstants.ContractIdModeFirstAvailable,
            TiktokUploadBrowserMode = "embedded",
            TiktokPlaywrightUploadHeadless = false,
            TiktokPaidRatioEnabled = true,
            TiktokExpectedFullPriceMode = "option_index",
            TiktokDeleteVideosOnArchive = true,
            TiktokDeleteVideosOnArchiveConfigured = true,
            TiktokArchiveRootConfigMigrated = true,
            TiktokProofAccountConfigMigrated = true,
            TiktokQueueEnabledSteps = QueueStepRegistry.DefaultEnabledSteps.ToList(),
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

    private static void NormalizeProfileDefaults(TikTokAccountProfile account)
    {
        account.TiktokSubmitAction = NormalizeSubmitAction(account.TiktokSubmitAction, account.TiktokSubmitEnabled);
        account.TiktokSubmitEnabled = string.Equals(account.TiktokSubmitAction, "submit", StringComparison.Ordinal);
        account.TiktokLoginBrowserMode = NormalizeLoginBrowserMode(account.TiktokLoginBrowserMode);
        account.TiktokTargetAudienceMode = NormalizeTargetAudience(account.TiktokTargetAudienceMode);
        account.TiktokGenreCount = TikTokPublishOptions.NormalizeGenreCount(account.TiktokGenreCount);
        account.TiktokUploadBrowserMode = NormalizeUploadBrowserMode(account.TiktokUploadBrowserMode);
        if (!account.TiktokDeleteVideosOnArchiveConfigured)
            account.TiktokDeleteVideosOnArchive = true;
        if (account.TiktokProfilePreviewEpisodes <= 0) account.TiktokProfilePreviewEpisodes = 3;
        if (account.TiktokFreePreviewEpisodes <= 0) account.TiktokFreePreviewEpisodes = 3;
        if (account.TiktokProjectConcurrency <= 0) account.TiktokProjectConcurrency = 4;
        account.TiktokProofCopyrightCompanyName = (account.TiktokProofCopyrightCompanyName ?? "").Trim();
        account.TiktokProofDeclarantCompanyName = (account.TiktokProofDeclarantCompanyName ?? "").Trim();
        account.TiktokProofSealPath = (account.TiktokProofSealPath ?? "").Trim();
        account.TiktokArchiveRootDir = (account.TiktokArchiveRootDir ?? "").Trim();
        account.TiktokCopyrightMaterialTypes = TikTokPublishConstants
            .NormalizeCopyrightMaterialTypes(account.TiktokCopyrightMaterialTypes)
            .ToList();
        account.ManagementDedupScope = NormalizeManagementDedupScope(account.ManagementDedupScope);
    }

    private static bool MigrateLegacyProofMaterialConfig(IEnumerable<TikTokAccountProfile> accounts)
    {
        var pending = accounts.Where(account => !account.TiktokProofAccountConfigMigrated).ToArray();
        if (pending.Length == 0)
            return false;

        ClientSettings legacySettings;
        try
        {
            legacySettings = ClientSettingsStore.Load();
        }
        catch
        {
            // Retry the migration on the next load. A transient settings-database error
            // must not permanently mark legacy values as migrated.
            return false;
        }

        return ApplyLegacyProofMaterialConfig(pending, legacySettings);
    }

    internal static bool ApplyLegacyProofMaterialConfig(
        IEnumerable<TikTokAccountProfile> accounts,
        ClientSettings legacySettings)
    {
        var pending = accounts.Where(account => !account.TiktokProofAccountConfigMigrated).ToArray();
        if (pending.Length == 0)
            return false;

        foreach (var account in pending)
        {
            if (string.IsNullOrWhiteSpace(account.TiktokProofDeclarantCompanyName))
                account.TiktokProofDeclarantCompanyName = legacySettings.TiktokProofDeclarantCompanyName;
            if (string.IsNullOrWhiteSpace(account.TiktokProofSealPath))
                account.TiktokProofSealPath = legacySettings.TiktokProofSealPath;
            account.TiktokProofAccountConfigMigrated = true;
        }

        return true;
    }

    private static bool MigrateLegacyArchiveRootConfig(IEnumerable<TikTokAccountProfile> accounts)
    {
        var pending = accounts.Where(account => !account.TiktokArchiveRootConfigMigrated).ToArray();
        if (pending.Length == 0)
            return false;

        ClientSettings legacySettings;
        try
        {
            legacySettings = ClientSettingsStore.Load();
        }
        catch
        {
            return false;
        }

        return ApplyLegacyArchiveRootConfig(pending, legacySettings);
    }

    internal static bool ApplyLegacyArchiveRootConfig(
        IEnumerable<TikTokAccountProfile> accounts,
        ClientSettings legacySettings)
    {
        var pending = accounts.Where(account => !account.TiktokArchiveRootConfigMigrated).ToArray();
        if (pending.Length == 0)
            return false;

        var legacyRoot = (legacySettings.ArchiveRootDir ?? "").Trim();
        foreach (var account in pending)
        {
            if (string.IsNullOrWhiteSpace(account.TiktokArchiveRootDir))
                account.TiktokArchiveRootDir = legacyRoot;
            account.TiktokArchiveRootConfigMigrated = true;
        }

        return true;
    }

    private static string NormalizeSubmitAction(string? value, bool? legacyEnabled = null)
    {
        var action = (value ?? "").Trim().ToLowerInvariant();
        return action switch
        {
            "none" => "none",
            "submit" => "submit",
            "save" => "save",
            _ => legacyEnabled.HasValue && !legacyEnabled.Value ? "none" : "submit",
        };
    }

    private static string NormalizeTargetAudience(string? value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return normalized is "female" or "male" or "ai_recommend"
            ? normalized
            : "ai_recommend";
    }

    private static string NormalizeLoginBrowserMode(string? value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return normalized is "embedded" or "cdp"
            ? normalized
            : "embedded";
    }

    private static string NormalizeUploadBrowserMode(string? value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return normalized is "embedded" or "playwright"
            ? normalized
            : "embedded";
    }

    private static string NormalizeManagementDedupScope(string? value)
    {
        var normalized = (value ?? "tiktok_username").Trim().ToLowerInvariant();
        return normalized switch
        {
            "tiktok" or "tiktok_account" or "tiktok_account_username" or "tt_account" or "account_username" => "tiktok_username",
            "software" or "login_user" or "owner" or "owner_user" => "software_user",
            "all" or "global" or "all_records" or "all_tt_series" or "global_series" => "all_series",
            "tiktok_username" or "software_user" or "all_series" => normalized,
            _ => "tiktok_username"
        };
    }

    private sealed class ActiveAccountPointer
    {
        public string ActiveAccountId { get; set; } = "";
    }
}
