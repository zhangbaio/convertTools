using Avalonia.Controls;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Controls;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Services;

/// <summary>每账号独立 WebView2，切换仅改可见性（对齐 Python 多账号会话策略）。</summary>
public sealed class BrowserSessionHost
{
    private readonly Dictionary<string, WebView2Host> _hosts = new();
    private Panel? _container;
    private TextBlock? _emptyHint;

    public void Attach(Panel container, TextBlock emptyHint)
    {
        _container = container;
        _emptyHint = emptyHint;
    }

    public IReadOnlyDictionary<string, WebView2Host> Hosts => _hosts;

    public void ShowAccount(AccountItemViewModel? account)
    {
        if (_container is null || _emptyHint is null) return;

        foreach (var host in _hosts.Values)
            host.IsVisible = false;

        if (account is null)
        {
            _emptyHint.IsVisible = _hosts.Count == 0;
            return;
        }

        var target = GetOrCreateHost(account);
        target.IsVisible = true;
        _emptyHint.IsVisible = false;
    }

    public WebView2Host GetOrCreateHost(AccountItemViewModel account)
    {
        if (_hosts.TryGetValue(account.Id, out var existing))
            return existing;

        if (_container is null)
            throw new InvalidOperationException("BrowserSessionHost 尚未 Attach 到容器。");

        var host = new WebView2Host
        {
            UserDataFolder = account.Model.ProfileDir,
            RemoteDebuggingPort = 9222 + _hosts.Count,
            IsVisible = false,
        };
        var proxy = TikTokProxyHelper.BuildFromAccount(account.Model);
        if (proxy is not null)
        {
            host.ProxyServer = proxy.Server;
            host.ProxyUsername = proxy.Username;
            host.ProxyPassword = proxy.Password;
        }
        host.Ready += () => account.Status = AccountStatus.Online;
        _hosts[account.Id] = host;
        _container.Children.Add(host);
        host.Navigate(MainViewModel.TikTokLoginUrl);
        return host;
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

        var warning = await TryDeleteProfileDirectoryAsync(account.Model.ProfileDir, ct).ConfigureAwait(false);
        Directory.CreateDirectory(account.Model.ProfileDir);
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
            await Task.Delay(500, ct);
        }

        return false;
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
}
