using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ChannelsPublisher.Core.Services;
using ChannelsPublisher.Desktop.ViewModels;
using ChannelsPublisher.Desktop.Views;

namespace ChannelsPublisher.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // P0 手动组合；后续可切 Microsoft.Extensions.DependencyInjection（已缓存）。
            var store = new AccountStore();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(store),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
