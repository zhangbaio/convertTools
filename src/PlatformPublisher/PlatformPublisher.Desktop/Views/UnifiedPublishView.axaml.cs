using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PlatformPublisher.Desktop.ViewModels;

namespace PlatformPublisher.Desktop.Views;

public partial class UnifiedPublishView : UserControl
{
    public UnifiedPublishView()=>InitializeComponent();
    private UnifiedPublishViewModel? ViewModel=>DataContext as UnifiedPublishViewModel;

    private async void OnSelectDirectoryClick(object? sender,RoutedEventArgs e)
    {
        var storage=TopLevel.GetTopLevel(this)?.StorageProvider;if(storage is null)return;
        var folders=await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions{Title="选择项目或素材目录",AllowMultiple=false});
        if(folders.Count>0&&folders[0].TryGetLocalPath() is{ }path)ViewModel?.SetWorkflowDirectory(path);
    }

    private async void OnSelectFilesClick(object? sender,RoutedEventArgs e)
    {
        var storage=TopLevel.GetTopLevel(this)?.StorageProvider;if(storage is null)return;
        var files=await storage.OpenFilePickerAsync(new FilePickerOpenOptions{Title="选择待发布视频",AllowMultiple=true,FileTypeFilter=[new FilePickerFileType("视频文件"){Patterns=["*.mp4","*.mov","*.m4v","*.mkv","*.avi","*.webm"]}]});
        ViewModel?.SetSelectedFiles(files.Select(item=>item.TryGetLocalPath()).Where(path=>path is not null)!);
    }
}
