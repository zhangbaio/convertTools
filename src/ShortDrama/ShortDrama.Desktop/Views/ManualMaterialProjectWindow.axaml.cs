using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ShortDrama.Desktop.Services;

namespace ShortDrama.Desktop.Views;

public partial class ManualMaterialProjectWindow : Window
{
    private readonly ManualMaterialProjectService _service;
    private readonly string _workspaceRoot;

    public ManualMaterialProjectWindow(
        string workspaceRoot,
        ManualMaterialProjectService service)
    {
        _workspaceRoot = workspaceRoot;
        _service = service;
        InitializeComponent();

        BrowseButton.Click += BrowseButton_Click;
        CancelButton.Click += (_, _) => Close(false);
        CreateButton.Click += CreateButton_Click;
    }

    public string? VideoDirectory { get; private set; }
    public string NewTitle => NewTitleTextBox.Text?.Trim() ?? string.Empty;
    public string OriginalTitle => OriginalTitleTextBox.Text?.Trim() ?? string.Empty;
    public int? EpisodeCount => EpisodeCountUpDown.Value is > 0 ? (int?)EpisodeCountUpDown.Value.Value : null;

    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择视频目录",
            AllowMultiple = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(_workspaceRoot)
        });

        var folder = folders.FirstOrDefault();
        if (folder is null)
        {
            return;
        }

        VideoDirectory = folder.Path.LocalPath;
        VideoDirectoryTextBox.Text = VideoDirectory;
        var count = _service.ListVideoFiles(VideoDirectory).Count;
        VideoCountTextBlock.Text = count > 0 ? $"{count} 个视频文件" : "未找到可用视频文件";
        if (string.IsNullOrWhiteSpace(OriginalTitleTextBox.Text))
        {
            OriginalTitleTextBox.Text = Path.GetFileName(VideoDirectory);
        }
    }

    private void CreateButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(VideoDirectory))
        {
            VideoDirectoryTextBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(NewTitle))
        {
            NewTitleTextBox.Focus();
            return;
        }

        Close(true);
    }
}
