using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views.TaskQueue;

public partial class TaskQueueOverviewView : UserControl
{
    public TaskQueueOverviewView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private async void PickRootDir_Click(object? sender, RoutedEventArgs e)
    {
        if (OwnerWindow?.StorageProvider is null)
        {
            return;
        }

        var folders = await OwnerWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择项目根目录",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            ViewModel?.SetRootDir(folder.Path.LocalPath);
        }
    }

    private void CheckAllProjects_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.SetAllProjectsChecked(true);
    }

    private void UncheckAllProjects_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.SetAllProjectsChecked(false);
    }

    private async void ArchiveCheckedProjects_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || OwnerWindow is null)
        {
            return;
        }

        var selectedProjects = ViewModel.Projects.Where(item => item.IsChecked).ToArray();
        if (selectedProjects.Length == 0)
        {
            return;
        }

        var preserveEpisodes = await ResolveArchivePreserveEpisodesAsync(selectedProjects);
        if (preserveEpisodes is null &&
            selectedProjects.Any(item => !string.Equals(item.MaterialUploadStepStatus, "已完成", StringComparison.Ordinal)))
        {
            return;
        }

        await ViewModel.ArchiveCheckedProjectsWithOptionsAsync(preserveEpisodes);
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

    private void OpenTaskQueueDownloadDetail_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.OpenTaskQueueNodeDetail((sender as Control)?.DataContext as ProjectListItemViewModel, "download");
    }

    private void OpenTaskQueueProjectMaterialDetail_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.OpenTaskQueueNodeDetail((sender as Control)?.DataContext as ProjectListItemViewModel, "project-material");
    }

    private void OpenTaskQueueEpisodeUploadDetail_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.OpenTaskQueueNodeDetail((sender as Control)?.DataContext as ProjectListItemViewModel, "episode-upload");
    }

    private void OpenTaskQueueMaterialUploadDetail_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.OpenTaskQueueNodeDetail((sender as Control)?.DataContext as ProjectListItemViewModel, "material-upload");
    }

    private void OpenTaskQueueProjectFolder_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.OpenProjectFolder((sender as Control)?.DataContext as ProjectListItemViewModel);
    }

    private void OpenTaskQueueSourceFolder_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.OpenProjectSourceFolder((sender as Control)?.DataContext as ProjectListItemViewModel);
    }

    private void OpenTaskQueueWorkflowFolder_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.OpenProjectWorkflowFolder((sender as Control)?.DataContext as ProjectListItemViewModel);
    }
}
