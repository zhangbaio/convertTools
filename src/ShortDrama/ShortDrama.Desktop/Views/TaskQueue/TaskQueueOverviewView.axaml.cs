using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views.TaskQueue;

public partial class TaskQueueOverviewView : UserControl
{
    private readonly ScrollViewer? _headerScrollViewer;
    private readonly ScrollViewer? _projectsScrollViewer;
    private bool _syncingHorizontalScroll;

    public TaskQueueOverviewView()
    {
        InitializeComponent();
        _headerScrollViewer = this.FindControl<ScrollViewer>("HeaderScrollViewer");
        _projectsScrollViewer = this.FindControl<ScrollViewer>("ProjectsScrollViewer");
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
            Title = "\u9009\u62e9\u9879\u76ee\u6839\u76ee\u5f55",
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

    private void CheckAllQueueSteps_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.SetAllQueueStepsEnabled(true);
    }

    private void UncheckAllQueueSteps_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.SetAllQueueStepsEnabled(false);
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
            selectedProjects.Any(item => !string.Equals(item.MaterialUploadStepStatus, "\u5df2\u5b8c\u6210", StringComparison.Ordinal)))
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

        var needsPrompt = projects.Any(item => !string.Equals(item.MaterialUploadStepStatus, "\u5df2\u5b8c\u6210", StringComparison.Ordinal));
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

    private void ProjectsScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncingHorizontalScroll ||
            _headerScrollViewer is null ||
            _projectsScrollViewer is null)
        {
            return;
        }

        if (Math.Abs(e.OffsetDelta.X) <= double.Epsilon &&
            Math.Abs(_headerScrollViewer.Offset.X - _projectsScrollViewer.Offset.X) <= 0.1d)
        {
            return;
        }

        _syncingHorizontalScroll = true;
        try
        {
            _headerScrollViewer.Offset = new Vector(_projectsScrollViewer.Offset.X, 0);
        }
        finally
        {
            _syncingHorizontalScroll = false;
        }
    }
}
