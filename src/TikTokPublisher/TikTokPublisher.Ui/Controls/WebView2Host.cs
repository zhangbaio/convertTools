using System.Drawing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Services;
using Microsoft.Web.WebView2.Core;

namespace TikTokPublisher.Ui.Controls;

/// <summary>把 WebView2（Edge 内核）内嵌进 Avalonia 的原生控件宿主。
///
/// 每实例一个账号：UserDataFolder 隔离登录态；启动带 --remote-debugging-port 暴露 CDP，
/// 供剧集上传经 ConnectOverCDP 驱动内置浏览器中的表单自动化。</summary>
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
    private string? _lastInitError;
    private bool _renderedVisible;
    private bool _nativeHandleAlive;

    public string? LastInitError => _lastInitError;

    public string UserDataFolder { get; set; } = "";
    public int RemoteDebuggingPort { get; set; }
    public string ProxyServer { get; set; } = "";
    public string ProxyUsername { get; set; } = "";
    public string ProxyPassword { get; set; } = "";

    public string? CurrentUrl
    {
        get
        {
            try { return _controller?.CoreWebView2?.Source; }
            catch { return null; }
        }
    }

    public string? CdpEndpoint =>
        _controller != null && RemoteDebuggingPort > 0 ? $"http://127.0.0.1:{RemoteDebuggingPort}" : null;

    public bool IsEngineReady
    {
        get
        {
            try { return _controller?.CoreWebView2 is not null && RemoteDebuggingPort > 0; }
            catch { return false; }
        }
    }

    public event Action? Ready;
    public event Action<string>? NavigationCompleted;

    public WebView2Host()
    {
        SizeChanged += (_, _) => UpdateBounds();
    }

    /// <summary>控制 WebView2 是否绘制到屏幕；与 Avalonia <see cref="IsVisible"/> 解耦以支持后台 CDP。</summary>
    public void SetRenderedVisible(bool visible)
    {
        _renderedVisible = visible;
        ApplyRenderedState();
    }

    public void RefreshBounds()
    {
        if (_renderedVisible)
            ApplyRenderedState();
    }

    /// <summary>后台隐藏时保持的虚拟视口：视口为 0 会让页面布局塌缩，Playwright 点击等可操作性检查全部超时。</summary>
    private static readonly Rectangle HiddenViewportBounds = new(0, 0, 1280, 860);

    private void ApplyRenderedState()
    {
        try
        {
            // 宿主 HWND 已销毁时禁止调用 controller（同步 COM 调用可能挂死 UI 线程）。
            if (_controller is null || !_nativeHandleAlive)
                return;

            _controller.IsVisible = _renderedVisible;
            if (_renderedVisible)
                UpdateBounds();
            else
                _controller.Bounds = HiddenViewportBounds;
        }
        catch { /* ignore */ }
    }

    public Task NavigateAsync(string url) => NavigateOnUiThreadAsync(url);

    public void Navigate(string url)
    {
        if (_controller?.CoreWebView2 != null) _controller.CoreWebView2.Navigate(url);
        else _pendingUrl = url;
    }

    public Task NavigateOnUiThreadAsync(string url)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Navigate(url);
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(() => Navigate(url)).GetTask();
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
        if (!OperatingSystem.IsWindows())
            return handle;

        _nativeHandleAlive = true;
        if (_controller is null)
            _ = InitAsync(handle.Handle);
        else
            ApplyRenderedState();

        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // 切页时 Avalonia 可能销毁原生 HWND；保留 WebView2 进程与 CDP，避免上传前反复冷启动。
        try
        {
            if (_controller is not null && _nativeHandleAlive)
                _controller.IsVisible = false;
        }
        catch { /* ignore */ }

        _nativeHandleAlive = false;
        base.DestroyNativeControlCore(control);
    }

    private async Task InitAsync(IntPtr hwnd)
    {
        try
        {
            _lastInitError = null;
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
            ApplyRenderedState();

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
            _lastInitError = $"{ex.GetType().Name}: {ex.Message}";
            Log($"FAILED udf={UserDataFolder} port={RemoteDebuggingPort} :: {_lastInitError}");
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
        if (_controller is null || !_nativeHandleAlive)
            return;

        if (!_renderedVisible)
        {
            _controller.Bounds = HiddenViewportBounds;
            return;
        }

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var w = Math.Max(0, (int)(Bounds.Width * scaling));
        var h = Math.Max(0, (int)(Bounds.Height * scaling));
        if (w <= 1 || h <= 1)
        {
            // 挂载层折叠成 1×1 时保持虚拟视口，避免布局塌缩破坏后台自动化。
            _controller.Bounds = HiddenViewportBounds;
            return;
        }

        _controller.Bounds = new Rectangle(0, 0, w, h);
        NotifyParentWindowPositionChanged();
    }

    private void NotifyParentWindowPositionChanged()
    {
        try { _controller?.NotifyParentWindowPositionChanged(); }
        catch { /* ignore */ }
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
