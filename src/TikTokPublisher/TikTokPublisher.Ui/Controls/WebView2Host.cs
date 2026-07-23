using System.Drawing;
using Avalonia;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services.TikTok;
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
    private const int MaxLoggedDialogMessageLength = 120;

    private CoreWebView2Controller? _controller;
    private string? _pendingUrl;
    private string? _lastInitError;
    private string? _lastProcessFailure;
    private bool _renderedVisible;
    private bool _nativeHandleAlive;
    private bool _staleProcessRecoveryAttempted;
    private string? _storageStateInitScriptId;

    public string? LastInitError => _lastInitError;
    public string? LastProcessFailure => _lastProcessFailure;

    /// <summary>本机缺少 Edge WebView2 Runtime 时为 true：内置浏览器无法承载，需引导用户安装运行时。</summary>
    public bool IsRuntimeMissing { get; private set; }

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
        _controller != null && string.IsNullOrWhiteSpace(_lastProcessFailure) && RemoteDebuggingPort > 0
            ? $"http://127.0.0.1:{RemoteDebuggingPort}"
            : null;

    public bool IsEngineReady
    {
        get
        {
            try { return string.IsNullOrWhiteSpace(_lastProcessFailure) && _controller?.CoreWebView2 is not null && RemoteDebuggingPort > 0; }
            catch { return false; }
        }
    }

    public event Action? Ready;
    public event Action<string>? NavigationCompleted;
    public event Action<string>? ProcessFailed;
    /// <summary>初始化时未找到 WebView2 运行时；用于在浏览器区域显示安装引导而非黑屏。</summary>
    public event Action? RuntimeMissing;

    public WebView2Host()
    {
        SizeChanged += (_, _) => UpdateBounds();
    }

    private bool _renderedStateApplied;

    /// <summary>控制 WebView2 是否绘制到屏幕；与 Avalonia <see cref="IsVisible"/> 解耦以支持后台 CDP。</summary>
    public void SetRenderedVisible(bool visible)
    {
        // 幂等：状态未变时跳过 COM 调用（切账号时会对多个 host 重复设置，累积开销明显）。
        if (_renderedStateApplied && _renderedVisible == visible)
            return;
        _renderedVisible = visible;
        _renderedStateApplied = true;
        ApplyRenderedState();
    }

    public void RefreshBounds()
    {
        if (_renderedVisible)
            ApplyRenderedState();
    }

    /// <summary>后台隐藏时的虚拟视口：移到父窗口可视区外但保持 IsVisible=true。
    /// 若用 IsVisible=false 隐藏，WebView2 会暂停渲染帧（rAF），Playwright 的
    /// 「元素稳定」检查依赖连续渲染帧，后台自动化的点击会全部超时失败。</summary>
    private static readonly Rectangle HiddenViewportBounds = new(-20000, 0, 1280, 860);

    private void ApplyRenderedState()
    {
        try
        {
            // 宿主 HWND 已销毁时禁止调用 controller（同步 COM 调用可能挂死 UI 线程）。
            if (_controller is null || !_nativeHandleAlive)
                return;

            _controller.IsVisible = true;
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

    public async Task<int> ImportStorageStateAsync(string authPath)
    {
        var webView = _controller?.CoreWebView2
            ?? throw new InvalidOperationException("内置浏览器尚未初始化完成。");
        if (!File.Exists(authPath))
            return 0;

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(authPath).ConfigureAwait(true));
        var root = document.RootElement;
        var cookieCount = 0;
        if (root.TryGetProperty("cookies", out var cookies) && cookies.ValueKind == JsonValueKind.Array)
        {
            var manager = webView.CookieManager;
            foreach (var item in cookies.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var value = item.TryGetProperty("value", out var valueElement) ? valueElement.GetString() : null;
                var domain = item.TryGetProperty("domain", out var domainElement) ? domainElement.GetString() : null;
                var path = item.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : "/";
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(domain))
                    continue;

                var cookie = manager.CreateCookie(name, value ?? "", domain, string.IsNullOrWhiteSpace(path) ? "/" : path);
                cookie.IsHttpOnly = item.TryGetProperty("httpOnly", out var httpOnly) && httpOnly.ValueKind == JsonValueKind.True;
                cookie.IsSecure = item.TryGetProperty("secure", out var secure) && secure.ValueKind == JsonValueKind.True;
                if (item.TryGetProperty("expires", out var expires) && expires.TryGetDouble(out var epochSeconds) && epochSeconds > 0)
                    cookie.Expires = DateTimeOffset.FromUnixTimeSeconds((long)epochSeconds).UtcDateTime;
                if (item.TryGetProperty("sameSite", out var sameSite))
                {
                    cookie.SameSite = sameSite.GetString()?.Trim().ToLowerInvariant() switch
                    {
                        "strict" => CoreWebView2CookieSameSiteKind.Strict,
                        "lax" => CoreWebView2CookieSameSiteKind.Lax,
                        "none" => CoreWebView2CookieSameSiteKind.None,
                        _ => CoreWebView2CookieSameSiteKind.Lax,
                    };
                }
                manager.AddOrUpdateCookie(cookie);
                cookieCount++;
            }
        }

        var localStorage = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("origins", out var origins) && origins.ValueKind == JsonValueKind.Array)
        {
            foreach (var originItem in origins.EnumerateArray())
            {
                var origin = originItem.TryGetProperty("origin", out var originElement) ? originElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(origin) ||
                    !originItem.TryGetProperty("localStorage", out var entries) ||
                    entries.ValueKind != JsonValueKind.Array)
                    continue;

                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in entries.EnumerateArray())
                {
                    var key = entry.TryGetProperty("name", out var keyElement) ? keyElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    values[key] = entry.TryGetProperty("value", out var storedValue) ? storedValue.GetString() ?? "" : "";
                }
                localStorage[origin] = values;
            }
        }

        if (_storageStateInitScriptId is not null)
            webView.RemoveScriptToExecuteOnDocumentCreated(_storageStateInitScriptId);
        var stateJson = JsonSerializer.Serialize(localStorage);
        _storageStateInitScriptId = await webView.AddScriptToExecuteOnDocumentCreatedAsync(
            "(() => { const states = " + stateJson +
            "; const values = states[location.origin]; if (!values) return; " +
            "for (const [key, value] of Object.entries(values)) { try { localStorage.setItem(key, value); } catch {} } })();")
            .ConfigureAwait(true);

        Log($"imported storage_state udf={UserDataFolder} cookies={cookieCount} origins={localStorage.Count}");
        return cookieCount;
    }

    public void CloseBrowser()
    {
        try { _controller?.Close(); } catch { /* ignore */ }
        _controller = null;
        _pendingUrl = null;
        _lastProcessFailure = null;
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
            _lastProcessFailure = null;
            if (!_staleProcessRecoveryAttempted)
            {
                _staleProcessRecoveryAttempted = true;
                WebView2ProcessRecovery.RecoverStaleProcess(UserDataFolder, Log);
            }
            var options = new CoreWebView2EnvironmentOptions();
            var browserArgs = new List<string>
            {
                // 部分新装 Windows 机器的显卡驱动/WebView2 GPU 合成会只显示黑色原生窗口。
                // 内置浏览器以稳定登录和后台自动化为主，使用软件渲染可跨机器可靠显示。
                "--disable-gpu",
                // 后台上传依赖持续的定时器与渲染帧；禁用 Chromium 的后台/遮挡节流。
                "--disable-background-timer-throttling",
                "--disable-backgrounding-occluded-windows",
                "--disable-renderer-backgrounding",
                "--disable-blink-features=AutomationControlled",
                $"--user-agent={QuoteChromiumArgumentValue(PlaywrightBrowserRuntime.DesktopChromeUserAgent)}",
            };
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
                WebView2ProcessRecovery.SaveMarker(UserDataFolder, _controller.CoreWebView2.BrowserProcessId);
                _controller.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
                _controller.CoreWebView2.ScriptDialogOpening += OnScriptDialogOpening;

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
                    else
                        Log($"navigation-failed udf={UserDataFolder} port={RemoteDebuggingPort} " +
                            $"status={args.WebErrorStatus} uri={_controller.CoreWebView2.Source}");
                };
                _controller.CoreWebView2.ProcessFailed += (_, args) =>
                {
                    var message = $"WebView2 进程异常：{args.ProcessFailedKind}";
                    _lastProcessFailure = message;
                    Log($"process-failed udf={UserDataFolder} port={RemoteDebuggingPort} :: {message}");
                    ProcessFailed?.Invoke(message);
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
        catch (WebView2RuntimeNotFoundException ex)
        {
            // 新装机器常见：未安装 Edge WebView2 Runtime，CreateAsync 直接抛此异常，
            // 原生宿主窗口只剩黑屏。标记状态并通知 UI 显示安装引导。
            IsRuntimeMissing = true;
            _lastInitError = "未检测到 WebView2 运行时（Microsoft Edge WebView2 Runtime），内置浏览器无法显示。";
            Log($"FAILED udf={UserDataFolder} port={RemoteDebuggingPort} :: runtime-missing :: {ex.Message}");
            RuntimeMissing?.Invoke();
        }
        catch (Exception ex)
        {
            _lastInitError = $"{ex.GetType().Name}: {ex.Message}";
            Log($"FAILED udf={UserDataFolder} port={RemoteDebuggingPort} :: {_lastInitError}");
        }
    }

    private void OnScriptDialogOpening(object? sender, CoreWebView2ScriptDialogOpeningEventArgs args)
    {
        try
        {
            if (IsLeaveSiteDialog(args))
            {
                args.Accept();
                Log($"auto-accepted leave-site dialog udf={UserDataFolder} port={RemoteDebuggingPort} kind={args.Kind} uri={args.Uri}");
                return;
            }

            if (args.Kind == CoreWebView2ScriptDialogKind.Alert)
            {
                args.Accept();
                Log($"auto-accepted alert dialog udf={UserDataFolder} port={RemoteDebuggingPort} message={TrimDialogMessage(args.Message)}");
                return;
            }

            // With default dialogs disabled, not accepting confirm/prompt is the safe equivalent of Cancel.
            Log($"auto-canceled script dialog udf={UserDataFolder} port={RemoteDebuggingPort} kind={args.Kind} message={TrimDialogMessage(args.Message)}");
        }
        catch (Exception ex)
        {
            Log($"script-dialog handler failed udf={UserDataFolder} port={RemoteDebuggingPort} :: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsLeaveSiteDialog(CoreWebView2ScriptDialogOpeningEventArgs args)
    {
        if (args.Kind == CoreWebView2ScriptDialogKind.Beforeunload)
            return true;

        var message = args.Message ?? "";
        return message.Contains("是否离开网站", StringComparison.Ordinal) ||
               message.Contains("更改可能未保存", StringComparison.Ordinal) ||
               message.Contains("Changes you made may not be saved", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Leave site", StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteChromiumArgumentValue(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string TrimDialogMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "";

        var normalized = message.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= MaxLoggedDialogMessageLength
            ? normalized
            : normalized[..MaxLoggedDialogMessageLength] + "...";
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

        try
        {
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
        catch (InvalidOperationException ex)
        {
            // WebView2 controller may be disposed asynchronously while Avalonia is still
            // completing a layout pass. A stale bounds update must not terminate the app.
            _lastProcessFailure = $"WebView2 控制器已失效：{ex.Message}";
            Log($"bounds-update-skipped udf={UserDataFolder} port={RemoteDebuggingPort} :: {_lastProcessFailure}");
            try { _controller?.Close(); } catch { /* ignore */ }
            _controller = null;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            _lastProcessFailure = $"WebView2 控制器状态异常：0x{ex.HResult:X8}";
            Log($"bounds-update-skipped udf={UserDataFolder} port={RemoteDebuggingPort} :: {_lastProcessFailure}");
            try { _controller?.Close(); } catch { /* ignore */ }
            _controller = null;
        }
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
