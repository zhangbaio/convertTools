using FluentAssertions;
using TikTokPublisher.Ui.Services;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public sealed class LaptopLayoutTests
{
    [Fact]
    public void Large_screen_keeps_the_preferred_window_size()
    {
        var fit = LaptopLayout.FitWindow(1920, 1080);

        fit.Width.Should().Be(1496);
        fit.Height.Should().Be(860);
        LaptopLayout.IsCompact(1920, 1080).Should().BeFalse();
        LaptopLayout.ShouldShortenNav(1920).Should().BeFalse();
    }

    [Fact]
    public void Laptop_work_area_shrinks_the_window_and_turns_on_compact_density()
    {
        var fit = LaptopLayout.FitWindow(1280, 720);

        fit.Width.Should().BeLessThan(1280);
        fit.Height.Should().BeLessThan(720);
        fit.Width.Should().Be(1178);
        fit.Height.Should().Be(680);
        fit.MinWidth.Should().Be(1100);
        fit.MinHeight.Should().Be(680);
        LaptopLayout.IsCompact(1280, 720).Should().BeTrue();
        LaptopLayout.ShouldShortenNav(1280).Should().BeFalse();
        LaptopLayout.ShouldShortenNav(1279).Should().BeTrue();
    }

    [Fact]
    public void Scaled_1080p_laptop_uses_compact_density_without_shortening_nav()
    {
        LaptopLayout.IsCompact(1536, 864).Should().BeTrue();
        LaptopLayout.ShouldShortenNav(1536).Should().BeFalse();

        var fit = LaptopLayout.FitWindow(1536, 864);
        fit.Width.Should().Be(1413);
        fit.Height.Should().Be(795);
    }

    [Fact]
    public void Saved_placement_is_rejected_when_it_is_larger_than_the_work_area()
    {
        LaptopLayout.PlacementFits(1496, 860, 1280, 720).Should().BeFalse();
        LaptopLayout.PlacementFits(1200, 700, 1280, 720).Should().BeTrue();
        LaptopLayout.PlacementFits(400, 700, 1280, 720).Should().BeFalse();
    }

    [Fact]
    public void Queue_columns_shrink_toward_minimums_without_passing_them()
    {
        double[] preferred = [48, 210, 0, 180];
        double[] minimum = [42, 120, 0, 120];

        var wide = LaptopLayout.FitColumns(preferred, minimum, 2000);
        wide.Should().Equal(48, 210, 0, 180);

        var fitted = LaptopLayout.FitColumns(preferred, minimum, 300);
        fitted.Sum().Should().BeApproximately(300, 0.01);
        fitted[0].Should().BeGreaterThanOrEqualTo(42);
        fitted[1].Should().BeGreaterThanOrEqualTo(120);
        fitted[2].Should().Be(0);
        fitted[3].Should().BeGreaterThanOrEqualTo(120);

        var narrow = LaptopLayout.FitColumns(preferred, minimum, 100);
        narrow.Should().Equal(42, 120, 0, 120);
    }
}
