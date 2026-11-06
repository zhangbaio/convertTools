using PlatformPublisher.Analytics.Models;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Kuaishou.Publishing;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public IReadOnlyList<AnalyticsAccount> ListAnalyticsAccounts() => _globalAccounts
        .Select(account =>
        {
            if (!KuaishouAccountEdition.TryGetPlatform(account.KuaishouAccountEdition, out var platform))
                return null;
            var configPath = platform == PublishPlatform.KuaishouEnterpriseRevenue
                ? FirstConfigured(account.KuaishouEnterpriseConfigPath, account.KuaishouPersonalConfigPath)
                : FirstConfigured(account.KuaishouPersonalConfigPath, account.KuaishouEnterpriseConfigPath);
            return new AnalyticsAccount(
                account.Id,
                platform,
                account.Name,
                Path.GetDirectoryName(configPath) ?? string.Empty,
                configPath);
        })
        .OfType<AnalyticsAccount>()
        .ToArray();

    private static string FirstConfigured(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback ?? string.Empty : preferred;
}
