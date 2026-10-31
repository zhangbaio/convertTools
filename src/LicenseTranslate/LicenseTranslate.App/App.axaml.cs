using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LicenseTranslate.Auth;

namespace LicenseTranslate.App;

public partial class App : Application
{
    public AccountStore Store { get; } = new(AppContext.BaseDirectory);
    public AccountAuthClient Auth { get; } = new();
    public TranslateAppSettings Settings { get; private set; } = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Settings = TranslateAppSettings.Load();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ShowLogin();
        }
        base.OnFrameworkInitializationCompleted();
    }

    public void ShowLogin(string? message = null, bool skipRestore = false)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        var previous = desktop.MainWindow;
        var login = new LoginWindow(message, skipRestore);
        desktop.MainWindow = login;
        login.Show();
        if (previous is not null && !ReferenceEquals(previous, login))
            previous.Close();
    }

    public void ShowTranslator(AccountSession session)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        var previous = desktop.MainWindow;
        var window = new TranslateWindow(session);
        desktop.MainWindow = window;
        window.Show();
        if (previous is not null && !ReferenceEquals(previous, window))
            previous.Close();
    }
}
