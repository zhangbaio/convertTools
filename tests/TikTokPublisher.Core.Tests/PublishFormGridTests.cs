using Avalonia;
using Avalonia.Controls;
using TikTokPublisher.Ui.Controls;

namespace TikTokPublisher.Core.Tests;

public sealed class PublishFormGridTests
{
    [Theory]
    [InlineData(900, 300, 0, 30, 600, 600, 30)]
    [InlineData(720, 360, 0, 30, 720, 0, 60)]
    [InlineData(600, 0, 30, 60, 600, 0, 90)]
    public void Publish_form_grid_reflows_visible_fields_and_clamps_the_template_span(
        double width, double thirdLeft, double thirdTop, double templateTop, double templateWidth,
        double lastLeft, double lastTop)
    {
        var grid = new PublishFormGrid();
        var first = new Border { Height = 20 };
        var hidden = new Border { Height = 200, IsVisible = false };
        var third = new Border { Height = 20 };
        var template = new Border { Height = 20 };
        var last = new Border { Height = 20 };
        PublishFormGrid.SetColumnSpan(template, 2);
        foreach (var control in new[] { first, hidden, third, template, last })
            grid.Children.Add(control);

        grid.Measure(new Size(width, double.PositiveInfinity));
        grid.Arrange(new Rect(0, 0, width, grid.DesiredSize.Height));

        Assert.Equal(0, first.Bounds.X);
        Assert.Equal(thirdLeft, third.Bounds.X);
        Assert.Equal(thirdTop, third.Bounds.Y);
        Assert.Equal(0, template.Bounds.X);
        Assert.Equal(templateTop, template.Bounds.Y);
        Assert.Equal(templateWidth, template.Bounds.Width);
        Assert.Equal(lastLeft, last.Bounds.X);
        Assert.Equal(lastTop, last.Bounds.Y);
        Assert.Equal(lastTop + 20, grid.DesiredSize.Height);
    }
}
