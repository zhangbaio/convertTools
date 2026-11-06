using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouProjectQueueTests
{
    [Theory]
    [InlineData(0, 4)]
    [InlineData(-2, 4)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(9, 5)]
    public void NormalizeParallelism_ClampsToReferenceRange(int configured, int expected) =>
        Assert.Equal(expected, KuaishouProjectQueue.NormalizeParallelism(configured));

    [Fact]
    public async Task RunAsync_OverlapsProjectsUpToParallelism()
    {
        var active = 0;
        var max = 0;
        var overlap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = KuaishouProjectQueue.RunAsync(2, 2, async (_, _) =>
        {
            var now = Interlocked.Increment(ref active);
            UpdateMax(ref max, now);
            if (Volatile.Read(ref active) >= 2)
                overlap.TrySetResult();
            try
            {
                await overlap.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await release.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }, CancellationToken.None);

        await overlap.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, Volatile.Read(ref max));
        release.TrySetResult();
        await run;
    }

    [Fact]
    public async Task RunAsync_ParallelismOneRunsProjectsOneAfterAnother()
    {
        var active = 0;
        var max = 0;
        await KuaishouProjectQueue.RunAsync(3, 1, async (_, _) =>
        {
            var now = Interlocked.Increment(ref active);
            UpdateMax(ref max, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref active);
        }, CancellationToken.None);

        Assert.Equal(1, max);
    }

    [Fact]
    public async Task RunAsync_CancellationDoesNotStartProjectsStillWaiting()
    {
        using var cts = new CancellationTokenSource();
        var started = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            KuaishouProjectQueue.RunAsync(3, 1, async (index, token) =>
            {
                Interlocked.Increment(ref started);
                if (index == 0)
                {
                    cts.Cancel();
                    await Task.Delay(Timeout.Infinite, token);
                }
            }, cts.Token));

        Assert.Equal(1, started);
    }

    [Fact]
    public async Task UploadSlot_AllowsOnlyOneUploadAtATime()
    {
        var slot = new KuaishouUploadSlot();
        var active = 0;
        var max = 0;
        var firstHolding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = 0;

        var first = Task.Run(async () =>
        {
            using var lease = await slot.AcquireAsync(CancellationToken.None);
            var now = Interlocked.Increment(ref active);
            UpdateMax(ref max, now);
            firstHolding.TrySetResult();
            await releaseFirst.Task;
            Interlocked.Decrement(ref active);
        });

        await firstHolding.Task;
        var second = Task.Run(async () =>
        {
            using var lease = await slot.AcquireAsync(CancellationToken.None);
            Interlocked.Exchange(ref secondEntered, 1);
            var now = Interlocked.Increment(ref active);
            UpdateMax(ref max, now);
            Interlocked.Decrement(ref active);
        });

        await Task.Delay(80);
        Assert.Equal(0, Volatile.Read(ref secondEntered));
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, max);
        Assert.Equal(1, secondEntered);
    }

    private static void UpdateMax(ref int target, int value)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref target);
            if (value <= observed)
                return;
        }
        while (Interlocked.CompareExchange(ref target, value, observed) != observed);
    }
}
