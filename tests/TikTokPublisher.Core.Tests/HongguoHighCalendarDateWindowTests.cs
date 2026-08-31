using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;

namespace TikTokPublisher.Core.Tests;

public sealed class HongguoHighCalendarDateWindowTests
{
    [Fact]
    public void One_day_window_is_the_supplied_natural_day()
    {
        var today = new DateOnly(2026, 8, 31);

        var window = HongguoHighCalendarMapper.ResolveRecentDateWindow(1, today);

        window.StartDate.Should().Be(today);
        window.EndDate.Should().Be(today);
        window.DisplayText.Should().Be("2026-08-31");
    }

    [Fact]
    public void Enriched_filter_removes_previous_day_and_unknown_dates()
    {
        var window = new HongguoHighDateWindow(
            new DateOnly(2026, 8, 31),
            new DateOnly(2026, 8, 31));
        var today = Item("today", "2026-08-31 00:01:00");
        var yesterday = Item("yesterday", "2026-08-30 23:59:59");
        var unknown = Item("unknown", "");

        var filtered = HongguoHighCalendarMapper.FilterByDateWindow(
            [today, yesterday, unknown],
            window,
            keepUnknownDate: false);

        filtered.Should().ContainSingle().Which.BookId.Should().Be("today");
    }

    [Fact]
    public void Preliminary_filter_keeps_unknown_dates_until_details_arrive()
    {
        var window = new HongguoHighDateWindow(
            new DateOnly(2026, 8, 31),
            new DateOnly(2026, 8, 31));
        var today = Item("today", "2026-08-31 12:00:00");
        var yesterday = Item("yesterday", "2026-08-30 12:00:00");
        var unknown = Item("unknown", "");

        var filtered = HongguoHighCalendarMapper.FilterByDateWindow(
            [today, yesterday, unknown],
            window,
            keepUnknownDate: true);

        filtered.Select(item => item.BookId).Should().Equal("today", "unknown");
    }

    private static DramaSearchItem Item(string id, string publishTime) =>
        new(id, id, "", 1, "", "", "", publishTime);
}
