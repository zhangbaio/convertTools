using System.Net;
using System.Text.Json;

namespace PlatformPublisher.Weixin.Publishing;

internal sealed class WeixinSeriesApiSession(
    string finderId, string finderUin, string finderKey, string activityId, CookieContainer cookies)
{
    public string FinderId { get; } = finderId;
    public string FinderUin { get; } = finderUin;
    public string FinderKey { get; } = finderKey;
    public string ActivityId { get; } = activityId;
    public CookieContainer Cookies { get; } = cookies;

    public static async Task<WeixinSeriesApiSession> LoadAsync(string authFile, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(authFile, ct).ConfigureAwait(false));
            var root = document.RootElement;
            var site = new Uri("https://channels.weixin.qq.com");
            var local = root.Get("origins").Items()
                .Where(origin => origin.Text("origin") == site.GetLeftPart(UriPartial.Authority))
                .SelectMany(origin => origin.Get("localStorage").Items())
                .GroupBy(item => item.Text("name"))
                .ToDictionary(group => group.Key, group => group.Last().Text("value"));
            var cookies = new CookieContainer(100, 100, 4096);
            foreach (var entry in root.Get("cookies").Items())
            {
                if (!string.Equals(entry.Text("domain").TrimStart('.'), site.Host, StringComparison.OrdinalIgnoreCase))
                    continue;
                var name = entry.Text("name");
                var path = entry.Text("path");
                if (name.Length == 0 || !path.StartsWith('/'))
                    continue;
                var cookie = new Cookie(name, entry.Text("value"), path) { Secure = true };
                if (entry.Get("expires") is { ValueKind: JsonValueKind.Number } expiration &&
                    expiration.TryGetDouble(out var unix) && unix is > 0 and <= 253402300799)
                    cookie.Expires = DateTimeOffset.FromUnixTimeSeconds((long)unix).UtcDateTime;
                cookies.Add(site, cookie);
            }

            var finder = local.GetValueOrDefault("finder_username", "");
            if (finder.Length > 0 && cookies.Count > 0)
                return new(finder, local.GetValueOrDefault("finder_uin", ""),
                    local.GetValueOrDefault("finder_raw", ""), local.GetValueOrDefault("__rx::aid", ""), cookies);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or CookieException)
        {
            // 登录快照或 Cookie 的解析错误可能带有登录信息。
        }
        throw new InvalidOperationException("当前账号的视频号登录快照缺失或已过期。请在「账号配置」重新扫码并打开视频号后台。");
    }
}
