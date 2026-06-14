using Avalonia.Controls;
using Avalonia.Interactivity;
using ShortDrama.Desktop.Models;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views.TaskQueue;

public partial class TaskQueueDownloadDetailView : UserControl
{
    public TaskQueueDownloadDetailView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void RunSelectedDownloadDetail_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null)
        {
            return;
        }

        await ViewModel.RunProjectStepFromQueueAsync(ViewModel.SelectedProject, "download", "下载剧集");
    }

    private async void RetryDownloadEpisode_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.RetryDownloadEpisodeAsync((sender as Control)?.DataContext as DownloadEpisodeItemViewModel);
    }

    private async void RemoveDownloadEpisode_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.RemoveDownloadEpisodeAsync((sender as Control)?.DataContext as DownloadEpisodeItemViewModel);
    }
}
