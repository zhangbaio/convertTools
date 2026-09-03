using PlatformPublisher.Analytics.Models;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public IReadOnlyList<AnalyticsAccount> ListAnalyticsAccounts() => _accounts.Select(account =>
        new AnalyticsAccount(account.Id, account.Platform, account.Name,
            Path.GetDirectoryName(account.BaseConfigPath) ?? string.Empty, account.BaseConfigPath)).ToArray();
}
