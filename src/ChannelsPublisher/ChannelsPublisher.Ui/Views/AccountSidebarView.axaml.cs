using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ChannelsPublisher.Desktop.Views;

public partial class AccountSidebarView : UserControl
{
    public event EventHandler? AccountSettingsRequested;

    public AccountSidebarView()
    {
        InitializeComponent();
    }

    public void UseShellChrome()
    {
        SidebarFrame.CornerRadius = new CornerRadius(0);
        SidebarFrame.BorderThickness = new Thickness(0, 0, 1, 0);
    }

    private void OnAccountSettingsClick(object? sender, RoutedEventArgs e) =>
        AccountSettingsRequested?.Invoke(this, EventArgs.Empty);
}
