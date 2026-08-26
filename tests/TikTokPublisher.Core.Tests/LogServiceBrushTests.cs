using Avalonia.Media;
using FluentAssertions;
using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.Views;

namespace TikTokPublisher.Core.Tests;

public sealed class LogServiceBrushTests
{
    [Theory]
    [InlineData("info", "#D7E3EC")]
    [InlineData("unknown", "#D7E3EC")]
    [InlineData("success", "#BDEBD8")]
    [InlineData("warn", "#FFE2A3")]
    [InlineData("error", "#FFC1C8")]
    public void BrushForLevel_UsesHighContrastDarkPanelPalette(string level, string expected)
    {
        ColorOf(LogService.BrushForLevel(level)).Should().Be(Color.Parse(expected));
    }

    [Theory]
    [InlineData("done", "#BDEBD8")]
    [InlineData("warning", "#FFE2A3")]
    [InlineData("failed", "#FFC1C8")]
    public void BrushForLevel_NormalizesLevelAliases(string level, string expected)
    {
        ColorOf(LogService.BrushForLevel(level)).Should().Be(Color.Parse(expected));
    }

    [Theory]
    [InlineData("info", "#72C7FF")]
    [InlineData("success", "#4BD69A")]
    [InlineData("warn", "#F5C66B")]
    [InlineData("error", "#FF6473")]
    public void AccentBrushForLevel_UsesDistinctLevelPalette(string level, string expected)
    {
        ColorOf(LogService.AccentBrushForLevel(level)).Should().Be(Color.Parse(expected));
    }

    [Fact]
    public void LogView_UsesIncrementalUpdate_WhenExistingPrefixIsUnchanged()
    {
        var first = new LogEntry { Text = "first" };
        var second = new LogEntry { Text = "second" };

        LogView.TryResolveIncrementalUpdate([first], [first, second], out var removed).Should().BeTrue();
        removed.Should().Be(0);
    }

    [Fact]
    public void LogView_UsesIncrementalUpdate_WhenHeadEntryIsTrimmed()
    {
        var first = new LogEntry { Text = "first" };
        var second = new LogEntry { Text = "second" };

        LogView.TryResolveIncrementalUpdate([first, second], [second], out var removed).Should().BeTrue();
        removed.Should().Be(1);
    }

    [Fact]
    public void LogView_RequiresRebuild_WhenEntriesChangeInTheMiddle()
    {
        var first = new LogEntry { Text = "first" };
        var second = new LogEntry { Text = "second" };
        var third = new LogEntry { Text = "third" };

        LogView.TryResolveIncrementalUpdate([first, second], [first, third], out var removed).Should().BeFalse();
        removed.Should().Be(0);
    }

    private static Color ColorOf(IBrush brush) =>
        brush.Should().BeOfType<SolidColorBrush>().Which.Color;
}
