using Avalonia.Controls;
using Avalonia.Interactivity;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views.TaskQueue;

public partial class TaskQueueDetailView : UserControl
{
    public TaskQueueDetailView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private void CloseTaskQueueDetail_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.CloseTaskQueueNodeDetail();
    }

    private async void ArchiveSelectedProject_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null || OwnerWindow is null)
        {
            return;
        }

        var preserveEpisodes = await ResolveArchivePreserveEpisodesAsync([ViewModel.SelectedProject]);
        if (preserveEpisodes is null &&
            !string.Equals(ViewModel.SelectedProject.MaterialUploadStepStatus, "已完成", StringComparison.Ordinal))
        {
            return;
        }

        await ViewModel.ArchiveSelectedProjectWithOptionsAsync(preserveEpisodes);
    }

    private async Task<IReadOnlyCollection<int>?> ResolveArchivePreserveEpisodesAsync(IEnumerable<ProjectListItemViewModel> projects)
    {
        if (OwnerWindow is null)
        {
            return null;
        }

        var needsPrompt = projects.Any(item => !string.Equals(item.MaterialUploadStepStatus, "已完成", StringComparison.Ordinal));
        if (!needsPrompt)
        {
            return Array.Empty<int>();
        }

        var window = new ArchiveMaterialPromptWindow();
        var result = await window.ShowDialog<string?>(OwnerWindow);
        return result switch
        {
            "keep" => new[] { 2, 3, 4 },
            "delete" => Array.Empty<int>(),
            _ => null
        };
    }
}
