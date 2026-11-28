using Avalonia.Controls;
using Avalonia.Interactivity;
using PlatformPublisher.Desktop.ViewModels;

namespace PlatformPublisher.Desktop.Views;

public partial class WeixinRuntimeLogView : UserControl
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public WeixinRuntimeLogView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => ViewModel?.ActivateRuntimeLogView();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && IsVisible) ViewModel?.ActivateRuntimeLogView();
        };
    }

    private void OnRefreshLogsClick(object? sender, RoutedEventArgs e) => ViewModel?.ActivateRuntimeLogView();

    private async void OnCopyLogsClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(string.Join(Environment.NewLine, ViewModel.FilteredActivityLogs));
        ViewModel.StatusMessage = $"已复制 {ViewModel.FilteredActivityLogs.Count} 条日志。";
    }
}
