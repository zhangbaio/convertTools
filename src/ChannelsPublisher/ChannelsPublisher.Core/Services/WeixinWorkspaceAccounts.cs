using ChannelsPublisher.Core.Models;

namespace ChannelsPublisher.Core.Services;

public static class WeixinWorkspaceAccounts
{
    public static bool IsVisible(PublishAccount account, bool hasWeixinJobs = false) =>
        account.IsWeixinWorkspaceAccount || account.WeixinLastLoginAt is not null ||
        HasSavedSession(account) || (!HasKuaishouBinding(account) && (hasWeixinJobs || account.LastLoginAt is not null));

    public static bool HasSavedSession(PublishAccount account) =>
        File.Exists(account.WeixinAuthStatePath) ||
        (!string.IsNullOrWhiteSpace(account.ProfileDir) &&
         (File.Exists(Path.Combine(account.ProfileDir, "weixin-series-auth.json")) ||
          File.Exists(Path.Combine(account.ProfileDir, "weixin-auth.json"))));

    public static string Nickname(PublishAccount account) =>
        account.WeixinNickname ?? (HasKuaishouBinding(account) ? "" : account.Nickname);

    public static string AccountName(PublishAccount account) =>
        !string.IsNullOrWhiteSpace(account.WeixinAccountName) ? account.WeixinAccountName :
        HasKuaishouBinding(account) ? $"视频号 {account.Id}" : account.Name;

    public static string DisplayName(PublishAccount account)
    {
        var nickname = Nickname(account);
        return string.IsNullOrWhiteSpace(nickname) ? AccountName(account) : nickname;
    }

    public static DateTimeOffset? LastLogin(PublishAccount account) =>
        account.WeixinLastLoginAt ?? (HasKuaishouBinding(account) ? null : account.LastLoginAt);

    public static string WorkDirectory(PublishAccount account, IEnumerable<PublishAccount> accounts)
    {
        if (account.WeixinWorkRootDirectory is { } dedicated) return dedicated;
        var legacy = Normalize(account.WorkRootDirectory);
        if (legacy.Length == 0) return "";
        var usedByKuaishou = accounts.Where(HasKuaishouBinding).SelectMany(KuaishouDirectories)
            .Any(directory => string.Equals(directory, legacy, StringComparison.OrdinalIgnoreCase));
        return usedByKuaishou ? "" : account.WorkRootDirectory;
    }

    public static bool HasKuaishouBinding(PublishAccount account) =>
        new[] { account.KuaishouAccountEdition, account.KuaishouPersonalAccount, account.KuaishouEnterpriseAccount,
            account.KuaishouPersonalConfigPath, account.KuaishouEnterpriseConfigPath, account.KuaishouPersonalAuthStatePath,
            account.KuaishouEnterpriseAuthStatePath, account.KuaishouPersonalWorkRootDirectory,
            account.KuaishouEnterpriseWorkRootDirectory }.Any(value => !string.IsNullOrWhiteSpace(value));

    private static IEnumerable<string> KuaishouDirectories(PublishAccount account)
    {
        var personal = Normalize(account.KuaishouPersonalWorkRootDirectory);
        var enterprise = Normalize(account.KuaishouEnterpriseWorkRootDirectory);
        if (personal.Length > 0) yield return personal;
        if (enterprise.Length > 0) yield return enterprise;
        if ((personal.Length == 0 && enterprise.Length == 0) ||
            (personal.Length == 0 && string.Equals(account.KuaishouAccountEdition, "personal", StringComparison.OrdinalIgnoreCase)) ||
            (enterprise.Length == 0 && string.Equals(account.KuaishouAccountEdition, "enterprise", StringComparison.OrdinalIgnoreCase)))
            yield return Normalize(account.WorkRootDirectory);
    }

    private static string Normalize(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "";
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim())); }
        catch (ArgumentException) { return ""; }
        catch (NotSupportedException) { return ""; }
    }
}
