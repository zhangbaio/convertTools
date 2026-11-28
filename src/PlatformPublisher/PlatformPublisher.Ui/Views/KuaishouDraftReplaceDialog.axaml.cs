using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PlatformPublisher.Desktop.Views;

public partial class KuaishouDraftReplaceDialog : Window
{
    public KuaishouDraftReplaceDialog() => InitializeComponent();

    public static Task<string?> ShowAsync(Window owner) =>
        new KuaishouDraftReplaceDialog().ShowDialog<string?>(owner);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        var text = SeriesIdBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            SeriesIdBox.Focus();
            return;
        }

        Close(text);
    }
}
