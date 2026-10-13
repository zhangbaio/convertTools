using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PlatformPublisher.Desktop.Views;

public partial class MaterialDownloadDialog : Window
{
    public MaterialDownloadDialog() : this(true, string.Empty) { }

    public MaterialDownloadDialog(bool systemHighlights, string initialValues)
    {
        InitializeComponent();
        IsSystemHighlights = systemHighlights;
        HeadingText.Text = systemHighlights ? "下载系统高光视频" : "按标签发现并下载";
        DescriptionText.Text = systemHighlights
            ? "输入剧名，程序将进入剧集管理读取并下载系统生成的高光视频。"
            : "输入标签或完整描述，程序将在视频管理中搜索并下载匹配素材。";
        ValuesTextBox.Watermark = systemHighlights ? "每行一个剧名" : "每行一个标签或完整描述";
        ValuesTextBox.Text = initialValues;
        LimitLabel.IsVisible = systemHighlights;
        LimitInput.IsVisible = systemHighlights;
    }

    public bool IsSystemHighlights { get; }
    public IReadOnlyList<string> Values => (ValuesTextBox.Text ?? string.Empty)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
    public int Limit => Math.Clamp((int)(LimitInput.Value ?? 10), 1, 50);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        if (Values.Count == 0)
        {
            ValidationText.Text = IsSystemHighlights ? "请至少填写一个剧名。" : "请至少填写一个标签或完整描述。";
            ValuesTextBox.Focus();
            return;
        }
        Close(true);
    }
}
