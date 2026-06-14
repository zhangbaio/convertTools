using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ShortDrama.Desktop.Models;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void PickRootDir_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
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

    private async void PickTemplateDocx_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择成本报表模板",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Word 模板")
                {
                    Patterns = ["*.docx"]
                }
            ]
        });

        var file = files.FirstOrDefault();
        if (file is not null)
        {
            ViewModel?.SetTemplateDocxPath(file.Path.LocalPath);
        }
    }

    private async void PickProjectImageTemplateDir_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择工程图模板目录",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            ViewModel?.SetProjectImageTemplateDir(folder.Path.LocalPath);
        }
    }

    private async void OpenConfigWindow_Click(object? sender, RoutedEventArgs e)
    {
        var window = new ConfigWindow
        {
            DataContext = ViewModel
        };

        await window.ShowDialog(this);
    }

    private void CheckAllProjects_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.SetAllProjectsChecked(true);
    }

    private void UncheckAllProjects_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.SetAllProjectsChecked(false);
    }

    private async void RunProjectRow_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.RunProjectFromQueueAsync((sender as Control)?.DataContext as ProjectListItemViewModel);
    }

    private async void RunProjectRowTranscode_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.RunProjectStepFromQueueAsync((sender as Control)?.DataContext as ProjectListItemViewModel, "transcode", "视频转码");
    }

    private async void RunProjectRowMaterialPipeline_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.RunProjectMaterialFromQueueAsync((sender as Control)?.DataContext as ProjectListItemViewModel);
    }

    private async void RunProjectRowWeixinUpload_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.RunProjectStepFromQueueAsync((sender as Control)?.DataContext as ProjectListItemViewModel, "weixin-upload", "微信上传剧集");
    }

    private async void RunProjectRowMaterialUpload_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.RunProjectStepFromQueueAsync((sender as Control)?.DataContext as ProjectListItemViewModel, "weixin-material-upload", "微信上传素材");
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

    private void CloseTaskQueueDetail_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.CloseTaskQueueNodeDetail();
    }

    private async void RunSelectedDownloadDetail_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null)
        {
            return;
        }

        await ViewModel.RunProjectStepFromQueueAsync(ViewModel.SelectedProject, "download", "下载剧集");
    }

    private async void RunSelectedRewriteDetail_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null)
        {
            return;
        }

        await ViewModel.RunProjectStepFromQueueAsync(ViewModel.SelectedProject, "rewrite", "仿写剧名简介");
    }

    private async void RunSelectedPosterDetail_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null)
        {
            return;
        }

        await ViewModel.RunProjectStepFromQueueAsync(ViewModel.SelectedProject, "poster-rename", "生成海报图片");
    }

    private async void RunSelectedProjectImageDetail_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null)
        {
            return;
        }

        await ViewModel.RunProjectStepFromQueueAsync(ViewModel.SelectedProject, "project-image", "生成工程图");
    }

    private async void RunSelectedCostReportDetail_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null)
        {
            return;
        }

        await ViewModel.RunProjectStepFromQueueAsync(ViewModel.SelectedProject, "cost-report", "生成成本报表");
    }

    private async void RunSelectedBatchRenameDetail_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null)
        {
            return;
        }

        await ViewModel.RunProjectStepFromQueueAsync(ViewModel.SelectedProject, "batch-file-rename", "重命名视频文件");
    }

    private async void RunSelectedMaterialConvertDetail_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null)
        {
            return;
        }

        await ViewModel.RunProjectStepFromQueueAsync(ViewModel.SelectedProject, "material-convert", "转换素材视频");
    }

    private async void RenameSelectedProjectTitle_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null)
        {
            return;
        }

        var window = new RenameProjectTitleWindow
        {
            OriginalTitle = ViewModel.SelectedProject.OriginalTitle,
            CurrentTitle = ViewModel.SelectedProject.DisplayName,
            NewTitle = ViewModel.SelectedProject.DisplayName
        };

        var result = await window.ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(result))
        {
            await ViewModel.UpdateSelectedProjectTitleAsync(result);
        }
    }

    private async void ArchiveSelectedProject_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null)
        {
            return;
        }

        var preserveEpisodes = await ResolveArchivePreserveEpisodesAsync([ViewModel.SelectedProject]);
        if (preserveEpisodes is null && ViewModel.SelectedProject is not null &&
            !string.Equals(ViewModel.SelectedProject.MaterialUploadStepStatus, "已完成", StringComparison.Ordinal))
        {
            return;
        }

        await ViewModel.ArchiveSelectedProjectWithOptionsAsync(preserveEpisodes);
    }

    private async void ArchiveCheckedProjects_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
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
        var needsPrompt = projects.Any(item => !string.Equals(item.MaterialUploadStepStatus, "已完成", StringComparison.Ordinal));
        if (!needsPrompt)
        {
            return Array.Empty<int>();
        }

        var window = new ArchiveMaterialPromptWindow();
        var result = await window.ShowDialog<string?>(this);
        return result switch
        {
            "keep" => new[] { 2, 3, 4 },
            "delete" => Array.Empty<int>(),
            _ => null
        };
    }

    private async void DeleteArchivedProject_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedArchivedProject is null)
        {
            return;
        }

        var window = new DeleteArchivedProjectWindow
        {
            DisplayName = ViewModel.SelectedArchivedProject.DisplayName,
            ArchiveProjectDir = ViewModel.SelectedArchivedProject.ArchiveProjectDir
        };

        var confirmed = await window.ShowDialog<bool?>(this);
        if (confirmed == true)
        {
            await ViewModel.DeleteSelectedArchivedProjectAsync();
        }
    }

    private async void DeleteArchivedProjectRow_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var item = (sender as Control)?.DataContext as ArchivedProjectItem;
        if (item is null)
        {
            return;
        }

        var window = new DeleteArchivedProjectWindow
        {
            DisplayName = item.DisplayName,
            ArchiveProjectDir = item.ArchiveProjectDir
        };

        var confirmed = await window.ShowDialog<bool?>(this);
        if (confirmed == true)
        {
            await ViewModel.DeleteArchivedProjectAsync(item);
        }
    }

    private void OpenArchivedProjectRow_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.OpenArchivedProjectDir((sender as Control)?.DataContext as ArchivedProjectItem);
    }

    private void OpenArchivedSourceRow_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.OpenArchivedSourceDir((sender as Control)?.DataContext as ArchivedProjectItem);
    }

    private void OpenArchivedWorkflowRow_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.OpenArchivedWorkflowDir((sender as Control)?.DataContext as ArchivedProjectItem);
    }

    private async void DeleteCheckedArchivedProjects_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var targets = ViewModel.ArchivedProjects.Where(item => item.IsChecked).ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        var window = new DeleteArchivedProjectWindow
        {
            DisplayName = $"已勾选 {targets.Length} 个归档项目",
            ArchiveProjectDir = string.Join(Environment.NewLine, targets.Select(item => item.ArchiveProjectDir))
        };

        var confirmed = await window.ShowDialog<bool?>(this);
        if (confirmed == true)
        {
            await ViewModel.DeleteCheckedArchivedProjectsAsync();
        }
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

    private void MarkMaterialUploadCompleted_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.MarkMaterialUploadCompleted();
    }

    private async void FixMaterialValidationIssue_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.FixMaterialValidationIssueAsync((sender as Control)?.DataContext as MaterialValidationIssueItem);
    }

    private async void FixAllMaterialValidationIssues_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.FixAllMaterialValidationIssuesForSelectedProjectAsync();
    }
}
