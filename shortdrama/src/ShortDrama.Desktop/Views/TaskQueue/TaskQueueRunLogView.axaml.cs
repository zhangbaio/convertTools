using Avalonia.Controls;
using Avalonia.Interactivity;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views.TaskQueue;

public partial class TaskQueueRunLogView : UserControl
{
    public TaskQueueRunLogView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void CopyRunLog_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is null || ViewModel is null)
        {
            return;
        }

        await topLevel.Clipboard.SetTextAsync(ViewModel.BuildVisibleActivityLogText());
    }

    private void ShowAllProjectsLog_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ShowAllProjectsActivityLog();
    }
}
