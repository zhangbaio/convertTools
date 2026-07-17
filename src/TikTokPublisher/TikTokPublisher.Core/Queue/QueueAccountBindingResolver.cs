using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

/// <summary>
/// 解析并修复队列项目的账号绑定。工作目录账号只作为无法识别旧绑定时的默认值，
/// 不覆盖仍能解析的逐项目账号绑定。
/// </summary>
public static class QueueAccountBindingResolver
{
    public static TikTokAccountProfile? Resolve(AccountStore store, QueueProjectItem item)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(item);

        var hasExplicitBinding =
            !string.IsNullOrWhiteSpace(item.AccountProfileId) ||
            !string.IsNullOrWhiteSpace(item.AccountProfileName);
        var resolved = ResolveExplicit(store, item);
        if (resolved is not null || hasExplicitBinding)
            return resolved;

        var activeId = store.ActiveAccountId;
        return store.Accounts.FirstOrDefault(account => account.Id == activeId)
               ?? store.Accounts.FirstOrDefault();
    }

    public static bool RepairForWorkspaceDefault(
        AccountStore store,
        QueueProjectItem item,
        TikTokAccountProfile workspaceAccount)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(workspaceAccount);

        // 已有且可识别的逐项目绑定优先；账号已删除、重新创建或名称也已变化时，
        // 才继承用户当前明确选择的工作目录账号。
        var account = ResolveExplicit(store, item) ?? workspaceAccount;
        if (string.Equals((item.AccountProfileId ?? "").Trim(), account.Id, StringComparison.Ordinal) &&
            string.Equals((item.AccountProfileName ?? "").Trim(), account.DisplayName, StringComparison.Ordinal))
        {
            return false;
        }

        item.AccountProfileId = account.Id;
        item.AccountProfileName = account.DisplayName;
        return true;
    }

    private static TikTokAccountProfile? ResolveExplicit(AccountStore store, QueueProjectItem item)
    {
        var savedId = (item.AccountProfileId ?? "").Trim();
        var savedName = (item.AccountProfileName ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(savedId))
        {
            var bound = store.Accounts.FirstOrDefault(account =>
                string.Equals(account.Id, savedId, StringComparison.Ordinal));
            if (bound is not null)
            {
                // 旧版本在账号文件为空时固定创建 ID=default。安装时重置账号后，
                // 新 default 会复用旧队列的 ID；仅在保存名称唯一命中其它账号时纠正。
                if (string.Equals(savedId, "default", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(savedName) &&
                    !MatchesSavedAccountName(bound, savedName))
                {
                    var namedMatches = FindNamedMatches(store, savedName);
                    if (namedMatches.Length == 1)
                        return namedMatches[0];

                    // 固定 default ID 可能已被一个全新账号复用。保存名称既不匹配该账号、
                    // 也无法唯一命中其它账号时，宁可交给有明确工作目录上下文的调用方修复，
                    // 也不能静默使用这个新建的空账号。
                    return null;
                }

                return bound;
            }

            return ResolveUniqueName(store, savedName);
        }

        return ResolveUniqueName(store, savedName);
    }

    private static TikTokAccountProfile? ResolveUniqueName(AccountStore store, string savedName)
    {
        if (string.IsNullOrWhiteSpace(savedName))
            return null;

        var matches = FindNamedMatches(store, savedName);
        return matches.Length == 1 ? matches[0] : null;
    }

    private static TikTokAccountProfile[] FindNamedMatches(AccountStore store, string savedName) =>
        store.Accounts
            .Where(account => MatchesSavedAccountName(account, savedName))
            .Take(2)
            .ToArray();

    private static bool MatchesSavedAccountName(TikTokAccountProfile account, string savedName) =>
        string.Equals(account.Id, savedName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(account.Name, savedName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(account.DisplayName, savedName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(account.TiktokAccountNickname, savedName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(account.TiktokLoginEmail, savedName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(account.TiktokLastLoginEmail, savedName, StringComparison.OrdinalIgnoreCase);
}
