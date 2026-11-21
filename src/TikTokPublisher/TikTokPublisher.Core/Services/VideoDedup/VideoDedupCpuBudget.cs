namespace TikTokPublisher.Core.Services.VideoDedup;

/// <summary>
/// 全机去重并发由系统设置决定。每路线程数是逻辑处理器数除以这个并发，不再额外空出处理器。
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

/// <summary>全机同时编码的名额。保存新的并发后，正在等待的下一集按新名额进入。</summary>
internal static class VideoDedupEncodeGate
{
    private static readonly object Sync = new();
    private static readonly Queue<TaskCompletionSource<int>> Waiters = new();
    private static readonly HashSet<int> Slots = [];
    private static int _limit = VideoDedupCpuBudget.DefaultConcurrency;
    private static bool _calibrating;

    public static bool TryBeginCalibration()
    {
        lock (Sync)
        {
            if (_calibrating || Slots.Count != 0 || Waiters.Count != 0)
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
            _limit = VideoDedupCpuBudget.Normalize(concurrency, Environment.ProcessorCount);
            Pump();
        }
    }

    public static async Task<int> WaitAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<int>? pending = null;
        lock (Sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryTakeSlot() is int slot)
                return slot;

            pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            Waiters.Enqueue(pending);
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
            foreach (var busy in Slots)
            {
                held = busy;
                break;
            }

            if (held is int busySlot)
                Slots.Remove(busySlot);
            Pump();
        }
    }

    private static int? TryTakeSlot()
    {
        if (_calibrating || Slots.Count >= _limit)
            return null;

        for (var i = 0; i < _limit; i++)
        {
            if (Slots.Add(i))
                return i;
        }

        return null;
    }

    private static void Pump()
    {
        while (Waiters.Count > 0 && TryTakeSlot() is int slot)
        {
            var next = Waiters.Dequeue();
            if (next.TrySetResult(slot))
                continue;
            Slots.Remove(slot);
        }
    }
}
