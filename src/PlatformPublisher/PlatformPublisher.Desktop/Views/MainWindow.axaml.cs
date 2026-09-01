using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PlatformPublisher.Desktop.ViewModels;
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
}
