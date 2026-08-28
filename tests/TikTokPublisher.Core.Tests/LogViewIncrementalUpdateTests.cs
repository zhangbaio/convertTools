using FluentAssertions;
using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.Views;

namespace TikTokPublisher.Core.Tests;

public sealed class LogViewIncrementalUpdateTests
{
    [Fact]
    public void Uses_incremental_update_when_existing_prefix_is_unchanged()
    {
        var first = new LogEntry { Text = "first" };
        var second = new LogEntry { Text = "second" };

        LogView.TryResolveIncrementalUpdate([first], [first, second], out var removed).Should().BeTrue();
        removed.Should().Be(0);
    }

    [Fact]
    public void Uses_incremental_update_when_head_entry_is_trimmed()
    {
        var first = new LogEntry { Text = "first" };
        var second = new LogEntry { Text = "second" };

        LogView.TryResolveIncrementalUpdate([first, second], [second], out var removed).Should().BeTrue();
        removed.Should().Be(1);
    }

    [Fact]
    public void Requires_rebuild_when_entries_change_in_the_middle()
    {
        var first = new LogEntry { Text = "first" };
        var second = new LogEntry { Text = "second" };
        var third = new LogEntry { Text = "third" };

        LogView.TryResolveIncrementalUpdate([first, second], [first, third], out var removed).Should().BeFalse();
        removed.Should().Be(0);
    }
}
