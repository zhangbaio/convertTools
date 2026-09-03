using PlatformPublisher.Analytics.Models;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public IReadOnlyList<AnalyticsAccount> ListAnalyticsAccounts() => _globalAccounts
        .SelectMany(account => new AnalyticsAccount?[]
        {
            string.IsNullOrWhiteSpace(account.KuaishouPersonalAccount) && string.IsNullOrWhiteSpace(account.KuaishouPersonalConfigPath)
                ? null
                : new AnalyticsAccount(account.Id, PlatformPublisher.Common.Models.PublishPlatform.KuaishouPersonalRevenue,
                    account.Name, Path.GetDirectoryName(account.KuaishouPersonalConfigPath) ?? string.Empty,
                    account.KuaishouPersonalConfigPath),
            string.IsNullOrWhiteSpace(account.KuaishouEnterpriseAccount) && string.IsNullOrWhiteSpace(account.KuaishouEnterpriseConfigPath)
                ? null
                : new AnalyticsAccount(account.Id, PlatformPublisher.Common.Models.PublishPlatform.KuaishouEnterpriseRevenue,
                    account.Name, Path.GetDirectoryName(account.KuaishouEnterpriseConfigPath) ?? string.Empty,
                    account.KuaishouEnterpriseConfigPath),
        })
        .OfType<AnalyticsAccount>()
        .ToArray();
}
