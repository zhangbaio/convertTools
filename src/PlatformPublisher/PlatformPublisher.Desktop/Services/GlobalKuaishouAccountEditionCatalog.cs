using ChannelsPublisher.Core.Services;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Kuaishou.Publishing;

namespace PlatformPublisher.Desktop.Services;

public sealed class GlobalKuaishouAccountEditionCatalog : IKuaishouAccountEditionCatalog
{
    private readonly AccountStore _store;

    public GlobalKuaishouAccountEditionCatalog(AccountStore store) => _store = store;

    public IReadOnlyList<string> AccountIds =>
        _store.Accounts.Select(account => account.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public bool TryGetEdition(string accountId, out PublishPlatform? platform)
    {
        var account = _store.Accounts.FirstOrDefault(item =>
            string.Equals(item.Id, accountId, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            platform = null;
            return false;
        }

        if (!KuaishouAccountEdition.TryGetPlatform(account.KuaishouAccountEdition, out var resolved))
        {
            platform = null;
            return true;
        }

        platform = resolved;
        return true;
    }
}
