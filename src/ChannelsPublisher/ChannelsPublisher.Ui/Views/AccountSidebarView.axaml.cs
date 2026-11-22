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
        SidebarFrame.Classes.Remove("standaloneAccountFrame");
        AccountList.Classes.Remove("standaloneAccountList");
        AddAccountButton.Classes.Remove("sidebarPrimaryAction");
        AccountSettingsButton.Classes.Remove("secondaryAction");
    }

    internal void HideAccountRemoval() => RemoveAccountButton.IsVisible = false;

    private void OnAccountSettingsClick(object? sender, RoutedEventArgs e) =>
        AccountSettingsRequested?.Invoke(this, EventArgs.Empty);
}
