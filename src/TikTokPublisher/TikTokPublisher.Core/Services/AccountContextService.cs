using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 账号上下文：切换时只改 active 指针并触发事件，避免 UI 线程全量 JSON 序列化。
/// </summary>
public sealed class AccountContextService
{
    private readonly AccountStore _store;

    public AccountContextService(AccountStore store) => _store = store;

    public TikTokAccountProfile? Active => _store.ActiveAccount;

    public event Action<TikTokAccountProfile?>? ActiveAccountChanged;

    public void SwitchTo(string accountId)
    {
        if (_store.ActiveAccountId == accountId) return;
        _store.SetActive(accountId);
        ActiveAccountChanged?.Invoke(_store.ActiveAccount);
    }

    public void SwitchTo(TikTokAccountProfile account) => SwitchTo(account.Id);

    public void NotifyProfileUpdated(TikTokAccountProfile account)
    {
        _store.Update(account);
        if (_store.ActiveAccountId == account.Id)
            ActiveAccountChanged?.Invoke(account);
    }
}
