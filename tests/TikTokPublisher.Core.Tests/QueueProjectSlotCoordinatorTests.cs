using FluentAssertions;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueProjectSlotCoordinatorTests
{
    private readonly object _runnerA = new();
    private readonly object _runnerB = new();

    [Fact]
    public void AccountOrder_FirstAccountUsesAllSlotsWhileItHasWaitingDramas()
    {
        var slots = new QueueProjectSlotCoordinator();
        slots.Configure(4, QueueProjectSlotCoordinator.AccountOrderMode);
        slots.SetDemand(_runnerA, [new QueueProjectSlotDemand("A", 0, 5)]);
        slots.SetDemand(_runnerB, [new QueueProjectSlotDemand("B", 1, 5)]);

        slots.TryAcquire("B", 1).Should().BeFalse();
        for (var i = 0; i < 4; i++)
            slots.TryAcquire("A", 0).Should().BeTrue();
        slots.TryAcquire("A", 0).Should().BeFalse();

        slots.Release("A");
        slots.SetDemand(_runnerA, [new QueueProjectSlotDemand("A", 0, 1)]);
        slots.TryAcquire("B", 1).Should().BeFalse();
        slots.TryAcquire("A", 0).Should().BeTrue();
    }

    [Fact]
    public void AccountOrder_NextAccountGetsSlotsTheFirstCannotUse()
    {
        var slots = new QueueProjectSlotCoordinator();
        slots.Configure(4, QueueProjectSlotCoordinator.AccountOrderMode);
        slots.SetDemand(_runnerA, [new QueueProjectSlotDemand("A", 0, 2)]);
        slots.SetDemand(_runnerB, [new QueueProjectSlotDemand("B", 1, 5)]);
        slots.TryAcquire("A", 0).Should().BeTrue();
        slots.TryAcquire("A", 0).Should().BeTrue();

        slots.SetDemand(_runnerA, []);
        slots.TryAcquire("B", 1).Should().BeTrue();
        slots.TryAcquire("B", 1).Should().BeTrue();
        slots.TryAcquire("B", 1).Should().BeFalse();
        slots.ActiveCount.Should().Be(4);
    }

    [Fact]
    public void AccountOrder_EarlierAccountAtItsProjectConcurrencyDoesNotBlockLaterAccount()
    {
        var slots = new QueueProjectSlotCoordinator();
        slots.Configure(4, QueueProjectSlotCoordinator.AccountOrderMode);
        slots.SetDemand(_runnerA, [new QueueProjectSlotDemand("A", 0, 2)]);
        slots.SetDemand(_runnerB, [new QueueProjectSlotDemand("B", 1, 5)]);

        slots.TryAcquire("A", 0).Should().BeTrue();
        slots.TryAcquire("A", 0).Should().BeTrue();
        slots.TryAcquire("B", 1).Should().BeFalse();
        slots.SetDemand(_runnerA, []);
        slots.TryAcquire("B", 1).Should().BeTrue();
    }

    [Fact]
    public void RoundRobin_GivesSlotToAccountHoldingFewer()
    {
        var slots = new QueueProjectSlotCoordinator();
        slots.Configure(4, QueueProjectSlotCoordinator.RoundRobinMode);
        slots.SetDemand(_runnerA, [new QueueProjectSlotDemand("A", 0, 5)]);
        slots.SetDemand(_runnerB, [new QueueProjectSlotDemand("B", 1, 5)]);

        slots.TryAcquire("A", 0).Should().BeTrue();
        slots.TryAcquire("A", 0).Should().BeFalse();
        slots.TryAcquire("B", 1).Should().BeTrue();
        slots.TryAcquire("A", 0).Should().BeTrue();
    }

    [Fact]
    public void ServerDispatchMode_FillFirstUsesAccountOrderAndBlankUsesRoundRobin()
    {
        QueueProjectSlotCoordinator.SlotModeForServer("fill_first")
            .Should().Be(QueueProjectSlotCoordinator.AccountOrderMode);
        QueueProjectSlotCoordinator.ServerUsesRoundRobin("fill_first").Should().BeFalse();
        QueueProjectSlotCoordinator.SlotModeForServer("")
            .Should().Be(QueueProjectSlotCoordinator.RoundRobinMode);
        QueueProjectSlotCoordinator.SlotModeForServer("round_robin")
            .Should().Be(QueueProjectSlotCoordinator.RoundRobinMode);
        QueueProjectSlotCoordinator.NormalizeServerDispatchMode("unknown").Should().Be("");
    }

    [Fact]
    public void ClearDemand_StopsBlockingOtherAccounts()
    {
        var slots = new QueueProjectSlotCoordinator();
        slots.Configure(2, QueueProjectSlotCoordinator.AccountOrderMode);
        slots.SetDemand(_runnerA, [new QueueProjectSlotDemand("A", 0, 3)]);
        slots.TryAcquire("B", 1).Should().BeFalse();

        slots.ClearDemand(_runnerA);
        slots.TryAcquire("B", 1).Should().BeTrue();
    }

    [Fact]
    public void UploadSlots_SameAccountFillsRemainingCapacityBeforeAnotherBrowser()
    {
        var uploads = new UploadSlotCoordinator();
        uploads.Configure(2);
        uploads.SetReady(_runnerA, [new UploadReadyDemand("A", 2)]);
        uploads.TryAcquire("A").Should().BeTrue();
        uploads.SetReady(_runnerA, [new UploadReadyDemand("A", 1)]);
        uploads.TryAcquire("B").Should().BeFalse();
        uploads.TryAcquire("A").Should().BeTrue();
        uploads.TryAcquire("B").Should().BeFalse();

        uploads.Release("A");
        uploads.SetReady(_runnerA, []);
        uploads.TryAcquire("B").Should().BeTrue();
    }

    [Fact]
    public void UploadSlots_DoesNotHoldASlotForAnAccountThatIsNotReady()
    {
        var uploads = new UploadSlotCoordinator();
        uploads.Configure(2);
        uploads.SetReady(_runnerA, [new UploadReadyDemand("A", 1)]);
        uploads.TryAcquire("A").Should().BeTrue();
        uploads.SetReady(_runnerA, []);
        uploads.TryAcquire("B").Should().BeTrue();
    }
}
