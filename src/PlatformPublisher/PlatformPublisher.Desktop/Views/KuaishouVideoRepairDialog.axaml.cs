using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PlatformPublisher.Desktop.Views;

public partial class KuaishouVideoRepairDialog : Window
{
    public KuaishouVideoRepairDialog() => InitializeComponent();

    public static Task<string?> ShowAsync(Window owner) =>
        new KuaishouVideoRepairDialog().ShowDialog<string?>(owner);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        var text = TitlesTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            TitlesTextBox.Focus();
            return;
        }

        Close(text);
    }
}
