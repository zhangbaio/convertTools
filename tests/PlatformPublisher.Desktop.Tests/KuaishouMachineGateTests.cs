using PlatformPublisher.Desktop.Services;
using TikTokPublisher.Core.Queue;
using Xunit;

namespace PlatformPublisher.Desktop.Tests;

public class KuaishouMachineGateTests
{
    [Fact]
    public async Task ProjectSlotWaitsForAnEarlierAccount()
    {
        var gate = new KuaishouMachineGate(() => (1, QueueProjectSlotCoordinator.AccountOrderMode, 2, 3));
        using var first = await gate.AcquireProjectAsync("a", 0, CancellationToken.None);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = gate.AcquireProjectAsync("b", 1, CancellationToken.None, () => secondStarted.TrySetResult());

        var announced = await Task.WhenAny(secondStarted.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(secondStarted.Task, announced);
        Assert.False(second.IsCompleted);

        first.Dispose();
        using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task UploadSlotLimitsTheMachine()
    {
        var gate = new KuaishouMachineGate(() => (4, QueueProjectSlotCoordinator.AccountOrderMode, 1, 3));
        using var first = await gate.AcquireUploadAsync(CancellationToken.None);
        var second = gate.AcquireUploadAsync(CancellationToken.None);
        var pending = await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(700)));
        Assert.False(second.IsCompleted);
        Assert.NotSame(second, pending);

        first.Dispose();
        using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task UploadSlotNamesTheCurrentHolder()
    {
        var gate = new KuaishouMachineGate(() => (4, QueueProjectSlotCoordinator.AccountOrderMode, 1, 3));
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = await gate.AcquireUploadAsync(CancellationToken.None, "快手分账个人 · 记忆清零");
        var second = gate.AcquireUploadAsync(
            CancellationToken.None,
            "薄荷剧场 · 乡厨百味",
            holder =>
            {
                seen.TrySetResult(holder);
                return Task.CompletedTask;
            });

        Assert.Equal("快手分账个人 · 记忆清零", await seen.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(second.IsCompleted);

        first.Dispose();
        using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task UploadSlotNamesEveryCurrentHolder()
    {
        var gate = new KuaishouMachineGate(() => (4, QueueProjectSlotCoordinator.AccountOrderMode, 2, 3));
        using var first = await gate.AcquireUploadAsync(CancellationToken.None, "快手分账个人 · 记忆清零");
        using var secondHold = await gate.AcquireUploadAsync(CancellationToken.None, "格佳信息 · 神眼憨婿");
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = gate.AcquireUploadAsync(
            CancellationToken.None,
            "薄荷剧场 · 乡厨百味",
            holder =>
            {
                seen.TrySetResult(holder);
                return Task.CompletedTask;
            });

        Assert.Equal("快手分账个人 · 记忆清零、格佳信息 · 神眼憨婿", await seen.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(third.IsCompleted);

        first.Dispose();
        using var acquired = await third.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ProjectImageSlotLimitsTheMachine()
    {
        var gate = new KuaishouMachineGate(() => (4, QueueProjectSlotCoordinator.AccountOrderMode, 2, 1));
        using var first = await gate.AcquireProjectImageAsync(CancellationToken.None);
        var second = gate.AcquireProjectImageAsync(CancellationToken.None);
        var pending = await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(700)));
        Assert.False(second.IsCompleted);
        Assert.NotSame(second, pending);

        first.Dispose();
        using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ProjectImageUnsetLimitDefaultsToThree()
    {
        var gate = new KuaishouMachineGate(() => (4, QueueProjectSlotCoordinator.AccountOrderMode, 2, 0));
        var leases = new List<IDisposable>();
        for (var i = 0; i < 3; i++)
            leases.Add(await gate.AcquireProjectImageAsync(CancellationToken.None));

        var fourth = gate.AcquireProjectImageAsync(CancellationToken.None);
        var pending = await Task.WhenAny(fourth, Task.Delay(TimeSpan.FromMilliseconds(700)));
        Assert.False(fourth.IsCompleted);
        Assert.NotSame(fourth, pending);

        foreach (var lease in leases)
            lease.Dispose();
        using var acquired = await fourth.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
