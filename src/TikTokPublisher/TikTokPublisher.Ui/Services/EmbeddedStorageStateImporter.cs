using System.Text.Json;
using Microsoft.Playwright;

namespace TikTokPublisher.Ui.Services;

/// <summary>将 Playwright storage_state 导入已连接的 CDP 浏览器上下文（用于 WebView2 会话未持久化时）。</summary>
public static class EmbeddedStorageStateImporter
{
    public static async Task<bool> TryImportAsync(
        IBrowserContext context,
        IPage page,
        string authPath,
        Action<string>? log,
        CancellationToken ct = default)
    {
        if (!File.Exists(authPath))
        {
            log?.Invoke("未找到 storage_state 文件，跳过 Cookie 导入。");
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(authPath, ct).ConfigureAwait(false));
            var root = doc.RootElement;
            var cookies = new List<Cookie>();
            if (root.TryGetProperty("cookies", out var cookiesEl) && cookiesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cookiesEl.EnumerateArray())
                {
                    var name = item.GetProperty("name").GetString();
                    var value = item.GetProperty("value").GetString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    cookies.Add(new Cookie
                    {
                        Name = name,
                        Value = value ?? "",
                        Domain = item.TryGetProperty("domain", out var d) ? d.GetString() ?? "" : "",
                        Path = item.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/",
                        Expires = item.TryGetProperty("expires", out var e) && e.TryGetDouble(out var exp) ? (float)exp : -1,
                        HttpOnly = item.TryGetProperty("httpOnly", out var h) && h.GetBoolean(),
                        Secure = item.TryGetProperty("secure", out var s) && s.GetBoolean(),
                        SameSite = ParseSameSite(item.TryGetProperty("sameSite", out var ss) ? ss.GetString() : null),
                    });
                }
            }

            if (cookies.Count > 0)
            {
                await context.AddCookiesAsync(cookies).ConfigureAwait(false);
                log?.Invoke($"已从 storage_state 导入 {cookies.Count} 个 Cookie。");
            }

            if (root.TryGetProperty("origins", out var originsEl) && originsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var originItem in originsEl.EnumerateArray())
                {
                    var origin = originItem.GetProperty("origin").GetString();
                    if (string.IsNullOrWhiteSpace(origin)) continue;
                    if (!originItem.TryGetProperty("localStorage", out var ls) || ls.ValueKind != JsonValueKind.Array)
                        continue;

                    await page.GotoAsync(origin, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 30000,
                    }).ConfigureAwait(false);
                    foreach (var entry in ls.EnumerateArray())
                    {
                        var key = entry.GetProperty("name").GetString();
                        var val = entry.GetProperty("value").GetString() ?? "";
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        await page.EvaluateAsync(
                            """([k, v]) => { try { localStorage.setItem(k, v); } catch {} }""",
                            new object[] { key, val }).ConfigureAwait(false);
                    }
                }
                log?.Invoke("已导入 localStorage。");
            }

            return cookies.Count > 0;
        }
        catch (Exception ex)
        {
            log?.Invoke($"storage_state 导入失败：{ex.Message}");
            return false;
        }
    }

    private static SameSiteAttribute? ParseSameSite(string? value) => value?.Trim() switch
    {
        "Strict" => SameSiteAttribute.Strict,
        "Lax" => SameSiteAttribute.Lax,
        "None" => SameSiteAttribute.None,
        _ => null,
    };
}
