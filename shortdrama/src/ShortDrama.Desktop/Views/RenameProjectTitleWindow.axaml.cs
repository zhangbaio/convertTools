using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ShortDrama.Desktop.Views;

public partial class RenameProjectTitleWindow : Window
{
    public RenameProjectTitleWindow()
    {
        InitializeComponent();
    }

    public string OriginalTitle { get; set; } = string.Empty;
    public string CurrentTitle { get; set; } = string.Empty;
    public string NewTitle { get; set; } = string.Empty;
    public string? ResultTitle { get; private set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        DataContext = this;
        NewTitleTextBox.Focus();
        NewTitleTextBox.SelectionStart = NewTitleTextBox.Text?.Length ?? 0;
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        ResultTitle = (NewTitle ?? string.Empty).Trim();
        Close(ResultTitle);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
