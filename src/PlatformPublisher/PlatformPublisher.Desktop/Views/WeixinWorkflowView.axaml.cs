using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ChannelsPublisher.Core.Models;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Weixin.Publishing;

namespace PlatformPublisher.Desktop.Views;

public partial class WeixinWorkflowView : UserControl
{
    private Func<PublishAccount?>? _accountProvider;

    public WeixinWorkflowView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public void Bind(Func<PublishAccount?> accountProvider)
    {
        _accountProvider = accountProvider;
        ApplyAccount(accountProvider());
    }

    public void ApplyAccount(PublishAccount? account)
    {
        ViewModel?.UseWeixinAccount(account?.Id, account?.Name, account?.ProfileDir);
    }

    private void OnRefreshAccountClick(object? sender, RoutedEventArgs e) => ApplyAccount(_accountProvider?.Invoke());

    private async void OnPickProjectDirectoryClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || ViewModel is null) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择视频号项目或素材目录",
            AllowMultiple = false,
        });
        if (folders.Count > 0)
            ViewModel.DraftProjectDirectory = folders[0].Path.LocalPath;
    }

    private async void OnImportLocalProjectsClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || ViewModel is null) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择要导入的视频号短剧项目",
            AllowMultiple = true,
        });
        if (folders.Count > 0)
            await ViewModel.ImportLocalProjectDirectoriesAsync(folders.Select(folder => folder.Path.LocalPath));
    }

    private async void OnPickConfigClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || ViewModel is null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择视频号自动化配置",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON 配置") { Patterns = ["*.json"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count > 0)
            ViewModel.DraftConfigPath = files[0].Path.LocalPath;
    }

    private async void OnPickCustomVideosClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || ViewModel is null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要发表的视频",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("视频文件")
                {
                    Patterns = ["*.mp4", "*.mov", "*.m4v", "*.mkv", "*.avi", "*.flv", "*.ts", "*.wmv", "*.webm"],
                },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count == 0) return;
        var paths = files.Select(file => file.Path.LocalPath).ToArray();
        ViewModel.DraftCustomVideoFilesText = string.Join(Environment.NewLine, paths);
        if (!Directory.Exists(ViewModel.DraftProjectDirectory))
            ViewModel.DraftProjectDirectory = Path.GetDirectoryName(paths[0]) ?? string.Empty;
    }

    private async void OnAdvancedConfigClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null || ViewModel is null) return;
        var draftJob = new PlatformPublisher.Common.Models.PublishJob
        {
            PlatformOptionsJson = ViewModel.DraftPlatformOptionsJson,
            PublishDescription = ViewModel.DraftPublishDescription,
            DeclareOriginal = ViewModel.DraftDeclareOriginal,
            HideLocation = ViewModel.DraftHideLocation,
        };
        var result = await WeixinPublishConfigDialog.ShowAsync(owner, WeixinPublishOptions.FromJob(draftJob));
        if (result is null) return;
        ViewModel.DraftPlatformOptionsJson = result.ToJson();
        ViewModel.DraftPublishDescription = result.DescriptionTemplate;
        ViewModel.DraftDeclareOriginal = result.DeclareOriginal;
        ViewModel.DraftHideLocation = !string.IsNullOrWhiteSpace(result.LocationOptionText);
        ViewModel.StatusMessage = "视频号高级发表配置已应用到新任务。";
    }
}
