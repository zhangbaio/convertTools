using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ShortDrama.Desktop.Views;

public partial class ArchiveMaterialPromptWindow : Window
{
    public ArchiveMaterialPromptWindow()
    {
        InitializeComponent();
    }

    public string? Decision { get; private set; }

    private void KeepEpisodes_Click(object? sender, RoutedEventArgs e)
    {
        Decision = "keep";
        Close(Decision);
    }

    private void DeleteAll_Click(object? sender, RoutedEventArgs e)
    {
        Decision = "delete";
        Close(Decision);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Decision = null;
        Close(null);
    }
}
