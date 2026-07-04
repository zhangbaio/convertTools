using Avalonia.Controls;
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

    public void Attach(Panel container, TextBlock emptyHint)
    {
        _container = container;
        _emptyHint = emptyHint;
    }

    public IReadOnlyDictionary<string, WebView2Host> Hosts => _hosts;

    public event Action<EmbeddedAuthSavedEventArgs>? AuthSaved;
    public event Action<string>? AuthStatusChanged;
    public event Action<string>? AuthSaveFailed;

    public void ShowAccount(AccountItemViewModel? account, bool createIfMissing = true)
    {
        if (_container is null || _emptyHint is null) return;

        foreach (var host in _hosts.Values)
            host.IsVisible = false;

        if (account is null)
        {
            _emptyHint.IsVisible = _hosts.Count == 0;
            return;
        }

        if (_hosts.TryGetValue(account.Id, out var existing))
        {
            existing.IsVisible = true;
            _emptyHint.IsVisible = false;
            return;
        }

        if (!createIfMissing)
        {
            _emptyHint.IsVisible = true;
            return;
        }

        var target = GetOrCreateHost(account);
        target.IsVisible = true;
        _emptyHint.IsVisible = false;
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

        var email = (account.Model.TiktokLoginEmail ?? account.Model.TiktokLastLoginEmail ?? "").Trim();
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
            IsVisible = false,
        };
        ApplyProxySettings(host, account.Model);

        host.NavigationCompleted += url => _ = OnNavigationCompletedAsync(account, url);
        _hosts[account.Id] = host;
        _container.Children.Add(host);
        host.Navigate(MainViewModel.TikTokLoginUrl);
        return host;
    }

    public void InvalidateHostIfNetworkChanged(TikTokAccountProfile account)
    {
        if (!_hosts.TryGetValue(account.Id, out var host))
            return;

        var fingerprint = TikTokProxyHelper.BuildFingerprint(account);
        if (string.Equals(_proxyFingerprints.GetValueOrDefault(account.Id), fingerprint, StringComparison.Ordinal))
            return;

        if (_hosts.Remove(account.Id, out var existing))
        {
            existing.CloseBrowser();
            _container?.Children.Remove(existing);
        }

        AccountBrowserPortAllocator.Release(account.Id);
        _proxyFingerprints.Remove(account.Id);
    }

    private readonly Dictionary<string, string> _proxyFingerprints = new(StringComparer.OrdinalIgnoreCase);

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

        for (var attempt = 0; attempt < 120; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (host.CdpEndpoint is not null) return true;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>为剧集上传准备内置浏览器：创建会话、等待 CDP、校验登录态（默认不切前台）。</summary>
    public async Task<EmbeddedPublishPrepareResult> PrepareForPublishAsync(
        AccountItemViewModel account,
        bool bringToFront = false,
        CancellationToken ct = default)
    {
        account.Model.TiktokLoginBrowserMode = "embedded";
        var host = TryGetHost(account.Id) ?? GetOrCreateHost(account);
        if (bringToFront)
            ShowAccount(account);

        for (var attempt = 0; attempt < 120; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (host.CdpEndpoint is { } endpoint)
            {
                account.Model.TiktokFingerprintBrowserCdpEndpoint = endpoint;
                if (EmbeddedBrowserLoginHelper.IsLoginUrl(host.CurrentUrl))
                {
                    return new EmbeddedPublishPrepareResult(
                        false,
                        endpoint,
                        "账号未登录，请在内置浏览器完成 TikTok 登录后重试");
                }

                return new EmbeddedPublishPrepareResult(true, endpoint, "");
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return new EmbeddedPublishPrepareResult(
            false,
            null,
            "内置浏览器 CDP 未就绪，请打开「浏览器」页等待加载完成");
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
