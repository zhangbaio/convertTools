using Avalonia.Controls;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class AccountProfilesView : UserControl
{
    public event EventHandler? LoginRequested;
    public event EventHandler? ReloginRequested;
    public event EventHandler? LogoutRequested;

    public AccountProfilesView()
    {
        InitializeComponent();
        Editor.LoginRequested += (_, _) => LoginRequested?.Invoke(this, EventArgs.Empty);
        Editor.ReloginRequested += (_, _) => ReloginRequested?.Invoke(this, EventArgs.Empty);
        Editor.LogoutRequested += (_, _) => LogoutRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Bind(MainViewModel vm) => Editor.Bind(vm);

    public void RefreshSelectedAccount() => Editor.RefreshSelectedAccount();
}
