using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views;

public partial class ConfigWindow : Window
{
    public ConfigWindow()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void PickRootDir_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择工作目录",
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

    private async void PickWeixinSubmissionReportDir_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择提审记录目录",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            ViewModel?.SetWeixinSubmissionReportDir(folder.Path.LocalPath);
        }
    }

    private void ClearWeixinSubmissionReportDir_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.SetWeixinSubmissionReportDir(string.Empty);
    }

    private void SaveAndClose_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.SaveConfigCommand.Execute(null);
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
