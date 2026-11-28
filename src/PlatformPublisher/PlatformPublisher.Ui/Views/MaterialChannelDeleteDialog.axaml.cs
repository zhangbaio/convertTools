using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PlatformPublisher.Desktop.Views;

public partial class MaterialChannelDeleteDialog : Window
{
    public MaterialChannelDeleteDialog() : this(string.Empty) { }

    public MaterialChannelDeleteDialog(string projectTitle)
    {
        InitializeComponent();
        ProjectText.Text = projectTitle;
        KeywordTextBox.Text = projectTitle;
    }

    public string Keyword => KeywordTextBox.Text?.Trim() ?? string.Empty;
    public int DeleteCount => Math.Max(1, (int)(DeleteCountInput.Value ?? 1));

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Keyword))
        {
            KeywordTextBox.Focus();
            return;
        }
        Close(true);
    }
}
