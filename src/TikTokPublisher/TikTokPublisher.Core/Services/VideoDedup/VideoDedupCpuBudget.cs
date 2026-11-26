namespace TikTokPublisher.Core.Services.VideoDedup;

/// <summary>
/// 按共享的视频处理名额和 CPU 预算，为各路转码分配线程。
/// </summary>
internal static class VideoDedupCpuBudget
{
    public const int DefaultConcurrency = 2;
    public const int DefaultCpuPercent = 90;

    public readonly record struct EncodeThreads(int DecoderThreads, int EncoderThreads, int FilterThreads);

    private static int _concurrency = DefaultConcurrency;
    private static int _cpuPercent = DefaultCpuPercent;
    private static readonly AsyncLocal<int> Lane = new();

    internal static int? ConcurrencyForTests { get; set; }
    internal static int? CpuPercentForTests { get; set; }

    public static int CurrentConcurrency =>
        ConcurrencyForTests ?? Volatile.Read(ref _concurrency);

    public static int CurrentCpuPercent =>
        CpuPercentForTests ?? Volatile.Read(ref _cpuPercent);

    public static int CurrentLane => Math.Max(0, Lane.Value);

    public static void UseLane(int lane) => Lane.Value = Math.Max(0, lane);

    public static int Normalize(int configured, int logicalProcessors)
    {
        var logical = Math.Max(1, logicalProcessors);
        var value = configured <= 0 ? DefaultConcurrency : configured;
        return Math.Clamp(value, 1, logical);
    }

    public static void Configure(int configured, int logicalProcessors) =>
        Volatile.Write(ref _concurrency, Normalize(configured, logicalProcessors));

    public static int NormalizePercent(int percent)
    {
        if (percent <= 0)
            return DefaultCpuPercent;
        return Math.Clamp(percent, 50, 100);
    }

    public static void ConfigurePercent(int percent) =>
        Volatile.Write(ref _cpuPercent, NormalizePercent(percent));

    /// <summary>这台电脑按占用比例能分给去重编码的逻辑处理器数。100% 就是全部。</summary>
    public static int ProcessorBudget(int logicalProcessors, int percent)
    {
        var logical = Math.Max(1, logicalProcessors);
        var normalized = NormalizePercent(percent);
        if (normalized >= 100)
            return logical;
        var budget = (int)Math.Round(logical * (normalized / 100d), MidpointRounding.AwayFromZero);
        return Math.Clamp(budget, 1, logical);
    }

    /// <summary>把可用处理器分到每一路，余数从第 0 路开始逐路加 1。</summary>
    public static int ShareForLane(int logicalProcessors, int concurrency, int percent, int laneIndex)
    {
        var logical = Math.Max(1, logicalProcessors);
        var lanes = Normalize(concurrency, logical);
        var budget = ProcessorBudget(logical, percent);
        var index = Math.Clamp(laneIndex, 0, lanes - 1);
        var share = budget / lanes + (index < budget % lanes ? 1 : 0);
        return Math.Max(1, share);
    }

    public static EncodeThreads ThreadsFor(int logicalProcessors, bool hardwareEncoder) =>
        ThreadsFor(logicalProcessors, CurrentConcurrency, CurrentCpuPercent, CurrentLane, hardwareEncoder);

    public static EncodeThreads ThreadsFor(int logicalProcessors, int concurrency, bool hardwareEncoder) =>
        ThreadsFor(logicalProcessors, concurrency, percent: 100, laneIndex: 0, hardwareEncoder);

    public static EncodeThreads ThreadsFor(
        int logicalProcessors,
        int concurrency,
        int percent,
        int laneIndex,
        bool hardwareEncoder)
    {
        var share = ShareForLane(logicalProcessors, concurrency, percent, laneIndex);
        if (hardwareEncoder)
        {
            var decoder = Math.Clamp(share / 3, 1, 4);
            return new EncodeThreads(decoder, 1, Math.Max(1, share - decoder));
        }

        var softwareDecoder = Math.Clamp(share / 4, 1, 4);
        var remaining = share - softwareDecoder;
        var filter = Math.Max(1, (remaining + 1) / 2);
        return new EncodeThreads(softwareDecoder, Math.Max(1, remaining - filter), filter);
    }
}

/// <summary>同一进程内共享的编码名额。单路时优先完成当前剧，多路时按剧轮转。</summary>
internal static class VideoDedupEncodeGate
{
    private static readonly object Sync = new();
    private static readonly LinkedList<Series> ReadySeries = new();
    private static readonly Dictionary<Series, Queue<TaskCompletionSource<int>>> Waiters = new();
    private static readonly Dictionary<int, Series> Slots = [];
    private static int _limit = VideoDedupCpuBudget.DefaultConcurrency;
    private static bool _calibrating;
    private static Series? _lastGrantedSeries;
    private static Series? _preferredSeries;

    public static Series BeginSeries() => new();

    public static bool TryBeginCalibration()
    {
        lock (Sync)
        {
            if (_calibrating || Slots.Count != 0 || Waiters.Count != 0 || _preferredSeries is not null)
                return false;
            _calibrating = true;
            return true;
        }
    }

    public static void EndCalibration()
    {
        lock (Sync)
        {
            _calibrating = false;
            Pump();
        }
    }

    public static void Configure(int concurrency)
    {
        lock (Sync)
        {
            var nextLimit = VideoDedupCpuBudget.Normalize(concurrency, Environment.ProcessorCount);
            if (nextLimit == 1 && _limit > 1)
            {
                // Earlier admissions may finish after a decrease. Keep one of those
                // series ahead of the queue once the active slots have drained.
                _preferredSeries = Slots.OrderBy(slot => slot.Key).Select(slot => slot.Value)
                    .FirstOrDefault(series => !series.Completed) ?? _lastGrantedSeries;
            }
            else if (nextLimit > 1)
            {
                _preferredSeries = null;
            }
            _limit = nextLimit;
            Pump();
        }
    }

    public static async Task<int> WaitAsync(Series series, CancellationToken cancellationToken)
    {
        TaskCompletionSource<int> pending;
        lock (Sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(series.Completed, series);
            pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Waiters.TryGetValue(series, out var seriesWaiters))
            {
                seriesWaiters = new Queue<TaskCompletionSource<int>>();
                Waiters.Add(series, seriesWaiters);
                ReadySeries.AddLast(series);
            }
            seriesWaiters.Enqueue(pending);
            Pump();
        }

        using var registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        try
        {
            return await pending.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (Sync)
                Pump();
            throw;
        }
    }

    public static void Release(int slot)
    {
        lock (Sync)
        {
            Slots.Remove(slot);
            Pump();
        }
    }

    public static void Release()
    {
        lock (Sync)
        {
            int? held = null;
            foreach (var busy in Slots.Keys)
            {
                held = busy;
                break;
            }

            if (held is int busySlot)
                Slots.Remove(busySlot);
            Pump();
        }
    }

    private static int TakeSlot(Series series)
    {
        // Pump calls this only when there is capacity below the current limit.
        for (var i = 0; i < _limit; i++)
        {
            if (Slots.TryAdd(i, series))
                return i;
        }
        throw new InvalidOperationException("没有可用的共享转码名额。");
    }

    private static void Pump()
    {
        while (!_calibrating && Slots.Count < _limit && ReadySeries.First is { } first)
        {
            var selected = first;
            if (_limit == 1 && _preferredSeries is { } preferred)
            {
                selected = ReadySeries.Find(preferred);
                if (selected is null)
                    return;
            }
            else if (_limit > 1 && first.Value == _lastGrantedSeries && first.Next is { } nextSeries)
            {
                ReadySeries.Remove(first);
                ReadySeries.AddLast(first);
                selected = nextSeries;
            }

            var series = selected.Value;
            ReadySeries.Remove(selected);
            var seriesWaiters = Waiters[series];
            var next = seriesWaiters.Dequeue();
            if (seriesWaiters.Count == 0)
                Waiters.Remove(series);
            else
                ReadySeries.AddLast(selected);

            var slot = TakeSlot(series);
            if (next.TrySetResult(slot))
            {
                _lastGrantedSeries = series;
                if (_limit == 1)
                    _preferredSeries = series;
                continue;
            }
            Slots.Remove(slot);
        }
    }

    public sealed class Series : IDisposable
    {
        internal bool Completed { get; private set; }

        public void Dispose()
        {
            lock (Sync)
            {
                if (Completed)
                    return;
                Completed = true;
                if (Waiters.Remove(this, out var pending))
                {
                    ReadySeries.Remove(this);
                    foreach (var waiter in pending)
                        waiter.TrySetCanceled();
                }
                if (_preferredSeries == this)
                    _preferredSeries = null;
                if (_lastGrantedSeries == this)
                    _lastGrantedSeries = null;
                Pump();
            }
        }
    }
}
