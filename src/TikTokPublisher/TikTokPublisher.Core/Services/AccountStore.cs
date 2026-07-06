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
        {
            NormalizeProfileDefaults(account);
            EnsureProfileDirs(account);
        }

        if (!File.Exists(AppPaths.AccountsFile) && _accounts.Count > 0)
            SaveAccounts();
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
    }

    public void Update(TikTokAccountProfile account)
    {
        NormalizeProfileDefaults(account);
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

    private void SaveAccounts()
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        File.WriteAllText(AppPaths.AccountsFile, JsonSerializer.Serialize(_accounts, JsonOptions));
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
            TiktokSeriesUrl = TikTokUrls.DefaultSeriesDraftUrl,
            TiktokContractIdMode = TikTokPublishConstants.ContractIdModeFirstAvailable,
            TiktokUploadBrowserMode = "embedded",
            TiktokPlaywrightUploadHeadless = false,
            TiktokPaidRatioEnabled = true,
            TiktokExpectedFullPriceMode = "option_index",
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
        account.TiktokTargetAudienceMode = NormalizeTargetAudience(account.TiktokTargetAudienceMode);
        account.TiktokGenreCount = TikTokPublishOptions.NormalizeGenreCount(account.TiktokGenreCount);
        account.TiktokUploadBrowserMode = NormalizeUploadBrowserMode(account.TiktokUploadBrowserMode);
        if (account.TiktokProfilePreviewEpisodes <= 0) account.TiktokProfilePreviewEpisodes = 3;
        if (account.TiktokFreePreviewEpisodes <= 0) account.TiktokFreePreviewEpisodes = 3;
        if (account.TiktokProjectConcurrency <= 0) account.TiktokProjectConcurrency = 4;
        account.ManagementDedupScope = NormalizeManagementDedupScope(account.ManagementDedupScope);
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

    private static string NormalizeUploadBrowserMode(string? value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return normalized is "embedded" or "external" or "playwright"
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
            "tiktok_username" or "software_user" => normalized,
            _ => "tiktok_username"
        };
    }

    private sealed class ActiveAccountPointer
    {
        public string ActiveAccountId { get; set; } = "";
    }
}
