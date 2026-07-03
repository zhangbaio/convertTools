using System.Drawing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using TikTokPublisher.Core.Abstractions;
using Microsoft.Web.WebView2.Core;

namespace TikTokPublisher.Ui.Controls;

/// <summary>把 WebView2（Edge 内核）内嵌进 Avalonia 的原生控件宿主。
///
/// 每实例一个账号：UserDataFolder 隔离登录态；启动带 --remote-debugging-port 暴露 CDP，
/// 供 PuppeteerSharp/Playwright ConnectOverCDP 驱动 P1 已验证的发布流程。
/// WebView2=Edge 内核，天生带 H.264/AAC 编解码器且能穿透 wujie 的 Shadow DOM。</summary>
public sealed class WebView2Host : NativeControlHost, IEmbeddedBrowser
{
    private static readonly string InitLog = Path.Combine(Path.GetTempPath(), "webview2-host.log");

    private CoreWebView2Controller? _controller;
    private string? _pendingUrl;

    public string UserDataFolder { get; set; } = "";
    public int RemoteDebuggingPort { get; set; }
    public string ProxyServer { get; set; } = "";
    public string ProxyUsername { get; set; } = "";
    public string ProxyPassword { get; set; } = "";

    public string? CdpEndpoint =>
        _controller != null && RemoteDebuggingPort > 0 ? $"http://127.0.0.1:{RemoteDebuggingPort}" : null;

    /// <summary>WebView2 就绪后回调（可用于把账号标记为在线）。</summary>
    public event Action? Ready;

    public WebView2Host()
    {
        // 控件显隐 → 同步 WebView2 控制器可见性（隐藏账号的 WebView 仍存活，会话/自动化不断）
        this.GetObservable(IsVisibleProperty).Subscribe(new AnonymousObserver<bool>(v =>
        {
            if (_controller != null) _controller.IsVisible = v;
        }));
        SizeChanged += (_, _) => UpdateBounds();
    }

    public Task NavigateAsync(string url)
    {
        Navigate(url);
        return Task.CompletedTask;
    }

    public void Navigate(string url)
    {
        if (_controller?.CoreWebView2 != null) _controller.CoreWebView2.Navigate(url);
        else _pendingUrl = url;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        if (OperatingSystem.IsWindows())
            _ = InitAsync(handle.Handle);
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        try { _controller?.Close(); } catch { /* 忽略关闭异常 */ }
        _controller = null;
        base.DestroyNativeControlCore(control);
    }

    private async Task InitAsync(IntPtr hwnd)
    {
        try
        {
            var options = new CoreWebView2EnvironmentOptions();
            var browserArgs = new List<string>();
            if (RemoteDebuggingPort > 0)
                browserArgs.Add($"--remote-debugging-port={RemoteDebuggingPort}");
            if (!string.IsNullOrWhiteSpace(ProxyServer))
                browserArgs.Add($"--proxy-server={ProxyServer.Trim()}");
            if (browserArgs.Count > 0)
                options.AdditionalBrowserArguments = string.Join(" ", browserArgs);

            var udf = string.IsNullOrWhiteSpace(UserDataFolder) ? null : UserDataFolder;
            var env = await CoreWebView2Environment.CreateAsync(null, udf, options);
            _controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
            _controller.IsVisible = IsVisible;
            UpdateBounds();

            if (_controller.CoreWebView2 is not null &&
                (!string.IsNullOrWhiteSpace(ProxyUsername) || !string.IsNullOrWhiteSpace(ProxyPassword)))
            {
                _controller.CoreWebView2.BasicAuthenticationRequested += (_, args) =>
                {
                    args.Response.UserName = ProxyUsername;
                    args.Response.Password = ProxyPassword;
                };
            }

            Log($"ready udf={udf} port={RemoteDebuggingPort} proxy={ProxyServer} cdp={CdpEndpoint}");
            Ready?.Invoke();

            if (_pendingUrl != null)
            {
                _controller.CoreWebView2.Navigate(_pendingUrl);
                _pendingUrl = null;
            }
        }
        catch (Exception ex)
        {
            Log($"FAILED udf={UserDataFolder} port={RemoteDebuggingPort} :: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void UpdateBounds()
    {
        if (_controller == null) return;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var w = Math.Max(0, (int)(Bounds.Width * scaling));
        var h = Math.Max(0, (int)(Bounds.Height * scaling));
        _controller.Bounds = new Rectangle(0, 0, w, h);
    }

    private static void Log(string message)
    {
        try { File.AppendAllText(InitLog, $"{DateTime.Now:HH:mm:ss} [WebView2] {message}{Environment.NewLine}"); }
        catch { /* 日志失败不影响运行 */ }
    }
}

/// <summary>轻量 IObserver（避免引入 System.Reactive）。</summary>
internal sealed class AnonymousObserver<T> : IObserver<T>
{
    private readonly Action<T> _onNext;
    public AnonymousObserver(Action<T> onNext) => _onNext = onNext;
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void OnNext(T value) => _onNext(value);
}
