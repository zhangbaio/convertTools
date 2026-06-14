using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ShortDrama.Desktop.Views;

public partial class MaterialChannelVideoDeleteWindow : Window
{
    public MaterialChannelVideoDeleteWindow(string defaultKeyword = "")
    {
        InitializeComponent();
        KeywordTextBox.Text = defaultKeyword;
        CancelButton.Click += (_, _) => Close(false);
        DeleteButton.Click += DeleteButton_Click;
    }

    public string Keyword => KeywordTextBox.Text?.Trim() ?? string.Empty;
    public int DeleteCount => Math.Max(1, (int)(DeleteCountUpDown.Value ?? 1));

    private void DeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Keyword))
        {
            KeywordTextBox.Focus();
            return;
        }

        Close(true);
    }
}
