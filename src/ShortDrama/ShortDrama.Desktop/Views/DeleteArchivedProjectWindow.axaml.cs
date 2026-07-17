using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ShortDrama.Desktop.Views;

public partial class DeleteArchivedProjectWindow : Window
{
    public DeleteArchivedProjectWindow()
    {
        InitializeComponent();
    }

    public string DisplayName { get; set; } = string.Empty;
    public string ArchiveProjectDir { get; set; } = string.Empty;

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
