using Avalonia.Controls;
using Avalonia.Interactivity;
using ShortDrama.Desktop.Models;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views.TaskQueue;

public partial class TaskQueueEpisodeUploadDetailView : UserControl
{
    public TaskQueueEpisodeUploadDetailView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void RetryEpisodeUpload_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.RetryEpisodeUploadAsync((sender as Control)?.DataContext as EpisodeUploadItemViewModel);
    }

    private void SkipEpisodeUpload_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.SkipEpisodeUpload((sender as Control)?.DataContext as EpisodeUploadItemViewModel);
    }

    private void MarkEpisodeUploadCompleted_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.MarkEpisodeUploadCompleted((sender as Control)?.DataContext as EpisodeUploadItemViewModel);
    }

    private void MarkSelectedEpisodeUploadCompleted_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.MarkEpisodeUploadCompleted(ViewModel.SelectedEpisodeUploadEpisode);
    }
}
