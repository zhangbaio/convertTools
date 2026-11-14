namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 快手分账勾选队列：按剧集列表从上往下领取下一部，同时执行的数量不超过配置。
/// 上传名额由本机闸门按账号分配，同一个账号的剧可以同时上传。
/// </summary>
public static class KuaishouProjectQueue
{
    public const int DefaultParallelism = 4;
    public const int MaxParallelism = 5;

    public static int NormalizeParallelism(int configured) =>
        Math.Clamp(configured <= 0 ? DefaultParallelism : configured, 1, MaxParallelism);

    public static async Task RunAsync(
        int projectCount,
        int parallelism,
        Func<int, CancellationToken, Task> execute,
        CancellationToken cancellationToken)
    {
        if (projectCount <= 0)
            return;

        var normalized = NormalizeParallelism(parallelism);
        if (normalized <= 1 || projectCount <= 1)
        {
            for (var index = 0; index < projectCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await execute(index, cancellationToken);
            }

            return;
        }

        var next = -1;
        var workers = new Task[Math.Min(normalized, projectCount)];
        for (var worker = 0; worker < workers.Length; worker++)
        {
            workers[worker] = Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var index = Interlocked.Increment(ref next);
                    if (index >= projectCount)
                        return;
                    try
                    {
                        await execute(index, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            });
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }
}

public sealed class KuaishouUploadSlot
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _holderLock = new();
    private string _holder = "";

    public async Task<IDisposable> AcquireAsync(
        CancellationToken cancellationToken,
        string? holder = null,
        Func<string, Task>? onWait = null)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            if (onWait is not null)
            {
                string current;
                lock (_holderLock)
                    current = _holder;
                await onWait(current).ConfigureAwait(false);
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_holderLock)
            _holder = holder?.Trim() ?? "";
        return new Release(this);
    }

    private void ReleaseHolder()
    {
        lock (_holderLock)
            _holder = "";
        _gate.Release();
    }

    private sealed class Release : IDisposable
    {
        private KuaishouUploadSlot? _slot;

        public Release(KuaishouUploadSlot slot) => _slot = slot;

        public void Dispose() => Interlocked.Exchange(ref _slot, null)?.ReleaseHolder();
    }
}
