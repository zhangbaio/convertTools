using Avalonia.Media;
using FluentAssertions;
using TikTokPublisher.Ui.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class LogServiceBrushTests
{
    [Theory]
    [InlineData("info", "#E3F2FF")]
    [InlineData("unknown", "#E3F2FF")]
    [InlineData("success", "#6EE7B7")]
    [InlineData("warn", "#FFD27A")]
    [InlineData("error", "#FF9EAA")]
    public void BrushForLevel_UsesHighContrastDarkPanelPalette(string level, string expected)
    {
        ColorOf(LogService.BrushForLevel(level)).Should().Be(Color.Parse(expected));
    }

    [Theory]
    [InlineData("done", "#6EE7B7")]
    [InlineData("warning", "#FFD27A")]
    [InlineData("failed", "#FF9EAA")]
    public void BrushForLevel_NormalizesLevelAliases(string level, string expected)
    {
        ColorOf(LogService.BrushForLevel(level)).Should().Be(Color.Parse(expected));
    }

    private static Color ColorOf(IBrush brush) =>
        brush.Should().BeOfType<SolidColorBrush>().Which.Color;
}
