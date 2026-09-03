using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PlatformPublisher.Desktop.Views;

public partial class UploadDramaTitlesDialog : Window
{
    public UploadDramaTitlesDialog()=>InitializeComponent();
    public static async Task<string?> ShowAsync(Window owner)=>await new UploadDramaTitlesDialog().ShowDialog<string?>(owner);
    private void OnCancelClick(object? sender,RoutedEventArgs e)=>Close(null);
    private void OnConfirmClick(object? sender,RoutedEventArgs e)
    {
        var text=TitlesTextBox.Text?.Trim();
        if(string.IsNullOrWhiteSpace(text)){TitlesTextBox.Focus();return;}
        Close(text);
    }
}
