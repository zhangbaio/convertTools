using PlatformPublisher.Analytics.Models;
using PlatformPublisher.Kuaishou.Publishing;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public IReadOnlyList<AnalyticsAccount> ListAnalyticsAccounts() => _globalAccounts
        .Select(account =>
        {
            if (!KuaishouAccountEdition.TryGetPlatform(account.KuaishouAccountEdition, out var platform))
                return null;
            var configPath = string.IsNullOrWhiteSpace(account.KuaishouPersonalConfigPath)
                ? account.KuaishouEnterpriseConfigPath
                : account.KuaishouPersonalConfigPath;
            return new AnalyticsAccount(
                account.Id,
                platform,
                account.Name,
                Path.GetDirectoryName(configPath) ?? string.Empty,
                configPath);
        })
        .OfType<AnalyticsAccount>()
        .ToArray();
}
