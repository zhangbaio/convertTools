namespace TikTokPublisher.Core.Queue;

/// <summary>Limits video download-heavy queue steps across all account workspaces in this app process.</summary>
public static class QueueDownloadSlotCoordinator
{
    private const int DefaultCapacity = 1;
    private const int MaxCapacity = 4;

    private static readonly object Sync = new();
    private static SemaphoreSlim Gate = new(DefaultCapacity, DefaultCapacity);
    private static int Capacity = DefaultCapacity;
    private static int Active;
    private static int Waiting;

    public static async Task<IDisposable> WaitAsync(
        int maxParallelProjects,
        string displayName,
        Action<string>? log,
        CancellationToken ct)
    {
        var normalized = Math.Clamp(maxParallelProjects <= 0 ? DefaultCapacity : maxParallelProjects, 1, MaxCapacity);
        SemaphoreSlim gate;
        int active;
        int capacity;
        lock (Sync)
        {
            if (normalized != Capacity && Active == 0 && Waiting == 0)
            {
                Gate.Dispose();
                Gate = new SemaphoreSlim(normalized, normalized);
                Capacity = normalized;
            }

            gate = Gate;
            active = Active;
            capacity = Capacity;
        }

        if (!gate.Wait(0))
        {
            log?.Invoke($"全局下载槽位已满，等待下载槽位（运行中 {active}/{capacity}）：{displayName}");
            lock (Sync)
                Waiting++;
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                lock (Sync)
                    Waiting = Math.Max(0, Waiting - 1);
            }
        }

        lock (Sync)
        {
            Active++;
            active = Active;
            capacity = Capacity;
        }

        log?.Invoke($"已获得全局下载槽位（运行中 {active}/{capacity}）：{displayName}");
        return new Lease(gate, displayName, log);
    }

    private sealed class Lease : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private readonly string _displayName;
        private readonly Action<string>? _log;
        private int _released;

        public Lease(SemaphoreSlim gate, string displayName, Action<string>? log)
        {
            _gate = gate;
            _displayName = displayName;
            _log = log;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            int active;
            int capacity;
            lock (Sync)
            {
                Active = Math.Max(0, Active - 1);
                active = Active;
                capacity = Capacity;
            }

            _gate.Release();
            _log?.Invoke($"已释放全局下载槽位（运行中 {active}/{capacity}）：{_displayName}");
        }
    }
}
