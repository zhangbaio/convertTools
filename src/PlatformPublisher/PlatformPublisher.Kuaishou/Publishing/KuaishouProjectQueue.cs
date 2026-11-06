namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 快手分账勾选队列：多个项目按配置同时执行。浏览器上传共用同一份登录态，由 <see cref="KuaishouUploadSlot"/> 保证同一时间只有一个项目在上传。
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

        using var gate = new SemaphoreSlim(normalized);
        var tasks = new Task[projectCount];
        for (var index = 0; index < projectCount; index++)
        {
            var projectIndex = index;
            tasks[index] = Task.Run(async () =>
            {
                try
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    await execute(projectIndex, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 停止时让已开始的项目各自收尾，不把单个取消扩散成整批失败。
                }
                finally
                {
                    gate.Release();
                }
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

public sealed class KuaishouUploadSlot
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Release(_gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
