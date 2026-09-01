using Avalonia.Media;
using FluentAssertions;
using TikTokPublisher.Ui.Themes;

namespace TikTokPublisher.Core.Tests;

public sealed class NativeWindowThemeTests
{
    [Theory]
    [InlineData("#0D243A", 0x003A240D)]
    [InlineData("#F7FBFF", 0x00FFFBF7)]
    public void ToColorRef_converts_rgb_to_windows_bgr(string hex, int expected)
    {
        NativeWindowTheme.ToColorRef(Color.Parse(hex)).Should().Be(expected);
    }
}
