using Microsoft.Extensions.Logging;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouOnlineQueueWorker : IDisposable
{
    private readonly PublishAccountStore _accountStore;
    private readonly KuaishouOnlineQueueProcessor _processor;
    private readonly ILogger<KuaishouOnlineQueueWorker> _logger;
    private readonly IKuaishouAccountEditionCatalog? _editions;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public KuaishouOnlineQueueWorker(
        PublishAccountStore accountStore,
        KuaishouOnlineQueueProcessor processor,
        ILogger<KuaishouOnlineQueueWorker> logger,
        IKuaishouAccountEditionCatalog? editions = null)
    {
        _accountStore = accountStore;
        _processor = processor;
        _logger = logger;
        _editions = editions;
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await ProcessAllAccountsAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogError(ex, "Kuaishou automatic online queue cycle failed"); }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task ProcessAllAccountsAsync(CancellationToken cancellationToken)
    {
        var accounts = await _accountStore.LoadAsync(cancellationToken);
        var accountIds = accounts.Select(account => account.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_editions is not null)
        {
            foreach (var accountId in _editions.AccountIds)
                accountIds.Add(accountId);
        }

        foreach (var accountId in accountIds)
        foreach (var platform in PlatformsFor(accountId))
        {
            try { await _processor.ProcessDueAsync(accountId, platform, null, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kuaishou online queue failed for account {AccountId}, platform {Platform}",
                    accountId, platform);
            }
        }
    }

    private IEnumerable<PublishPlatform> PlatformsFor(string accountId)
    {
        var both = new[]
        {
            PublishPlatform.KuaishouPersonalRevenue,
            PublishPlatform.KuaishouEnterpriseRevenue,
        };
        if (_editions is null || !_editions.TryGetEdition(accountId, out var platform))
            return both;
        return platform is null ? [] : [platform.Value];
    }
}
