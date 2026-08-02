using TikTokPublisher.Core.Licensing;

namespace TikTokPublisher.Core.Services;

/// <summary>合并账号变更并在后台串行同步最新完整快照。</summary>
public sealed class TikTokAccountInventorySyncCoordinator : IDisposable
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
    ];

    private readonly AccountStore _store;
    private readonly TikTokManagementAccountSnapshotSyncService _syncService;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeSpan _debounceDelay;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly object _gate = new();

    private IReadOnlyList<TikTokClientAccountSnapshotItem> _latestSnapshot = [];
    private Task? _worker;
    private string? _lastSuccessfulFingerprint;
    private bool _pending;
    private bool _forcePending;
    private bool _started;
    private bool _disposed;

    public TikTokAccountInventorySyncCoordinator(AccountStore store)
        : this(
            store,
            new TikTokManagementAccountSnapshotSyncService(),
            static (delay, ct) => Task.Delay(delay, ct),
            TimeSpan.FromMilliseconds(400))
    {
    }

    internal TikTokAccountInventorySyncCoordinator(
        AccountStore store,
        TikTokManagementAccountSnapshotSyncService syncService,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        TimeSpan debounceDelay)
    {
        _store = store;
        _syncService = syncService;
        _delayAsync = delayAsync;
        _debounceDelay = debounceDelay;
        _lifetimeToken = _lifetimeCts.Token;
    }

    public event Action<string>? StatusChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started) return;
            _started = true;
        }

        _store.AccountsChanged += OnAccountsChanged;
        LicenseStore.StateChanged += OnLicenseStateChanged;
        QueueCurrentSnapshot(force: true);
    }

    public void RequestSync() => QueueCurrentSnapshot(force: true);

    private void OnAccountsChanged() => QueueCurrentSnapshot(force: false);

    private void OnLicenseStateChanged() => QueueCurrentSnapshot(force: true);

    private void QueueCurrentSnapshot(bool force)
    {
        if (!_store.CanSyncAccountSnapshot)
        {
            return;
        }

        var snapshot = TikTokManagementAccountSnapshotSyncService.BuildSnapshot(_store.Accounts);
        QueueSnapshot(snapshot, force);
    }

    private void QueueSnapshot(
        IReadOnlyList<TikTokClientAccountSnapshotItem> snapshot,
        bool force)
    {
        lock (_gate)
        {
            if (_disposed || !_started) return;
            _latestSnapshot = snapshot;
            _pending = true;
            _forcePending |= force;
            if (_worker is null || _worker.IsCompleted)
                _worker = Task.Run(ProcessPendingAsync);
        }
    }

    private async Task ProcessPendingAsync()
    {
        var ct = _lifetimeToken;
        var retryAttempt = 0;
        try
        {
            await _delayAsync(_debounceDelay, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                IReadOnlyList<TikTokClientAccountSnapshotItem> snapshot;
                bool force;
                lock (_gate)
                {
                    if (!_pending) return;
                    snapshot = _latestSnapshot;
                    force = _forcePending;
                    _pending = false;
                    _forcePending = false;
                }

                var fingerprint = TikTokManagementAccountSnapshotSyncService.BuildFingerprint(
                    _syncService.ResolveCurrentScopeKey(),
                    snapshot);
                lock (_gate)
                {
                    if (!force && string.Equals(
                            fingerprint,
                            _lastSuccessfulFingerprint,
                            StringComparison.Ordinal))
                    {
                        retryAttempt = 0;
                        continue;
                    }
                }

                var result = await _syncService.SyncAsync(snapshot, ct).ConfigureAwait(false);
                if (result.Ok)
                {
                    lock (_gate)
                        _lastSuccessfulFingerprint = fingerprint;
                    retryAttempt = 0;
                    RaiseStatus($"客户端 TikTok 账号已同步：{snapshot.Count} 个");
                    continue;
                }

                if (!result.ShouldRetry)
                    return;

                var retryDelay = RetryDelays[Math.Min(retryAttempt, RetryDelays.Length - 1)];
                retryAttempt++;
                await _delayAsync(retryDelay, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!_pending)
                        _pending = true;
                    _forcePending |= force;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常停止。
        }
        catch (Exception)
        {
            // Account snapshot sync is best-effort telemetry for the management console.
            // Failures must stay silent and never affect the desktop client workflow.
        }
        finally
        {
            lock (_gate)
            {
                _worker = null;
                if (_pending && !_disposed)
                    _worker = Task.Run(ProcessPendingAsync);
            }
        }
    }

    private void RaiseStatus(string message)
    {
        foreach (var handler in StatusChanged?.GetInvocationList().Cast<Action<string>>() ?? [])
        {
            try { handler(message); }
            catch { /* 状态输出不能终止同步。 */ }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _started = false;
            _pending = false;
        }

        _store.AccountsChanged -= OnAccountsChanged;
        LicenseStore.StateChanged -= OnLicenseStateChanged;
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }
}
