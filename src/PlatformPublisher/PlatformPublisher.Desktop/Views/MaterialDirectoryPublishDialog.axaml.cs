using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using PlatformPublisher.Weixin.Publishing;

namespace PlatformPublisher.Desktop.Views;

public partial class MaterialDirectoryPublishDialog : Window
{
    private WeixinDirectoryMaterialPublishService? _service;
    private readonly ObservableCollection<DirectoryDraftItemViewModel> _items = [];

    public MaterialDirectoryPublishDialog()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _items;
    }

    public MaterialDirectoryPublishDialog(WeixinDirectoryMaterialPublishService service, string initialDirectory)
        : this()
    {
        _service = service;
        WorkspaceTextBox.Text = initialDirectory;
        if (Directory.Exists(initialDirectory)) _ = ScanAsync();
    }

    public MaterialDirectoryDraftSelection? Selection { get; private set; }

    private async void OnChooseDirectoryClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择目录批量发表工作目录",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        WorkspaceTextBox.Text = folders[0].Path.LocalPath;
        await ScanAsync();
    }

    private async void OnScanClick(object? sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        try
        {
            ScanButton.IsEnabled = false;
            StatusText.Text = "正在扫描一级子目录…";
            var directory = WorkspaceTextBox.Text?.Trim() ?? string.Empty;
            if (_service is null) throw new InvalidOperationException("目录素材服务尚未初始化。");
            var result = await Task.Run(() => _service.Scan(directory));
            _items.Clear();
            foreach (var item in result)
                _items.Add(new DirectoryDraftItemViewModel(item.VideoPath, item.Description));
            StatusText.Text = $"扫描完成：发现 {_items.Count} 条素材。";
            AcceptButton.IsEnabled = _items.Count > 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = "扫描失败：" + ex.Message;
            AcceptButton.IsEnabled = false;
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private async void OnChooseCoverClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DirectoryDraftItemViewModel item) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择素材封面",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp"] }],
        });
        if (files.Count > 0) item.CoverPath = files[0].Path.LocalPath;
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        var selected = _items.Where(item => item.IsEnabled).ToArray();
        if (selected.Length == 0)
        {
            StatusText.Text = "请至少启用一条素材。";
            return;
        }
        Selection = new MaterialDirectoryDraftSelection(
            WorkspaceTextBox.Text?.Trim() ?? string.Empty,
            selected.Select(item => new MaterialDirectoryDraftItem(item.VideoPath, item.Description, item.CoverPath)).ToArray());
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}

public sealed partial class DirectoryDraftItemViewModel : ObservableObject
{
    public DirectoryDraftItemViewModel(string videoPath, string description)
    {
        VideoPath = videoPath;
        _description = description;
    }

    public string VideoPath { get; }
    public string FileName => Path.GetFileName(VideoPath);
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _coverPath = string.Empty;
}

public sealed record MaterialDirectoryDraftItem(string VideoPath, string Description, string CoverPath);
public sealed record MaterialDirectoryDraftSelection(string WorkspaceDirectory, IReadOnlyList<MaterialDirectoryDraftItem> Items);
