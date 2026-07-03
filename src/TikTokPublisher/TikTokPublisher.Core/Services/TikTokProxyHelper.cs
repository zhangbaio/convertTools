using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

/// <summary>构建 WebView2 / Playwright 代理参数（对齐 Python <c>proxy_service.py</c>）。</summary>
public static class TikTokProxyHelper
{
    public sealed record ProxyConfig(string Server, string Username, string Password, string Description);

    public static string BuildFingerprint(TikTokAccountProfile account)
    {
        var proxy = BuildFromAccount(account);
        if (proxy is null) return "direct";
        return $"{proxy.Server}|{proxy.Username}";
    }

    public static ProxyConfig? BuildFromAccount(TikTokAccountProfile account)
    {
        if (!account.TiktokProxyEnabled) return null;
        var host = (account.TiktokProxyHost ?? "").Trim();
        if (string.IsNullOrEmpty(host)) return null;

        var proxyType = NormalizeProxyType(account.TiktokProxyType);
        var port = NormalizePort(account.TiktokProxyPort);
        var server = BuildServer(proxyType, host, port);
        if (string.IsNullOrEmpty(server)) return null;

        var username = (account.TiktokProxyUsername ?? "").Trim();
        var password = account.TiktokProxyPassword ?? "";
        var description = string.IsNullOrWhiteSpace(account.Name) ? server : $"{account.DisplayName} · {server}";
        return new ProxyConfig(server, username, password, description);
    }

    public static string NormalizeProxyType(string? value)
    {
        var text = (value ?? "http").Trim().ToLowerInvariant();
        if (text is "https" or "http/https") return "http";
        return text is "http" or "socks4" or "socks5" ? text : "http";
    }

    private static int NormalizePort(int value) =>
        value is > 0 and <= 65535 ? value : 0;

    private static string BuildServer(string proxyType, string host, int port)
    {
        var normalized = host.Trim();
        if (string.IsNullOrEmpty(normalized)) return "";
        if (normalized.Contains("://", StringComparison.Ordinal)) return normalized;
        return port > 0 ? $"{proxyType}://{normalized}:{port}" : $"{proxyType}://{normalized}";
    }
}
