using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Weixin.Publishing;
using TikTokPublisher.Ui.ViewModels;

namespace PlatformPublisher.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    public void BindSettings(SystemSettingsViewModel viewModel)
    {
        SharedSettingsView.Bind(viewModel);
        viewModel.Load();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnPipelineNavClick(object? sender, RoutedEventArgs e) => ShowPage(showSettings: false);

    private void OnSettingsNavClick(object? sender, RoutedEventArgs e) => ShowPage(showSettings: true);

    private void ShowPage(bool showSettings)
    {
        PipelineContent.IsVisible = !showSettings;
        SharedSettingsView.IsVisible = showSettings;
        SetActiveNav(PipelineNavButton, !showSettings);
        SetActiveNav(SettingsNavButton, showSettings);
    }

    private static void SetActiveNav(Button button, bool active)
    {
        if (active)
            button.Classes.Add("navActive");
        else
            button.Classes.Remove("navActive");
    }

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

    private async void PickCustomVideoFiles_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
        if (files.Count == 0 || ViewModel is null)
            return;

        var paths = files.Select(file => file.Path.LocalPath).ToArray();
        ViewModel.DraftCustomVideoFilesText = string.Join(Environment.NewLine, paths);
        if (!Directory.Exists(ViewModel.DraftProjectDirectory))
            ViewModel.DraftProjectDirectory = Path.GetDirectoryName(paths[0]) ?? string.Empty;
    }

    private async void OpenWeixinPublishConfig_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        var draftJob = new PublishJob
        {
            PlatformOptionsJson = ViewModel.DraftPlatformOptionsJson,
            PublishDescription = ViewModel.DraftPublishDescription,
            DeclareOriginal = ViewModel.DraftDeclareOriginal,
            HideLocation = ViewModel.DraftHideLocation,
        };
        var result = await WeixinPublishConfigDialog.ShowAsync(this, WeixinPublishOptions.FromJob(draftJob));
        if (result is null)
            return;

        ViewModel.DraftPlatformOptionsJson = result.ToJson();
        ViewModel.DraftPublishDescription = result.DescriptionTemplate;
        ViewModel.DraftDeclareOriginal = result.DeclareOriginal;
        ViewModel.DraftHideLocation = !string.IsNullOrWhiteSpace(result.LocationOptionText);
        ViewModel.StatusMessage = "视频号高级发表配置已应用到新任务。";
    }
}
