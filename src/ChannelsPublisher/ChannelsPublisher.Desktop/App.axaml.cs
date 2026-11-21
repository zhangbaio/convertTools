using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using ChannelsPublisher.Core.Services;
using ChannelsPublisher.Desktop.Views;

namespace ChannelsPublisher.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                var lease = ChannelsWorkspaceLease.Acquire();
                desktop.Exit += (_, _) => lease.Dispose();
                desktop.MainWindow = new MainWindow();
            }
            catch (InvalidOperationException ex)
            {
                desktop.MainWindow = new Window
                {
                    Title = "视频号发布助手",
                    Width = 420,
                    Height = 140,
                    Content = new TextBlock
                    {
                        Margin = new Thickness(20),
                        Text = ex.Message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
