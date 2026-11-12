namespace TikTokPublisher.Core.Services.VideoDedup;

/// <summary>
/// 按逻辑处理器数量限制去重编码，使多部剧、多集同时处理时总线程数仍留有余量。
/// </summary>
internal static class VideoDedupCpuBudget
{
    public readonly record struct EncodeThreads(int EncoderThreads, int FilterThreads);

    public static int MaxConcurrentEncodes(int logicalProcessors)
    {
        var logical = Math.Max(1, logicalProcessors);
        if (logical <= 8)
            return 1;
        if (logical <= 24)
            return 2;
        return 3;
    }

    public static int ThreadBudget(int logicalProcessors)
    {
        var logical = Math.Max(1, logicalProcessors);
        var reserved = logical <= 2 ? 1 : Math.Max(1, logical / 3);
        return Math.Max(1, logical - reserved);
    }

    public static EncodeThreads ThreadsFor(int logicalProcessors, bool hardwareEncoder)
    {
        var perProcess = Math.Max(
            1,
            ThreadBudget(logicalProcessors) / MaxConcurrentEncodes(logicalProcessors));
        if (hardwareEncoder)
            return new EncodeThreads(1, Math.Max(1, perProcess - 1));

        var filter = Math.Max(1, (perProcess + 1) / 2);
        return new EncodeThreads(Math.Max(1, perProcess - filter), filter);
    }
}

internal static class VideoDedupEncodeGate
{
    private static readonly SemaphoreSlim Gate = new(Capacity, Capacity);

    private static int Capacity => VideoDedupCpuBudget.MaxConcurrentEncodes(Environment.ProcessorCount);

    public static Task WaitAsync(CancellationToken cancellationToken) => Gate.WaitAsync(cancellationToken);

    public static void Release() => Gate.Release();
}
