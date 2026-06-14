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

}
