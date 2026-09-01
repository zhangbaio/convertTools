using PlatformPublisher.Core.Models;
using PlatformPublisher.Core.Services;
using Xunit;

namespace PlatformPublisher.Core.Tests;

public sealed class PublishSchedulePolicyTests
{
    [Fact]
    public void ScheduledJobBecomesDueOnlyAtConfiguredTime()
    {
        var scheduledAt = new DateTimeOffset(2026, 9, 1, 20, 30, 0, TimeSpan.FromHours(8));
        var job = new PublishJob
        {
            Status = PublishJobStatus.Pending,
            ScheduledAt = scheduledAt,
        };

        Assert.False(PublishSchedulePolicy.IsDue(job, scheduledAt.AddSeconds(-1)));
        Assert.True(PublishSchedulePolicy.IsDue(job, scheduledAt));
        Assert.True(PublishSchedulePolicy.CanRunNow(job, scheduledAt.AddMinutes(1)));
    }

    [Theory]
    [InlineData("2026-09-01 20:30")]
    [InlineData("2026/9/1 8:05")]
    public void ParsesSupportedLocalScheduleFormats(string text)
    {
        Assert.True(PublishSchedulePolicy.TryParseLocal(text, out var value));
        Assert.Equal(2026, value.Year);
        Assert.Equal(9, value.Month);
        Assert.Equal(1, value.Day);
    }

    [Fact]
    public void RecoversJobsLeftRunningByPreviousProcess()
    {
        var interrupted = new PublishJob { Status = PublishJobStatus.Running };
        var completed = new PublishJob { Status = PublishJobStatus.Succeeded };

        var count = PublishSchedulePolicy.RecoverInterrupted([interrupted, completed]);

        Assert.Equal(1, count);
        Assert.Equal(PublishJobStatus.Pending, interrupted.Status);
        Assert.Equal(PublishJobStatus.Succeeded, completed.Status);
    }
}
