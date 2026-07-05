using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Controls;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Services;

public sealed record EmbeddedAuthSavedEventArgs(
    AccountItemViewModel Account,
    EmbeddedAuthSaveResult Result);

/// <summary>每账号独立 WebView2，切换仅改可见性（对齐 Python 多账号会话策略）。</summary>
public sealed class BrowserSessionHost
{
    private const int LoginAutofillMaxAttempts = 6;

    private readonly Dictionary<string, WebView2Host> _hosts = new();
    private readonly Dictionary<string, LoginAutofillState> _autofillStates = new();
    private readonly Dictionary<string, bool> _wasOnLoginPage = new();
    private readonly int[] _autofillDelaysMs = [1200, 2200, 3500, 5000, 6500, 8000];
    private Panel? _container;
    private TextBlock? _emptyHint;

    public void Attach(Panel container, TextBlock? emptyHint = null)
    {
        _container = container;
        _emptyHint = emptyHint;
    }

    public void SetEmptyHint(TextBlock? emptyHint) => _emptyHint = emptyHint;

    public IReadOnlyDictionary<string, WebView2Host> Hosts => _hosts;

    public event Action<EmbeddedAuthSavedEventArgs>? AuthSaved;
    public event Action<string>? AuthStatusChanged;
    public event Action<string>? AuthSaveFailed;

    private readonly Dictionary<string, string> _proxyFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private bool _presentationVisible = true;
    private string? _activeAccountId;

    public event Action? PresentationLayoutChanged;

    /// <summary>非浏览器页时隐藏 WebView2 展示（保持会话与 CDP），避免原生 HWND 叠在队列页上。</summary>
    public void SetPresentationVisible(bool visible)
    {
        _presentationVisible = visible;
        foreach (var entry in _hosts)
        {
            var host = entry.Value;
            host.IsVisible = true;
            ApplyHostVisibility(
                host,
                visible && string.Equals(entry.Key, _activeAccountId, StringComparison.Ordinal));
        }

        if (_emptyHint is not null)
            _emptyHint.IsVisible = visible && _hosts.Count == 0;
    }

    private static void ApplyHostVisibility(WebView2Host host, bool rendered)
    {
        // 禁止把原生控件从视觉树移除再添加（会销毁 WebView2 宿主 HWND，
        // 导致后续 CoreWebView2Controller COM 调用挂死 UI 线程）。
        // 同一时刻仅一个 host 以非零 bounds 渲染，无需调整 Z 序。
        if (rendered)
        {
            host.Width = double.NaN;
            host.Height = double.NaN;
            host.Margin = new Avalonia.Thickness(0);
            host.HorizontalAlignment = HorizontalAlignment.Stretch;
            host.VerticalAlignment = VerticalAlignment.Stretch;
            host.ZIndex = 10;
            host.IsVisible = true;
            host.SetRenderedVisible(true);
            Dispatcher.UIThread.Post(host.RefreshBounds, DispatcherPriority.Render);
            Dispatcher.UIThread.Post(host.RefreshBounds, DispatcherPriority.Background);
        }
        else
        {
            host.SetRenderedVisible(false);
            host.Width = 1;
            host.Height = 1;
            host.Margin = new Avalonia.Thickness(-4096, -4096, 0, 0);
            host.HorizontalAlignment = HorizontalAlignment.Left;
            host.VerticalAlignment = VerticalAlignment.Top;
            host.ZIndex = 0;
        }
    }

    private void OnHostReady(string accountId, WebView2Host host)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_presentationVisible)
            {
                host.SetRenderedVisible(false);
                return;
            }

            if (!string.Equals(_activeAccountId, accountId, StringComparison.Ordinal))
            {
                host.SetRenderedVisible(false);
                return;
            }

            ApplyHostVisibility(host, rendered: true);
            PresentationLayoutChanged?.Invoke();
        }, DispatcherPriority.Loaded);
    }

    public void RefreshPresentationBounds()
    {
        foreach (var host in _hosts.Values)
            host.RefreshBounds();
    }

    public void ShowAccount(AccountItemViewModel? account, bool createIfMissing = true)
    {
        if (_container is null) return;

        _activeAccountId = account?.Id;

        foreach (var entry in _hosts)
        {
            var isTarget = account is not null
                           && string.Equals(entry.Key, account.Id, StringComparison.Ordinal);
            if (!isTarget || !_presentationVisible)
                ApplyHostVisibility(entry.Value, rendered: false);
        }

        if (account is null)
        {
            if (_emptyHint is not null)
                _emptyHint.IsVisible = _presentationVisible && _hosts.Count == 0;
            return;
        }

        if (_hosts.TryGetValue(account.Id, out var existing))
        {
            if (_presentationVisible)
            {
                ApplyHostVisibility(existing, rendered: true);
                if (_emptyHint is not null)
                    _emptyHint.IsVisible = false;

                if (!string.IsNullOrWhiteSpace(existing.LastInitError))
                {
                    AuthStatusChanged?.Invoke(
                        $"内置浏览器初始化异常：{existing.LastInitError}，请点「重新登录」或「刷新」");
                }
                else if (existing.IsEngineReady
                         && (string.IsNullOrWhiteSpace(existing.CurrentUrl)
                             || EmbeddedBrowserLoginHelper.IsLoginUrl(existing.CurrentUrl)))
                {
                    existing.Navigate(EmbeddedBrowserLoginHelper.ResolveHomeUrl(account.Model));
                }
            }

            PresentationLayoutChanged?.Invoke();
            return;
        }

        if (!createIfMissing)
        {
            if (_emptyHint is not null)
                _emptyHint.IsVisible = _presentationVisible;
            return;
        }

        var target = GetOrCreateHost(account);
        if (_presentationVisible)
        {
            ApplyHostVisibility(target, rendered: true);
            if (_emptyHint is not null)
                _emptyHint.IsVisible = false;
        }

        PresentationLayoutChanged?.Invoke();
    }

    public void BeginLogin(AccountItemViewModel account, bool forceRelogin = false)
    {
        account.Status = AccountStatus.LoggingIn;
        account.Model.TiktokLoginBrowserMode = "embedded";

        if (forceRelogin)
        {
            _autofillStates.Remove(account.Id);
            _wasOnLoginPage.Remove(account.Id);
        }

        var email = account.Model.ResolveTikTokAccountName();
        var pwd = account.Model.TiktokLoginPassword ?? "";
        if (!string.IsNullOrEmpty(email) || !string.IsNullOrEmpty(pwd))
        {
            _autofillStates[account.Id] = new LoginAutofillState(email, pwd, 0);
        }

        var host = GetOrCreateHost(account);
        ShowAccount(account);
        host.Navigate(MainViewModel.TikTokLoginUrl);
        _wasOnLoginPage[account.Id] = true;
        AuthStatusChanged?.Invoke("请在下方浏览器完成 TikTok 登录");
    }

    public WebView2Host GetOrCreateHost(AccountItemViewModel account) =>
        GetOrCreateHost(account, out _);

    public WebView2Host GetOrCreateHost(AccountItemViewModel account, out bool created)
    {
        created = false;
        if (_hosts.TryGetValue(account.Id, out var existing))
        {
            SyncProxySettings(existing, account.Model);
            if (_hosts.TryGetValue(account.Id, out existing))
                return existing;
        }

        if (_container is null)
            throw new InvalidOperationException("BrowserSessionHost 尚未 Attach 到容器。");

        created = true;
        var host = new WebView2Host
        {
            UserDataFolder = account.Model.ProfileDir,
            RemoteDebuggingPort = AccountBrowserPortAllocator.Allocate(account.Id),
            IsVisible = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };
        ApplyProxySettings(host, account.Model);

        host.Ready += () => OnHostReady(account.Id, host);
        host.NavigationCompleted += url => _ = OnNavigationCompletedAsync(account, url);
        host.ProcessFailed += message => OnHostProcessFailed(account, message);
        _hosts[account.Id] = host;
        _container.Children.Add(host);
        ApplyHostVisibility(
            host,
            rendered: _presentationVisible && string.Equals(_activeAccountId, account.Id, StringComparison.Ordinal));
        host.Navigate(TikTokUrls.DefaultSeriesListUrl);
        return host;
    }

    private void OnHostProcessFailed(AccountItemViewModel account, string message)
    {
        Dispatcher.UIThread.Post(() =>
            AuthStatusChanged?.Invoke($"账号「{account.DisplayName}」内置浏览器已断开：{message}，下次上传会自动重建。"));
    }

    public void InvalidateHostIfNetworkChanged(TikTokAccountProfile account)
    {
        if (!_hosts.TryGetValue(account.Id, out var host))
            return;

        var fingerprint = TikTokProxyHelper.BuildFingerprint(account);
        if (string.Equals(_proxyFingerprints.GetValueOrDefault(account.Id), fingerprint, StringComparison.Ordinal))
            return;

        RemoveHostCore(account.Id);
    }

    public async Task<WebView2Host> RecreateHostAsync(
        AccountItemViewModel account,
        CancellationToken ct = default,
        Action<string>? log = null,
        string? navigateUrl = null)
    {
        var host = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RemoveHostCore(account.Id);
            var created = GetOrCreateHost(account);
            if (!string.IsNullOrWhiteSpace(navigateUrl))
                created.Navigate(navigateUrl);
            if (_presentationVisible && string.Equals(_activeAccountId, account.Id, StringComparison.Ordinal))
                ApplyHostVisibility(created, rendered: true);
            return created;
        }).GetTask().ConfigureAwait(false);

        log?.Invoke($"已重建账号「{account.DisplayName}」内置浏览器会话");
        await Task.Delay(500, ct).ConfigureAwait(false);
        return host;
    }

    private void RemoveHostCore(string accountId)
    {
        if (_hosts.Remove(accountId, out var existing))
        {
            existing.CloseBrowser();
            _container?.Children.Remove(existing);
        }

        AccountBrowserPortAllocator.Release(accountId);
        _proxyFingerprints.Remove(accountId);
    }

    private void ApplyProxySettings(WebView2Host host, TikTokAccountProfile account)
    {
        var proxy = TikTokProxyHelper.BuildFromAccount(account);
        if (proxy is null)
        {
            host.ProxyServer = "";
            host.ProxyUsername = "";
            host.ProxyPassword = "";
            _proxyFingerprints[account.Id] = "direct";
            return;
        }

        host.ProxyServer = proxy.Server;
        host.ProxyUsername = proxy.Username;
        host.ProxyPassword = proxy.Password;
        _proxyFingerprints[account.Id] = TikTokProxyHelper.BuildFingerprint(account);
    }

    private void SyncProxySettings(WebView2Host host, TikTokAccountProfile account)
    {
        var fingerprint = TikTokProxyHelper.BuildFingerprint(account);
        if (string.Equals(_proxyFingerprints.GetValueOrDefault(account.Id), fingerprint, StringComparison.Ordinal))
            return;

        // WebView2 代理在环境创建后不可热更新，需重建会话。
        InvalidateHostIfNetworkChanged(account);
    }

    public WebView2Host? TryGetHost(string accountId) =>
        _hosts.TryGetValue(accountId, out var host) ? host : null;

    public async Task<string> ResetAccountAsync(AccountItemViewModel account, CancellationToken ct = default)
    {
        if (_hosts.Remove(account.Id, out var existing))
        {
            existing.CloseBrowser();
            _container?.Children.Remove(existing);
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        AccountBrowserPortAllocator.Release(account.Id);
        _proxyFingerprints.Remove(account.Id);

        _autofillStates.Remove(account.Id);
        _wasOnLoginPage.Remove(account.Id);
        AccountLoginStatusHelper.DeleteAuthState(account.Model);

        var warning = await TryDeleteProfileDirectoryAsync(account.Model.ProfileDir, ct).ConfigureAwait(false);
        Directory.CreateDirectory(account.Model.ProfileDir);
        account.Status = AccountStatus.Offline;
        return warning;
    }

    public async Task<bool> EnsureReadyAsync(TikTokAccountProfile account, CancellationToken ct)
    {
        var host = TryGetHost(account.Id);
        if (host is null) return false;

        for (var attempt = 0; attempt < 240; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var endpoint = await ReadCdpEndpointOnUiThreadAsync(host).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(endpoint)
                && await IsHostCdpUsableAsync(host, endpoint, ct).ConfigureAwait(false))
                return true;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>为剧集上传准备内置浏览器：创建会话、等待 CDP、校验登录态（默认不切前台）。</summary>
    public async Task<EmbeddedPublishPrepareResult> PrepareForPublishAsync(
        AccountItemViewModel account,
        bool bringToFront = false,
        CancellationToken ct = default,
        Action<string>? log = null)
    {
        account.Model.TiktokLoginBrowserMode = "embedded";
        if (bringToFront)
            ShowAccount(account);

        var host = TryGetHost(account.Id) ?? GetOrCreateHost(account);
        var recreated = false;

        for (var attempt = 0; attempt < 240; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await ReadHostSnapshotOnUiThreadAsync(host).ConfigureAwait(false);
            var initError = await ReadInitErrorOnUiThreadAsync(host).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(snapshot.ProcessFailure))
            {
                if (!recreated)
                {
                    log?.Invoke($"内置浏览器会话异常，正在重建：{snapshot.ProcessFailure}");
                    host = await RecreateHostAsync(account, ct, log, TikTokUrls.DefaultSeriesListUrl)
                        .ConfigureAwait(false);
                    recreated = true;
                    continue;
                }

                return new EmbeddedPublishPrepareResult(
                    false,
                    null,
                    $"内置浏览器会话异常：{snapshot.ProcessFailure}，自动重建后仍未恢复，请在「浏览器」页刷新或重新登录。");
            }

            if (!string.IsNullOrWhiteSpace(initError) && !snapshot.IsEngineReady && attempt >= 3)
            {
                return new EmbeddedPublishPrepareResult(
                    false,
                    null,
                    $"内置浏览器初始化失败：{initError}（详见 %TEMP%\\webview2-host.log）");
            }

            if (!string.IsNullOrEmpty(snapshot.CdpEndpoint)
                && await IsHostCdpUsableAsync(host, snapshot.CdpEndpoint, ct).ConfigureAwait(false))
            {
                // 不写回 account.TiktokFingerprintBrowserCdpEndpoint：那是用户为“外部/指纹浏览器”配置的端点，
                // 内置浏览器的运行时端点通过返回值与 IEmbeddedBrowser.CdpEndpoint 传递即可，写回会污染外部模式配置。
                if (EmbeddedBrowserLoginHelper.IsLoginUrl(snapshot.CurrentUrl))
                {
                    return new EmbeddedPublishPrepareResult(
                        false,
                        snapshot.CdpEndpoint,
                        "账号未登录，请在内置浏览器完成 TikTok 登录后重试");
                }

                return new EmbeddedPublishPrepareResult(true, snapshot.CdpEndpoint, "");
            }

            if (!string.IsNullOrWhiteSpace(snapshot.CdpEndpoint) && attempt == 20 && !recreated)
            {
                log?.Invoke("内置浏览器 CDP 探测不可用，正在重建浏览器会话后重试。");
                host = await RecreateHostAsync(account, ct, log, TikTokUrls.DefaultSeriesListUrl)
                    .ConfigureAwait(false);
                recreated = true;
                continue;
            }

            if (attempt == 0)
                log?.Invoke("正在初始化内置浏览器会话…");
            else if (attempt % 10 == 0)
                log?.Invoke($"等待内置浏览器 CDP 就绪…（{attempt / 2} 秒）");

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        var lastError = await ReadInitErrorOnUiThreadAsync(host).ConfigureAwait(false);
        var suffix = string.IsNullOrWhiteSpace(lastError) ? "" : $" 初始化错误：{lastError}";
        return new EmbeddedPublishPrepareResult(
            false,
            null,
            $"内置浏览器 CDP 未就绪（已等待约 120 秒），请打开「浏览器」页完成登录并等待页面加载完成。{suffix}");
    }

    private static Task<string?> ReadInitErrorOnUiThreadAsync(WebView2Host host) =>
        Dispatcher.UIThread.InvokeAsync(() => host.LastInitError).GetTask();

    private static Task<string?> ReadCdpEndpointOnUiThreadAsync(WebView2Host host) =>
        Dispatcher.UIThread.InvokeAsync(() => host.CdpEndpoint).GetTask();

    private static Task<HostSnapshot> ReadHostSnapshotOnUiThreadAsync(WebView2Host host) =>
        Dispatcher.UIThread.InvokeAsync(() =>
            new HostSnapshot(host.CdpEndpoint, host.CurrentUrl, host.IsEngineReady, host.LastProcessFailure)).GetTask();

    private sealed record HostSnapshot(string? CdpEndpoint, string? CurrentUrl, bool IsEngineReady, string? ProcessFailure);

    private static async Task<bool> IsHostCdpUsableAsync(WebView2Host host, string? endpoint, CancellationToken ct)
    {
        var snapshot = await ReadHostSnapshotOnUiThreadAsync(host).ConfigureAwait(false);
        if (!snapshot.IsEngineReady || !string.IsNullOrWhiteSpace(snapshot.ProcessFailure) || string.IsNullOrEmpty(endpoint))
            return false;

        return await EmbeddedBrowserCdpProbe.IsReachableAsync(endpoint, ct).ConfigureAwait(false);
    }

    public Task<EmbeddedPublishPrepareResult> PrepareForPublishAsync(
        TikTokAccountProfile account,
        Func<TikTokAccountProfile, AccountItemViewModel?> resolveAccountVm,
        bool bringToFront = false,
        CancellationToken ct = default)
    {
        var accountVm = resolveAccountVm(account);
        if (accountVm is null)
        {
            return Task.FromResult(new EmbeddedPublishPrepareResult(
                false,
                null,
                $"未找到账号视图：{account.DisplayName}"));
        }

        return PrepareForPublishAsync(accountVm, bringToFront, ct);
    }

    public async Task SaveAuthAsync(AccountItemViewModel account, bool auto = false)
    {
        var host = TryGetHost(account.Id);
        if (host is null)
        {
            AuthSaveFailed?.Invoke("内置浏览器尚未打开账号页面。");
            return;
        }

        var currentUrl = host.CurrentUrl ?? "";
        if (EmbeddedBrowserLoginHelper.IsLoginUrl(currentUrl))
        {
            AuthSaveFailed?.Invoke("当前仍在登录页，请登录成功后再保存授权。");
            return;
        }

        AuthStatusChanged?.Invoke(auto ? "检测到登录成功，正在保存授权..." : "正在读取授权...");
        try
        {
            var cookies = await host.GetCookiesAsync().ConfigureAwait(true);
            var rawLocalStorage = await host.ExecuteScriptAsync(EmbeddedBrowserScripts.LocalStorageExport).ConfigureAwait(true);
            var localStorageText = UnwrapScriptResult(rawLocalStorage);
            var localStorageByOrigin = EmbeddedAuthExportService.ParseLocalStorageExport(localStorageText);
            var result = EmbeddedAuthExportService.SaveAuthState(account.Model, cookies, localStorageByOrigin);
            account.Status = AccountStatus.Online;
            account.RefreshFromModel();
            _wasOnLoginPage[account.Id] = false;
            _autofillStates.Remove(account.Id);
            AuthSaved?.Invoke(new EmbeddedAuthSavedEventArgs(account, result));
            AuthStatusChanged?.Invoke($"授权已保存（{result.CookieCount} 个 Cookie）");
        }
        catch (Exception ex)
        {
            AuthSaveFailed?.Invoke(ex.Message);
            AuthStatusChanged?.Invoke("保存失败");
        }
    }

    private async Task OnNavigationCompletedAsync(AccountItemViewModel account, string url)
    {
        if (EmbeddedBrowserLoginHelper.IsLoginUrl(url))
        {
            _wasOnLoginPage[account.Id] = true;
            await ScheduleLoginAutofillAsync(account).ConfigureAwait(true);
            return;
        }

        if (_wasOnLoginPage.TryGetValue(account.Id, out var wasLogin) && wasLogin)
        {
            _wasOnLoginPage[account.Id] = false;
            _autofillStates.Remove(account.Id);
            await SaveAuthAsync(account, auto: true).ConfigureAwait(true);
            return;
        }

        if (_autofillStates.ContainsKey(account.Id))
            _autofillStates.Remove(account.Id);
    }

    private async Task ScheduleLoginAutofillAsync(AccountItemViewModel account)
    {
        if (!_autofillStates.TryGetValue(account.Id, out var state))
            return;

        var host = TryGetHost(account.Id);
        if (host is null) return;

        var delay = _autofillDelaysMs[Math.Min(state.Attempts, _autofillDelaysMs.Length - 1)];
        await Task.Delay(delay).ConfigureAwait(true);
        if (!_autofillStates.ContainsKey(account.Id))
            return;

        state = state with { Attempts = state.Attempts + 1 };
        _autofillStates[account.Id] = state;

        var script = EmbeddedBrowserScripts.BuildLoginAutofillScript(state.Email, state.Password);
        await host.ExecuteScriptAsync(script).ConfigureAwait(true);

        if (state.Attempts < LoginAutofillMaxAttempts
            && _autofillStates.ContainsKey(account.Id)
            && EmbeddedBrowserLoginHelper.IsLoginUrl(host.CurrentUrl))
        {
            _ = ScheduleLoginAutofillAsync(account);
        }
        else
        {
            _autofillStates.Remove(account.Id);
        }
    }

    private async Task ContinueAutofillAsync(AccountItemViewModel account)
    {
        await ScheduleLoginAutofillAsync(account).ConfigureAwait(true);
    }

    private static string? UnwrapScriptResult(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var text = raw.Trim();
        if (text.Length >= 2 && text.StartsWith('"') && text.EndsWith('"'))
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<string>(text); }
            catch { return text.Trim('"'); }
        }
        return text;
    }

    private static async Task<string> TryDeleteProfileDirectoryAsync(string profileDir, CancellationToken ct)
    {
        var fullPath = "";
        try
        {
            fullPath = Path.GetFullPath(profileDir);
            var profilesRoot = Path.GetFullPath(AppPaths.ProfilesRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootWithSeparator = profilesRoot + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                return $"跳过清理非默认账号目录：{fullPath}";

            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(fullPath))
                        Directory.Delete(fullPath, recursive: true);
                    return "";
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        return string.IsNullOrWhiteSpace(fullPath)
            ? "清理账号浏览器会话失败"
            : $"清理账号浏览器会话失败：{fullPath}";
    }

    private sealed record LoginAutofillState(string Email, string Password, int Attempts);
}
