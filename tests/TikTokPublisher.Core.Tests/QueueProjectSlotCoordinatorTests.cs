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
        slots.Configure(4, 0, QueueProjectSlotCoordinator.AccountOrderMode);
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
        slots.Configure(4, 0, QueueProjectSlotCoordinator.AccountOrderMode);
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
    public void AccountOrder_CappedEarlierAccountDoesNotBlockLaterAccount()
    {
        var slots = new QueueProjectSlotCoordinator();
        slots.Configure(4, 2, QueueProjectSlotCoordinator.AccountOrderMode);
        slots.SetDemand(_runnerA, [new QueueProjectSlotDemand("A", 0, 5)]);
        slots.SetDemand(_runnerB, [new QueueProjectSlotDemand("B", 1, 5)]);

        slots.TryAcquire("A", 0).Should().BeTrue();
        slots.TryAcquire("A", 0).Should().BeTrue();
        slots.TryAcquire("A", 0).Should().BeFalse();
        slots.TryAcquire("B", 1).Should().BeTrue();
    }

    [Fact]
    public void RoundRobin_GivesSlotToAccountHoldingFewer()
    {
        var slots = new QueueProjectSlotCoordinator();
        slots.Configure(4, 0, QueueProjectSlotCoordinator.RoundRobinMode);
        slots.SetDemand(_runnerA, [new QueueProjectSlotDemand("A", 0, 5)]);
        slots.SetDemand(_runnerB, [new QueueProjectSlotDemand("B", 1, 5)]);

        slots.TryAcquire("A", 0).Should().BeTrue();
        slots.TryAcquire("A", 0).Should().BeFalse();
        slots.TryAcquire("B", 1).Should().BeTrue();
        slots.TryAcquire("A", 0).Should().BeTrue();
    }

    [Fact]
    public void ClearDemand_StopsBlockingOtherAccounts()
    {
        var slots = new QueueProjectSlotCoordinator();
        slots.Configure(2, 0, QueueProjectSlotCoordinator.AccountOrderMode);
        slots.SetDemand(_runnerA, [new QueueProjectSlotDemand("A", 0, 3)]);
        slots.TryAcquire("B", 1).Should().BeFalse();

        slots.ClearDemand(_runnerA);
        slots.TryAcquire("B", 1).Should().BeTrue();
    }

    [Fact]
    public void UploadSlots_GlobalCapacityLimitsAllAccounts()
    {
        var uploads = new UploadSlotCoordinator();
        uploads.Configure(2);
        uploads.TryAcquire("A").Should().BeTrue();
        uploads.TryAcquire("A").Should().BeFalse();
        uploads.TryAcquire("B").Should().BeTrue();
        uploads.TryAcquire("C").Should().BeFalse();
        uploads.Release("A");
        uploads.TryAcquire("C").Should().BeTrue();
    }
}
