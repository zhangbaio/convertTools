using PlatformPublisher.Common.Models;
using PlatformPublisher.Desktop.Services;
using Xunit;

namespace PlatformPublisher.Desktop.Tests;

public sealed class KuaishouDispatchSelectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 11, 30, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Today_added_drama_stays_checked_even_when_it_has_not_run()
    {
        var job = new PublishJob { CreatedAt = Now.AddHours(-1), Status = PublishJobStatus.Pending };

        Assert.True(KuaishouDispatchSelection.ShouldStayChecked(job, alreadyChecked: false, Now));
    }

    [Fact]
    public void Drama_executed_today_stays_checked()
    {
        var job = new PublishJob
        {
            CreatedAt = Now.AddDays(-2),
            Status = PublishJobStatus.Failed,
            LastCompletedAt = Now.AddMinutes(-20),
        };

        Assert.True(KuaishouDispatchSelection.ShouldStayChecked(job, alreadyChecked: false, Now));
    }

    [Fact]
    public void Earlier_executed_drama_that_is_still_checked_stays_checked()
    {
        var job = new PublishJob
        {
            CreatedAt = Now.AddDays(-3),
            Status = PublishJobStatus.Succeeded,
            LastCompletedAt = Now.AddDays(-3),
        };

        Assert.True(KuaishouDispatchSelection.ShouldStayChecked(job, alreadyChecked: true, Now));
    }

    [Fact]
    public void Earlier_pending_manual_selection_can_be_cleared()
    {
        var job = new PublishJob { CreatedAt = Now.AddDays(-1), Status = PublishJobStatus.Pending };

        Assert.False(KuaishouDispatchSelection.ShouldStayChecked(job, alreadyChecked: true, Now));
    }

    [Fact]
    public void Earlier_executed_drama_that_is_already_unchecked_stays_unchecked()
    {
        var job = new PublishJob
        {
            CreatedAt = Now.AddDays(-4),
            Status = PublishJobStatus.Failed,
            LastCompletedAt = Now.AddDays(-4),
        };

        Assert.False(KuaishouDispatchSelection.ShouldStayChecked(job, alreadyChecked: false, Now));
    }
}
