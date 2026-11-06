using PlatformPublisher.Desktop.ViewModels;

namespace PlatformPublisher.Desktop.Views;

public partial class AnalyticsView : Avalonia.Controls.UserControl
{
    public AnalyticsView()
    {
        InitializeComponent();
        DailyChart.HoverTextChanged += text => HoverText.Text = text ?? string.Empty;
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && IsVisible && DataContext is AnalyticsViewModel vm)
                _ = vm.ActivateAsync();
        };
    }
}
