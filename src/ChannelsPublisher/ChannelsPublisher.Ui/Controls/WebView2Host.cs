using System.Drawing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using ChannelsPublisher.Core.Abstractions;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;

namespace ChannelsPublisher.Desktop.Controls;

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
    public string StorageStatePath { get; set; } = "";
    public int RemoteDebuggingPort { get; set; }

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
            if (RemoteDebuggingPort > 0)
                options.AdditionalBrowserArguments = $"--remote-debugging-port={RemoteDebuggingPort}";

            var udf = string.IsNullOrWhiteSpace(UserDataFolder) ? null : UserDataFolder;
            var env = await CoreWebView2Environment.CreateAsync(null, udf, options);
            _controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
            _controller.IsVisible = IsVisible;
            UpdateBounds();

            await ImportStorageStateAsync(_controller.CoreWebView2);

            Log($"ready udf={udf} port={RemoteDebuggingPort} cdp={CdpEndpoint}");
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

    private async Task ImportStorageStateAsync(CoreWebView2 webView)
    {
        if (string.IsNullOrWhiteSpace(StorageStatePath) || !File.Exists(StorageStatePath)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(StorageStatePath));
            var root = document.RootElement;
            if (root.TryGetProperty("cookies", out var cookies) && cookies.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in cookies.EnumerateArray()) ImportCookie(webView, value);
            }
            if (root.TryGetProperty("origins", out var origins) && origins.ValueKind == JsonValueKind.Array)
            {
                foreach (var origin in origins.EnumerateArray()) await ImportOriginStorageAsync(webView, origin);
            }
            Log("Imported external Playwright storage state");
        }
        catch (Exception ex)
        {
            Log($"Storage state import failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ImportCookie(CoreWebView2 webView, JsonElement value)
    {
        var name = Text(value, "name");
        var content = Text(value, "value");
        var domain = Text(value, "domain");
        var path = Text(value, "path") ?? "/";
        if (string.IsNullOrWhiteSpace(name) || content is null || string.IsNullOrWhiteSpace(domain)) return;
        var cookie = webView.CookieManager.CreateCookie(name, content, domain, path);
        cookie.IsHttpOnly = Boolean(value, "httpOnly");
        cookie.IsSecure = Boolean(value, "secure");
        if (value.TryGetProperty("expires", out var expires) && expires.TryGetDouble(out var seconds) && seconds > 0)
            cookie.Expires = DateTimeOffset.FromUnixTimeSeconds((long)seconds).DateTime;
        cookie.SameSite = (Text(value, "sameSite") ?? string.Empty).ToLowerInvariant() switch
        {
            "strict" => CoreWebView2CookieSameSiteKind.Strict,
            "none" => CoreWebView2CookieSameSiteKind.None,
            _ => CoreWebView2CookieSameSiteKind.Lax,
        };
        webView.CookieManager.AddOrUpdateCookie(cookie);
    }

    private static async Task ImportOriginStorageAsync(CoreWebView2 webView, JsonElement origin)
    {
        var url = Text(origin, "origin");
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !uri.Host.EndsWith("weixin.qq.com", StringComparison.OrdinalIgnoreCase)) return;
        if (!origin.TryGetProperty("localStorage", out var values) || values.ValueKind != JsonValueKind.Array) return;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, CoreWebView2NavigationCompletedEventArgs __) => completed.TrySetResult();
        webView.NavigationCompleted += Handler;
        try
        {
            webView.Navigate(url);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            foreach (var value in values.EnumerateArray())
            {
                var name = Text(value, "name");
                var content = Text(value, "value");
                if (name is null || content is null) continue;
                await webView.ExecuteScriptAsync($"localStorage.setItem({JsonSerializer.Serialize(name)}, {JsonSerializer.Serialize(content)});");
            }
        }
        catch { /* Cookies remain usable if an origin cannot be seeded. */ }
        finally { webView.NavigationCompleted -= Handler; }
    }

    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static bool Boolean(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False && property.GetBoolean();

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
