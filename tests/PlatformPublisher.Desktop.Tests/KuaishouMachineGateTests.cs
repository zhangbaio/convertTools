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
    public async Task UploadSlotPrefersTheEarlierRowInTheList()
    {
        var gate = new KuaishouMachineGate(() => (4, QueueProjectSlotCoordinator.AccountOrderMode, 1, 3));
        using var held = await gate.AcquireUploadAsync(CancellationToken.None, "占用");
        var laterWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var earlierWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var later = gate.AcquireUploadAsync(
            CancellationToken.None,
            "后面的剧",
            _ =>
            {
                laterWaiting.TrySetResult();
                return Task.CompletedTask;
            },
            "acct",
            listOrder: 5);
        var earlier = gate.AcquireUploadAsync(
            CancellationToken.None,
            "前面的剧",
            _ =>
            {
                earlierWaiting.TrySetResult();
                return Task.CompletedTask;
            },
            "acct",
            listOrder: 1);

        await Task.WhenAll(
            laterWaiting.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            earlierWaiting.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        held.Dispose();

        var winner = await Task.WhenAny(earlier, later).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Same(earlier, winner);
        using var acquired = await earlier;
        Assert.False(later.IsCompleted);
        acquired.Dispose();
        using var second = await later.WaitAsync(TimeSpan.FromSeconds(3));
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
    public async Task SameAccountCanUploadTwoDramas()
    {
        var gate = new KuaishouMachineGate(() => (4, QueueProjectSlotCoordinator.AccountOrderMode, 2, 3));
        using var first = await gate.AcquireUploadAsync(CancellationToken.None, "甲 · 第一部", accountKey: "a|personal");
        using var second = await gate.AcquireUploadAsync(CancellationToken.None, "甲 · 第二部", accountKey: "a|personal");
    }

    [Fact]
    public async Task FreeUploadSlotGoesToAnotherAccountWhenThisOneIsNotReady()
    {
        var gate = new KuaishouMachineGate(() => (4, QueueProjectSlotCoordinator.AccountOrderMode, 2, 3));
        using var first = await gate.AcquireUploadAsync(CancellationToken.None, "甲 · 第一部", accountKey: "a|personal");
        using var second = await gate.AcquireUploadAsync(CancellationToken.None, "乙 · 第一部", accountKey: "b|personal");
    }

    [Fact]
    public async Task NextUploadSlotStaysWithTheAccountThatStillHasAReadyDrama()
    {
        var uploads = 1;
        var gate = new KuaishouMachineGate(() => (4, QueueProjectSlotCoordinator.AccountOrderMode, uploads, 3));
        using var first = await gate.AcquireUploadAsync(CancellationToken.None, "甲 · 第一部", accountKey: "a|personal");
        var sameAccountWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherAccountWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sameAccount = gate.AcquireUploadAsync(
            CancellationToken.None,
            "甲 · 第二部",
            _ =>
            {
                sameAccountWaiting.TrySetResult();
                return Task.CompletedTask;
            },
            "a|personal");
        var otherAccount = gate.AcquireUploadAsync(
            CancellationToken.None,
            "乙 · 第一部",
            _ =>
            {
                otherAccountWaiting.TrySetResult();
                return Task.CompletedTask;
            },
            "b|personal");

        await Task.WhenAll(sameAccountWaiting.Task, otherAccountWaiting.Task).WaitAsync(TimeSpan.FromSeconds(2));
        uploads = 2;
        var winner = await Task.WhenAny(sameAccount, otherAccount).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Same(sameAccount, winner);
        Assert.False(otherAccount.IsCompleted);
        using var acquired = await sameAccount;
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
