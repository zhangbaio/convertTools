using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Controls;

namespace TikTokUploadHeadedSpike;

public sealed class EmbeddedSpikeWindow : Window
{
    private readonly WebView2Host _browser;
    private readonly TextBlock _status = new()
    {
        Text = "正在初始化内置 WebView2…",
        Margin = new Thickness(12, 8),
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
    };

    public EmbeddedSpikeWindow()
    {
        var ctx = SpikeHostContext.Current
            ?? throw new InvalidOperationException("Spike 上下文未初始化");
        var account = ctx.Account;

        _browser = new WebView2Host
        {
            RemoteDebuggingPort = 9224,
            UserDataFolder = account.ProfileDir,
        };
        Directory.CreateDirectory(account.ProfileDir);
        var proxy = TikTokProxyHelper.BuildFromAccount(account);
        if (proxy is not null)
        {
            _browser.ProxyServer = proxy.Server;
            _browser.ProxyUsername = proxy.Username;
            _browser.ProxyPassword = proxy.Password;
        }

        Title = "TikTok 内置浏览器上传测试";
        Width = 1280;
        Height = 900;
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Children =
            {
                _browser,
                _status,
            },
        };
        Grid.SetRow(_status, 1);
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e) => _ = RunAsync(SpikeHostContext.Current!);

    private async Task RunAsync(SpikeHostContext ctx)
    {
        try
        {
            SetStatus("等待内置浏览器 CDP 就绪…");
            if (!await WaitForCdpAsync(_browser, TimeSpan.FromMinutes(2)).ConfigureAwait(true))
                throw new InvalidOperationException("内置浏览器 CDP 超时未就绪");

            await EmbeddedSpikeRunner.RunAsync(_browser, ctx, SetStatus).ConfigureAwait(true);
            ctx.ExitCode = 0;
            SetStatus("✅ 测试完成，见控制台与 embedded-run 日志。");
        }
        catch (Exception ex)
        {
            ctx.ExitCode = 1;
            ctx.ErrorMessage = ex.Message;
            SetStatus($"❌ {ex.Message}");
            Console.Error.WriteLine(ex);
        }
        finally
        {
            if (ctx.Options.AutoClose)
            {
                await Task.Delay(2500).ConfigureAwait(true);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                        desktop.Shutdown(ctx.ExitCode);
                    else
                        Close();
                });
            }
        }
    }

    private void SetStatus(string text) => Dispatcher.UIThread.Post(() => _status.Text = text);

    private static async Task<bool> WaitForCdpAsync(WebView2Host host, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (host.CdpEndpoint is not null)
                return true;
            await Task.Delay(400).ConfigureAwait(false);
        }
        return host.CdpEndpoint is not null;
    }
}
