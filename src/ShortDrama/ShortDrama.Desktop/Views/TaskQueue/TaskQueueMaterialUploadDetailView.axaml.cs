using Avalonia.Controls;
using Avalonia.Interactivity;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views.TaskQueue;

public partial class TaskQueueMaterialUploadDetailView : UserControl
{
    public TaskQueueMaterialUploadDetailView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void MarkMaterialUploadCompleted_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.MarkMaterialUploadCompleted();
    }
}
