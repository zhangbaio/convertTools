using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;
using PlatformPublisher.Weixin.Publishing;
using TikTokPublisher.Ui.ViewModels;

namespace PlatformPublisher.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => ShowWeixinPage();
    }

    public void BindSettings(SystemSettingsViewModel viewModel)
    {
        SharedSettingsView.Bind(viewModel);
        viewModel.Load();
    }

    public void BindWeixinSeries(IPlatformPublishAdapter adapter)
    {
        var seriesView = new WeixinSeriesUploadView();
        seriesView.Bind(adapter, () => WeixinPublisherView.SelectedAccountProfile);
        WeixinPublisherView.SetSeriesPublishContent(seriesView);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnWeixinNavClick(object? sender, RoutedEventArgs e) => ShowWeixinPage();

    private void OnKuaishouPersonalNavClick(object? sender, RoutedEventArgs e) =>
        ShowKuaishouPage(PublishPlatform.KuaishouPersonalRevenue);

    private void OnKuaishouEnterpriseNavClick(object? sender, RoutedEventArgs e) =>
        ShowKuaishouPage(PublishPlatform.KuaishouEnterpriseRevenue);

    private void OnSettingsNavClick(object? sender, RoutedEventArgs e) => ShowSettingsPage();

    private void ShowWeixinPage()
    {
        ViewModel?.SelectPlatform(PublishPlatform.WeixinChannel);
        WeixinPublisherView.IsVisible = true;
        PipelinePage.IsVisible = false;
        SharedSettingsView.IsVisible = false;
        SetActiveNavigation(WeixinNavButton);
    }

    private void ShowKuaishouPage(PublishPlatform platform)
    {
        ViewModel?.SelectPlatform(platform);
        WeixinPublisherView.IsVisible = false;
        PipelinePage.IsVisible = true;
        PipelineContent.IsVisible = true;
        SharedSettingsView.IsVisible = false;
        SetActiveNavigation(platform == PublishPlatform.KuaishouPersonalRevenue
            ? KuaishouPersonalNavButton
            : KuaishouEnterpriseNavButton);
    }

    private void ShowSettingsPage()
    {
        WeixinPublisherView.IsVisible = false;
        PipelinePage.IsVisible = true;
        PipelineContent.IsVisible = false;
        SharedSettingsView.IsVisible = true;
        SetActiveNavigation(SettingsNavButton);
    }

    private void SetActiveNavigation(Button activeButton)
    {
        foreach (var button in new[]
                 {
                     WeixinNavButton,
                     KuaishouPersonalNavButton,
                     KuaishouEnterpriseNavButton,
                     SettingsNavButton,
                 })
        {
            SetActiveNav(button, ReferenceEquals(button, activeButton));
        }
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
