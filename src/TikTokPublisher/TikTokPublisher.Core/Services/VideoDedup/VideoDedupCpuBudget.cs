namespace TikTokPublisher.Core.Services.VideoDedup;

/// <summary>
/// 全机去重并发由系统设置决定。每路线程数是逻辑处理器数除以这个并发，不再额外空出处理器。
/// </summary>
internal static class VideoDedupCpuBudget
{
    public const int DefaultConcurrency = 2;

    public readonly record struct EncodeThreads(int EncoderThreads, int FilterThreads);

    private static int _concurrency = DefaultConcurrency;

    internal static int? ConcurrencyForTests { get; set; }

    public static int CurrentConcurrency =>
        ConcurrencyForTests ?? Volatile.Read(ref _concurrency);

    public static int Normalize(int configured, int logicalProcessors)
    {
        var logical = Math.Max(1, logicalProcessors);
        var value = configured <= 0 ? DefaultConcurrency : configured;
        return Math.Clamp(value, 1, logical);
    }

    public static void Configure(int configured, int logicalProcessors) =>
        Volatile.Write(ref _concurrency, Normalize(configured, logicalProcessors));

    public static EncodeThreads ThreadsFor(int logicalProcessors, bool hardwareEncoder) =>
        ThreadsFor(logicalProcessors, CurrentConcurrency, hardwareEncoder);

    public static EncodeThreads ThreadsFor(int logicalProcessors, int concurrency, bool hardwareEncoder)
    {
        var logical = Math.Max(1, logicalProcessors);
        var lanes = Normalize(concurrency, logical);
        var perProcess = Math.Max(1, logical / lanes);
        if (hardwareEncoder)
            return new EncodeThreads(1, perProcess);

        var filter = Math.Max(1, (perProcess + 1) / 2);
        return new EncodeThreads(Math.Max(1, perProcess - filter), filter);
    }
}

/// <summary>全机同时编码的名额。保存新的并发后，正在等待的下一集按新名额进入。</summary>
internal static class VideoDedupEncodeGate
{
    private static readonly object Sync = new();
    private static readonly Queue<TaskCompletionSource<bool>> Waiters = new();
    private static int _limit = VideoDedupCpuBudget.DefaultConcurrency;
    private static int _inUse;

    public static void Configure(int concurrency)
    {
        lock (Sync)
        {
            _limit = VideoDedupCpuBudget.Normalize(concurrency, Environment.ProcessorCount);
            Pump();
        }
    }

    public static async Task WaitAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool>? pending = null;
        lock (Sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_inUse < _limit)
            {
                _inUse++;
                return;
            }

            pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Waiters.Enqueue(pending);
        }

        using var registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        try
        {
            await pending.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (Sync)
                Pump();
            throw;
        }
    }

    public static void Release()
    {
        lock (Sync)
        {
            if (_inUse > 0)
                _inUse--;
            Pump();
        }
    }

    private static void Pump()
    {
        while (Waiters.Count > 0 && _inUse < _limit)
        {
            var next = Waiters.Dequeue();
            if (!next.TrySetResult(true))
                continue;
            _inUse++;
        }
    }
}
