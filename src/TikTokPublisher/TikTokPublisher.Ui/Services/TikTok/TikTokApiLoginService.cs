using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services.TikTok;

internal static class TikTokApiLoginService
{
    private static readonly Uri Origin = new(TikTokApiSites.DomesticOrigin);
    private static readonly Uri LoginPage = new(TikTokUrls.DefaultLoginUrl);
    private const string LoginPath = "/passport/web/user/login/?aid=920662&account_sdk_source=web&sdk_version=2.1.11-tiktok&language=zh";

    public static async Task<Attempt> TryLoginAsync(TikTokAccountProfile account, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var email = account.TiktokLoginEmail.Trim();
        var password = account.TiktokLoginPassword;
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            return new(null, "账号未配置登录邮箱和密码");
        if (TikTokApiSites.IsInternational(account.TiktokApiSite))
            return new(null, "此站点需要使用浏览器登录");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var cookies = new CookieContainer();
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                CookieContainer = cookies,
            };
            if (TikTokProxyHelper.BuildFromAccount(account) is { } proxy)
            {
                handler.Proxy = new WebProxy(proxy.Server)
                {
                    Credentials = string.IsNullOrEmpty(proxy.Username)
                        ? null : new NetworkCredential(proxy.Username, proxy.Password),
                };
            }

            using var client = new HttpClient(handler);
            return await TryLoginAsync(client, cookies, email, password, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(null, "接口登录超时");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or ArgumentException
                                   or InvalidOperationException or NotSupportedException or UriFormatException)
        {
            ct.ThrowIfCancellationRequested();
            return new(null, "接口登录请求失败");
        }
    }

    internal static async Task<Attempt> TryLoginAsync(
        HttpClient client, CookieContainer cookies, string email, string password, CancellationToken ct)
    {
        using var landing = await client.GetAsync(LoginPage, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if ((int)landing.StatusCode == 429 || landing.Headers.RetryAfter is not null)
            return new(null, "TikTok 接口暂时限流");
        if (!landing.IsSuccessStatusCode)
            return new(null, "无法初始化 TikTok 登录会话");

        using var login = new HttpRequestMessage(HttpMethod.Post, new Uri(Origin, LoginPath))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["mix_mode"] = "1",
                ["email"] = EncodeFormValue(email),
                ["password"] = EncodeFormValue(password),
                ["type"] = "13",
                ["email_logic_type"] = "2",
                ["fixed_mix_mode"] = "1",
            }),
        };
        login.Headers.Referrer = LoginPage;
        login.Headers.Add("Origin", TikTokApiSites.DomesticOrigin);
        login.Headers.Accept.ParseAdd("application/json, text/plain, */*");

        using var response = await client.SendAsync(login, ct).ConfigureAwait(false);
        if ((int)response.StatusCode == 429 || response.Headers.RetryAfter is not null)
            return new(null, "TikTok 接口暂时限流");
        if (!response.IsSuccessStatusCode)
            return new(null, "TikTok 登录接口请求失败");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = json.RootElement;
        if (ReadField(root, "message") != "success")
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
            {
                if (ReadField(data, "error_code") == "7")
                    return new(null, "TikTok 接口暂时限流");

                var description = ReadField(data, "description");
                if (description.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
                    description.Contains("verif", StringComparison.OrdinalIgnoreCase) ||
                    description.Contains("验证", StringComparison.Ordinal))
                    return new(null, "TikTok 要求完成验证");
            }

            return new(null, "TikTok 未接受接口登录");
        }

        using var verify = new HttpRequestMessage(HttpMethod.Post, new Uri(Origin, TikTokApiUploadService.SessionVerificationPath))
        {
            Content = JsonContent.Create(new { pageIndex = 1, pageSize = 1 }),
        };
        verify.Headers.Referrer = new Uri(TikTokUrls.DefaultSeriesDraftUrl);
        verify.Headers.Add("Origin", TikTokApiSites.DomesticOrigin);
        using var verified = await client.SendAsync(verify, ct).ConfigureAwait(false);
        if ((int)verified.StatusCode == 429 || verified.Headers.RetryAfter is not null)
            return new(null, "TikTok 接口暂时限流");
        var body = await verified.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!TikTokApiUploadService.IsVerifiedLoginReply((int)verified.StatusCode, body))
            return new(null, "新登录态未通过剧集列表接口校验");

        var state = EmbeddedAuthExportService.BuildPlaywrightStorageState(
            cookies.GetAllCookies().Select(cookie => new EmbeddedBrowserCookie(
                cookie.Name, cookie.Value, cookie.Domain, cookie.Path,
                cookie.Expires == DateTime.MinValue ? -1 : new DateTimeOffset(cookie.Expires).ToUnixTimeSeconds(),
                cookie.HttpOnly, cookie.Secure, "Lax")).ToArray(),
            new Dictionary<string, IReadOnlyDictionary<string, string>>());
        ct.ThrowIfCancellationRequested();
        return state.Cookies.Count == 0
            ? new(null, "接口未返回可保存的登录 Cookie")
            : new(state, "");
    }

    internal static TikTokLoginResult SaveVerifiedLogin(string authPath, string email, PlaywrightStorageState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(authPath)!);
        var temporaryPath = authPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            File.Move(temporaryPath, authPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }

        return new(email, authPath, TikTokUrls.DefaultSeriesListUrl, false, DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
    }

    private static string EncodeFormValue(string value) =>
        Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(value).Select(b => (byte)(b ^ 5)).ToArray()).ToLowerInvariant();

    private static string ReadField(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) &&
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";

    internal sealed record Attempt(PlaywrightStorageState? State, string FailureReason);
}
