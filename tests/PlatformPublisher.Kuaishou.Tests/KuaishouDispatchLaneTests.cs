using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouDispatchLaneTests
{
    [Fact]
    public void Enqueue_StartsUpToClientParallelism()
    {
        var lane = new KuaishouDispatchLane<string>();

        Assert.Equal(["a", "b"], lane.Enqueue("a", 4).Concat(lane.Enqueue("b", 4)));
        var held = lane.Enqueue("c", 2);

        Assert.Empty(held);
        Assert.Equal(2, lane.Running);
        Assert.Equal(1, lane.Pending);
    }

    [Fact]
    public void Completed_StartsTheNextWaitingDrama()
    {
        var lane = new KuaishouDispatchLane<string>();
        lane.Enqueue("a", 1);
        Assert.Empty(lane.Enqueue("b", 1));

        var next = lane.Completed(1, stopStarting: false);

        Assert.Equal(["b"], next);
        Assert.Equal(1, lane.Running);
        Assert.Equal(0, lane.Pending);
    }

    [Fact]
    public void Completed_StopClearsDramasThatHaveNotStarted()
    {
        var lane = new KuaishouDispatchLane<string>();
        lane.Enqueue("a", 1);
        lane.Enqueue("b", 1);

        Assert.Empty(lane.Completed(1, stopStarting: true));
        Assert.True(lane.Idle);
    }
}
