using TikTokPublisher.Core.Queue;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public class TikTokDispatchQueueSchedulerTests
{
    [Fact]
    public void AccountOrderStartsOneAccountAtATime()
    {
        var scheduler = new TikTokDispatchQueueScheduler();
        scheduler.Note(@"D:\a", "a", 0, [@"D:\a\1", @"D:\a\2"]);
        scheduler.Note(@"D:\b", "b", 1, [@"D:\b\1"]);
        scheduler.Note(@"D:\c", "c", 2, [@"D:\c\1"]);

        var first = scheduler.Take(4, roundRobin: false);
        Assert.Single(first);
        Assert.Equal("a", first[0].AccountId);
        Assert.Equal(2, first[0].ProjectDirs.Count);
        Assert.Empty(scheduler.Take(4, roundRobin: false));

        scheduler.Complete(first[0].Workspace);
        var second = scheduler.Take(4, roundRobin: false);
        Assert.Equal("b", second[0].AccountId);
    }

    [Fact]
    public void RoundRobinKeepsOneDramaOnEachStartedAccount()
    {
        var scheduler = new TikTokDispatchQueueScheduler();
        scheduler.Note(@"D:\a", "a", 0, [@"D:\a\1", @"D:\a\2"]);
        scheduler.Note(@"D:\b", "b", 1, [@"D:\b\1", @"D:\b\2"]);
        scheduler.Note(@"D:\c", "c", 2, [@"D:\c\1"]);

        var first = scheduler.Take(2, roundRobin: true);
        Assert.Equal(2, first.Count);
        Assert.All(first, start => Assert.Single(start.ProjectDirs));
        Assert.Equal("a", first[0].AccountId);
        Assert.Equal("b", first[1].AccountId);

        scheduler.Complete(first[0].Workspace);
        var second = scheduler.Take(2, roundRobin: true);
        Assert.Equal("c", second[0].AccountId);
        Assert.Single(second[0].ProjectDirs);
    }

    [Fact]
    public void DropPendingRemovesDramasAlreadyHandedToTheRunningQueue()
    {
        var scheduler = new TikTokDispatchQueueScheduler();
        scheduler.Note(@"D:\a", "a", 0, [@"D:\a\1", @"D:\a\2", @"D:\a\3"]);

        scheduler.DropPending(@"D:\a", [@"D:\a\2", @"D:\a\3"]);
        var started = scheduler.Take(4, roundRobin: false);
        Assert.Single(started);
        Assert.Equal(new[] { Path.GetFullPath(@"D:\a\1") }, started[0].ProjectDirs.Select(Path.GetFullPath));
    }

    [Fact]
    public void ClearPendingStopsTheRestFromStartingAfterManualStop()
    {
        var scheduler = new TikTokDispatchQueueScheduler();
        scheduler.Note(@"D:\a", "a", 0, [@"D:\a\1", @"D:\a\2"]);
        var started = scheduler.Take(4, roundRobin: true);
        Assert.Single(started[0].ProjectDirs);

        scheduler.ClearPending(@"D:\a");
        scheduler.Complete(started[0].Workspace);
        Assert.Empty(scheduler.Take(4, roundRobin: true));
    }

    [Fact]
    public void BusyAccountIsSkippedSoTheNextOneCanStart()
    {
        var scheduler = new TikTokDispatchQueueScheduler();
        scheduler.Note(@"D:\a", "a", 0, [@"D:\a\1"]);
        scheduler.Note(@"D:\b", "b", 1, [@"D:\b\1"]);

        var started = scheduler.Take(4, roundRobin: false, workspace => workspace.EndsWith("a", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("b", started[0].AccountId);

        scheduler.Restore(started[0].Workspace, started[0].ProjectDirs);
        var again = scheduler.Take(4, roundRobin: false);
        Assert.Equal("a", again[0].AccountId);
    }
}
