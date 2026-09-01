using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PlatformPublisher.Desktop.ViewModels;

namespace PlatformPublisher.Desktop.Views;

public partial class WeixinDramaDownloadView : UserControl
{
    public WeixinDramaDownloadView() => InitializeComponent();
    private WeixinDramaDownloadViewModel? ViewModel => DataContext as WeixinDramaDownloadViewModel;

    private async void OnPickRootClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || ViewModel is null) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择短剧下载工作根目录",
            AllowMultiple = false,
        });
        if (folders.Count > 0) ViewModel.RootDirectory = folders[0].Path.LocalPath;
    }

    private void OnCheckAllClick(object? sender, RoutedEventArgs e) => ViewModel?.CheckAll(true);
    private void OnUncheckAllClick(object? sender, RoutedEventArgs e) => ViewModel?.CheckAll(false);
}
