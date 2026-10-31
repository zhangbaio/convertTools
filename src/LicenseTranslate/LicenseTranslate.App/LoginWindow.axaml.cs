using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LicenseTranslate.Auth;

namespace LicenseTranslate.App;

public partial class LoginWindow : Window
{
    private readonly bool _skipRestore;

    public LoginWindow() : this(null) { }

    public LoginWindow(string? message, bool skipRestore = false)
    {
        _skipRestore = skipRestore;
        InitializeComponent();
        MessageText.Text = string.IsNullOrWhiteSpace(message)
            ? "请登录后再翻译执照。这个登录只属于本程序，不会使用云帆的登录状态。"
            : message;
        var settings = ((App)Application.Current!).Settings;
        ServerBox.Text = settings.AuthServerUrl;
        Opened += async (_, _) => await RestoreAsync();
    }

    private async Task RestoreAsync()
    {
        if (_skipRestore) return;
        var app = (App)Application.Current!;
        var session = app.Store.Load();
        if (!session.IsSignedIn() || session.IsExpired() || !string.Equals(session.MachineId, MachineFingerprint.Current(), StringComparison.OrdinalIgnoreCase))
            return;
        if (!session.NeedsVerification())
        {
            app.ShowTranslator(session);
            return;
        }
        StatusText.Text = "正在校验登录…";
        try
        {
            var verified = await app.Auth.VerifyAsync(session);
            app.Store.Save(verified);
            app.ShowTranslator(verified);
        }
        catch (AccountRejectedException ex)
        {
            app.Store.Clear();
            StatusText.Text = ex.Message;
        }
        catch (Exception ex) when (ex is AccountNetworkException or AccountAuthException)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void Login_Click(object? sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current!;
        app.Settings.AuthServerUrl = ServerBox.Text?.Trim() ?? "";
        app.Settings.Save();
        StatusText.Text = "正在登录…";
        try
        {
            var previous = app.Store.Load();
            var session = await app.Auth.LoginAsync(app.Settings.AuthServerUrl, AccountBox.Text ?? "", PasswordBox.Text ?? "");
            if (previous.IsSignedIn() && !string.Equals(previous.Token, session.Token, StringComparison.Ordinal))
            {
                try { await app.Auth.LogoutAsync(previous); }
                catch (AccountAuthException) { }
            }
            app.Store.Save(session);
            app.ShowTranslator(session);
        }
        catch (Exception ex) when (ex is AccountAuthException)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void Exit_Click(object? sender, RoutedEventArgs e)
    {
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
