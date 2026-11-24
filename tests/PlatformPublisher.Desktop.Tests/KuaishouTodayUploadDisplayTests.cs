using PlatformPublisher.Common.Models;
using PlatformPublisher.Desktop.ViewModels;
using Xunit;

namespace PlatformPublisher.Desktop.Tests;

public sealed class KuaishouTodayUploadDisplayTests
{
    [Fact]
    public void Results_update_their_own_account_and_edition_even_while_another_account_is_reading()
    {
        var display = new KuaishouTodayUploadDisplay();
        var day = new DateTime(2026, 11, 24);
        var personal = PublishPlatform.KuaishouPersonalRevenue;
        var enterprise = PublishPlatform.KuaishouEnterpriseRevenue;

        var first = display.Begin(personal, "account-a", day);
        var second = display.Begin(personal, "account-b", day);
        var third = display.Begin(enterprise, "account-a", day);
        Assert.Null(display.GetCount(personal, "account-a", day));
        Assert.True(display.TryRecord(first, day, 5));
        Assert.True(display.TryRecord(second, day, 0));
        Assert.True(display.TryRecord(third, day, 3));
        Assert.Equal(5, display.GetCount(personal, "ACCOUNT-A", day));
        Assert.Equal(0, display.GetCount(personal, "account-b", day));
        Assert.Equal(3, display.GetCount(enterprise, "account-a", day));
    }

    [Fact]
    public void An_older_bulk_result_cannot_replace_a_newer_account_refresh()
    {
        var display = new KuaishouTodayUploadDisplay();
        var day = new DateTime(2026, 11, 24);
        var platform = PublishPlatform.KuaishouPersonalRevenue;
        var prior = display.Begin(platform, "account-a", day);
        Assert.True(display.TryRecord(prior, day, 5));

        var uploaded = display.Begin(platform, "account-a", day);
        Assert.Equal(5, display.GetCount(platform, "account-a", day));
        Assert.True(display.TryRecord(uploaded, day, 6));
        Assert.False(display.TryRecord(prior, day, 5));
        Assert.Equal(6, display.GetCount(platform, "account-a", day));
    }

    [Fact]
    public void Old_counts_are_discarded_at_midnight()
    {
        var display = new KuaishouTodayUploadDisplay();
        var day = new DateTime(2026, 11, 24);
        var platform = PublishPlatform.KuaishouPersonalRevenue;
        var request = display.Begin(platform, "account-a", day);
        Assert.True(display.TryRecord(request, day, 5));
        Assert.False(display.TryRecord(request, day.AddDays(1), 6));
        Assert.Null(display.GetCount(platform, "account-a", day.AddDays(1)));

        var nextDay = display.Begin(platform, "account-a", day.AddDays(1));
        Assert.Null(display.GetCount(platform, "account-a", day.AddDays(1)));
        Assert.False(display.TryRecord(request, day.AddDays(1), 8));
        Assert.True(display.TryRecord(nextDay, day.AddDays(1), 1));
    }
}
