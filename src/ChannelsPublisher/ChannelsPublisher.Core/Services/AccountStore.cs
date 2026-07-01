using System.Text.Json;
using ChannelsPublisher.Core.Models;

namespace ChannelsPublisher.Core.Services;

/// <summary>账号档案的读写与会话目录管理（JSON 持久化到 AppPaths.AccountsFile）。</summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly List<PublishAccount> _accounts = new();

    public IReadOnlyList<PublishAccount> Accounts => _accounts;

    public void Load()
    {
        _accounts.Clear();
        try
        {
            if (File.Exists(AppPaths.AccountsFile))
            {
                var list = JsonSerializer.Deserialize<List<PublishAccount>>(File.ReadAllText(AppPaths.AccountsFile));
                if (list != null) _accounts.AddRange(list);
            }
        }
        catch
        {
            // 损坏的档案不应让应用崩溃；从空列表开始。
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        File.WriteAllText(AppPaths.AccountsFile, JsonSerializer.Serialize(_accounts, JsonOptions));
    }

    public PublishAccount Add(string name)
    {
        var id = "acct-" + Guid.NewGuid().ToString("N")[..8];
        var account = new PublishAccount
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name.Trim(),
            ProfileDir = AppPaths.ProfileDirFor(id),
        };
        Directory.CreateDirectory(account.ProfileDir); // 预建每账号独立会话目录
        _accounts.Add(account);
        Save();
        return account;
    }

    public void Remove(PublishAccount account)
    {
        _accounts.Remove(account);
        Save();
        try
        {
            if (Directory.Exists(account.ProfileDir)) Directory.Delete(account.ProfileDir, recursive: true);
        }
        catch
        {
            // 会话目录可能被浏览器占用；删除失败不影响档案移除。
        }
    }

    public void Update(PublishAccount account)
    {
        Save();
    }
}
