using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace ShortDrama.Desktop.Views;

public partial class MaterialDirectoryPublishWindow : Window
{
    public MaterialDirectoryPublishWindow()
        : this(string.Empty)
    {
    }

    public MaterialDirectoryPublishWindow(string initialDirectory)
    {
        InitializeComponent();

        WorkspaceTextBox.Text = initialDirectory ?? string.Empty;
        BrowseButton.Click += BrowseButton_Click;
        StartButton.Click += StartButton_Click;
        CancelButton.Click += (_, _) => Close(false);
    }

    public string WorkspacePath => WorkspaceTextBox.Text?.Trim() ?? string.Empty;

    public bool HideLocation => HideLocationCheckBox.IsChecked == true;

    public bool DeclareOriginal => DeclareOriginalCheckBox.IsChecked == true;

    public bool AiRewriteDescription => AiRewriteDescriptionCheckBox.IsChecked == true;

    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择目录批量发表工作目录",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            WorkspaceTextBox.Text = folder.Path.LocalPath;
            ValidationMessageTextBlock.Text = string.Empty;
        }
    }

    private void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        var path = WorkspacePath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ValidationMessageTextBlock.Text = "请选择一个存在的工作目录。";
            return;
        }

        Close(true);
    }
}
