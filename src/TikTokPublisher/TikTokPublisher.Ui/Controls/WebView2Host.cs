using System.Drawing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Services;
using Microsoft.Web.WebView2.Core;

namespace TikTokPublisher.Ui.Controls;

/// <summary>把 WebView2（Edge 内核）内嵌进 Avalonia 的原生控件宿主。
///
/// 每实例一个账号：UserDataFolder 隔离登录态；启动带 --remote-debugging-port 暴露 CDP，
/// 供 Playwright ConnectOverCDP 驱动发布流程。</summary>
public sealed class WebView2Host : NativeControlHost, IEmbeddedBrowser
{
    private static readonly string InitLog = Path.Combine(Path.GetTempPath(), "webview2-host.log");
    private static readonly string[] CookieSources =
    [
        "https://www.tiktokdramacenter.com",
        "https://tiktokdramacenter.com",
        "https://www.tiktok.com",
    ];

    private CoreWebView2Controller? _controller;
    private string? _pendingUrl;

    public string UserDataFolder { get; set; } = "";
    public int RemoteDebuggingPort { get; set; }
    public string ProxyServer { get; set; } = "";
    public string ProxyUsername { get; set; } = "";
    public string ProxyPassword { get; set; } = "";

    public string? CurrentUrl => _controller?.CoreWebView2?.Source;

    public string? CdpEndpoint =>
        _controller != null && RemoteDebuggingPort > 0 ? $"http://127.0.0.1:{RemoteDebuggingPort}" : null;

    public event Action? Ready;
    public event Action<string>? NavigationCompleted;

    public WebView2Host()
    {
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

    public void Reload()
    {
        try { _controller?.CoreWebView2?.Reload(); }
        catch { /* ignore */ }
    }

    public async Task<string?> ExecuteScriptAsync(string script)
    {
        if (_controller?.CoreWebView2 is null)
            return null;
        try
        {
            return await _controller.CoreWebView2.ExecuteScriptAsync(script).ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<EmbeddedBrowserCookie>> GetCookiesAsync()
    {
        if (_controller?.CoreWebView2 is null)
            return [];

        var manager = _controller.CoreWebView2.CookieManager;
        var byKey = new Dictionary<string, EmbeddedBrowserCookie>(StringComparer.Ordinal);
        foreach (var source in CookieSources)
        {
            try
            {
                var cookies = await manager.GetCookiesAsync(source).ConfigureAwait(true);
                foreach (var cookie in cookies)
                {
                    var converted = ToEmbeddedCookie(cookie);
                    var key = $"{converted.Name}|{converted.Domain}|{converted.Path}";
                    byKey[key] = converted;
                }
            }
            catch
            {
                // 单个域名读取失败时继续
            }
        }

        return byKey.Values.ToList();
    }

    public void CloseBrowser()
    {
        try { _controller?.Close(); } catch { /* ignore */ }
        _controller = null;
        _pendingUrl = null;
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
        CloseBrowser();
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

            if (_controller.CoreWebView2 is not null)
            {
                if (!string.IsNullOrWhiteSpace(ProxyUsername) || !string.IsNullOrWhiteSpace(ProxyPassword))
                {
                    _controller.CoreWebView2.BasicAuthenticationRequested += (_, args) =>
                    {
                        args.Response.UserName = ProxyUsername;
                        args.Response.Password = ProxyPassword;
                    };
                }

                _controller.CoreWebView2.NavigationCompleted += (_, args) =>
                {
                    if (args.IsSuccess)
                        NavigationCompleted?.Invoke(_controller.CoreWebView2.Source);
                };
            }

            Log($"ready udf={udf} port={RemoteDebuggingPort} proxy={ProxyServer} cdp={CdpEndpoint}");
            Ready?.Invoke();

            if (_pendingUrl != null && _controller.CoreWebView2 is not null)
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

    private static EmbeddedBrowserCookie ToEmbeddedCookie(CoreWebView2Cookie cookie)
    {
        long expires = -1;
        try
        {
            if (!cookie.IsSession)
                expires = new DateTimeOffset(cookie.Expires).ToUnixTimeSeconds();
        }
        catch
        {
            expires = -1;
        }

        return new EmbeddedBrowserCookie(
            cookie.Name ?? "",
            cookie.Value ?? "",
            cookie.Domain ?? "",
            string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
            expires,
            cookie.IsHttpOnly,
            cookie.IsSecure,
            cookie.SameSite switch
            {
                CoreWebView2CookieSameSiteKind.Strict => "Strict",
                CoreWebView2CookieSameSiteKind.Lax => "Lax",
                CoreWebView2CookieSameSiteKind.None => "None",
                _ => "Lax",
            });
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
        catch { /* ignore */ }
    }
}

internal sealed class AnonymousObserver<T> : IObserver<T>
{
    private readonly Action<T> _onNext;
    public AnonymousObserver(Action<T> onNext) => _onNext = onNext;
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void OnNext(T value) => _onNext(value);
}
