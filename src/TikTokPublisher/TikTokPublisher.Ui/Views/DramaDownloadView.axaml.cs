using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class DramaDownloadView : UserControl
{
    private DramaDownloadViewModel? _vm;

    public DramaDownloadView()
    {
        InitializeComponent();
    }

    public void Bind(DramaDownloadViewModel vm, Action<string> log)
    {
        _vm = vm;
        vm.LogRequested += log;
        vm.LoadState();
        DataContext = vm;
        SyncComboFromVm();
    }

    private void SyncComboFromVm()
    {
        if (_vm is null) return;
        foreach (var item in QualityCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), _vm.DefaultQuality, StringComparison.OrdinalIgnoreCase))
                QualityCombo.SelectedItem = item;
        }
        foreach (var item in EpisodeModeCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, _vm.EpisodeNumberMode, StringComparison.OrdinalIgnoreCase))
                EpisodeModeCombo.SelectedItem = item;
        }
    }

    private void OnQualityChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || QualityCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.DefaultQuality = item.Content?.ToString() ?? "1080P";
    }

    private void OnEpisodeModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || EpisodeModeCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.EpisodeNumberMode = item.Tag as string ?? "source";
    }

    private async void OnPickDownloadDirClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择短剧下载目录",
            AllowMultiple = false,
        });
        var folder = folders.FirstOrDefault();
        if (folder is null) return;
        _vm.DownloadWorkspace = folder.Path.LocalPath;
        _vm.SaveState();
    }

    private void OnOpenQueueProjectFolderClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var row = (sender as Control)?.DataContext as DramaQueueRowViewModel;
        _vm?.OpenQueueProjectFolder(row);
    }

    private async void OnSaveSettingsClick(object? sender, RoutedEventArgs e)
    {
        _vm?.SaveState();
        var owner = TopLevel.GetTopLevel(this) as Window;
        await InfoDialog.ShowSaveSuccessAsync(owner, "当前页面配置已保存成功。");
    }
}
