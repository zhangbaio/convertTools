using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PlatformPublisher.Desktop.ViewModels;

namespace PlatformPublisher.Desktop.Views;

public partial class WeixinArchivedProjectsView : UserControl
{
    public WeixinArchivedProjectsView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void OnPickRootDirectoryClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || ViewModel is null) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择视频号工作根目录",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        ViewModel.ArchiveRootDirectory = folders[0].Path.LocalPath;
        ViewModel.RefreshArchivedProjects();
    }

    private void OnOpenArchiveClick(object? sender, RoutedEventArgs e) => OpenDirectory(ViewModel?.SelectedArchivedProject?.ArchiveProjectDirectory);
    private void OnOpenSourceClick(object? sender, RoutedEventArgs e) => OpenDirectory(ViewModel?.SelectedArchivedProject?.ArchivedSourceDirectory);
    private void OnOpenWorkflowClick(object? sender, RoutedEventArgs e) => OpenDirectory(ViewModel?.SelectedArchivedProject?.ArchivedWorkflowDirectory);

    private static void OpenDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }
}
