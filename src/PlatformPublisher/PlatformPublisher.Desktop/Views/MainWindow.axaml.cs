using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PlatformPublisher.Desktop.ViewModels;

namespace PlatformPublisher.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void PickProjectDirectory_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择短剧项目目录",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && ViewModel is not null)
            ViewModel.DraftProjectDirectory = folders[0].Path.LocalPath;
    }

    private async void PickConfigFile_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择视频号自动化配置",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON 配置") { Patterns = ["*.json"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count > 0 && ViewModel is not null)
            ViewModel.DraftConfigPath = files[0].Path.LocalPath;
    }
}
